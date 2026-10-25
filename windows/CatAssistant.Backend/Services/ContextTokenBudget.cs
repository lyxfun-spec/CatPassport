using System.Text;
using System.Text.Json;
using CatAssistant.Backend.Models;

namespace CatAssistant.Backend.Services;

public interface ITokenEstimator
{
    TokenEstimate Estimate(string payload);
}

/// <summary>
/// Conservative offline fallback for providers that do not expose a tokenizer/counting API.
/// It deliberately over-counts CJK, JSON punctuation and UTF-8 bytes rather than treating
/// unknown content as free. Actual provider usage is recorded separately for calibration.
/// </summary>
public sealed class ConservativeTokenEstimator : ITokenEstimator
{
    public TokenEstimate Estimate(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return new TokenEstimate(0, "conservative_utf8", true, 0, 0);

        var asciiRun = 0;
        var tokens = 0;
        foreach (var rune in payload.EnumerateRunes())
        {
            if (rune.Value <= 0x7f && (char.IsLetterOrDigit((char)rune.Value) || rune.Value is '_' or '-'))
            {
                asciiRun++;
                continue;
            }

            tokens += FlushAscii(asciiRun);
            asciiRun = 0;
            tokens += rune.Value <= 0x7f && char.IsWhiteSpace((char)rune.Value) ? 0 : 1;
        }
        tokens += FlushAscii(asciiRun);

        var bytes = Encoding.UTF8.GetByteCount(payload);
        // JSON/message framing and tokenizer variance are covered by a 12.5% guard.
        tokens = Math.Max(tokens, (int)Math.Ceiling(bytes / 4d));
        tokens = Math.Max(1, (int)Math.Ceiling(tokens * 1.125d));
        return new TokenEstimate(tokens, "conservative_utf8", true, payload.Length, bytes);
    }

    private static int FlushAscii(int characters) => characters == 0 ? 0 : (int)Math.Ceiling(characters / 3.5d);
}

public sealed class ContextTokenBudget(ITokenEstimator? estimator = null)
{
    private readonly ITokenEstimator _estimator = estimator ?? new ConservativeTokenEstimator();

    public ContextBudgetAssessment Assess<T>(T actualRequestPayload, ModelContextProfile profile)
    {
        var payload = JsonSerializer.Serialize(actualRequestPayload);
        var estimate = _estimator.Estimate(payload);
        var utilization = estimate.Tokens / (double)profile.InputBudgetTokens;
        return new ContextBudgetAssessment(
            profile,
            estimate,
            utilization,
            utilization >= profile.SoftThreshold,
            estimate.Tokens > profile.InputBudgetTokens);
    }
}

public static class ModelContextProfiles
{
    // Work responses include complete file contents and tool arguments in the output budget.
    public static ModelContextProfile ResolveWork(string model) => Resolve(model, 8_192);

    public static ModelContextProfile Resolve(string model, int? outputReserveTokens = null)
    {
        var normalized = model.Trim().ToLowerInvariant();
        var window = normalized switch
        {
            var value when value.Contains("qwen3.8", StringComparison.Ordinal) => 131_072,
            var value when value.Contains("qwen-plus", StringComparison.Ordinal) => 131_072,
            var value when value.Contains("qwen-max", StringComparison.Ordinal) => 32_768,
            _ => 32_768
        };
        var output = Math.Clamp(outputReserveTokens ?? 4_096, 512, Math.Max(512, window / 4));
        var safety = Math.Max(1_024, window / 20);
        return new ModelContextProfile(model, window, output, safety);
    }
}
