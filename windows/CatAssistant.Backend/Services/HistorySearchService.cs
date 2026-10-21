using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CatAssistant.Backend.Models;

namespace CatAssistant.Backend.Services;

public interface ILocalEmbeddingProvider
{
    string ModelId { get; }
    float[] Embed(string text);
}

/// <summary>
/// Dependency-free offline vector fallback. It hashes Unicode word/character n-grams into
/// a normalized vector. A packaged ONNX embedding provider can replace it without changing
/// storage or ranking semantics; failures always leave keyword search available.
/// </summary>
public sealed partial class HashingLocalEmbeddingProvider(int dimensions = 512) : ILocalEmbeddingProvider
{
    public string ModelId => $"local-hash-ngram-v1-{dimensions}";

    public float[] Embed(string text)
    {
        var vector = new float[dimensions];
        foreach (var gram in Ngrams(text))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(gram));
            var index = (int)(BitConverter.ToUInt32(hash, 0) % (uint)dimensions);
            vector[index] += (hash[4] & 1) == 0 ? 1f : -1f;
        }
        var norm = MathF.Sqrt(vector.Sum(value => value * value));
        if (norm > 0)
            for (var index = 0; index < vector.Length; index++) vector[index] /= norm;
        return vector;
    }

    private static IEnumerable<string> Ngrams(string value)
    {
        var normalized = Whitespace().Replace(value.ToLowerInvariant(), " ").Trim();
        foreach (Match match in Words().Matches(normalized))
        {
            var word = match.Value;
            yield return word;
            for (var size = 2; size <= 3; size++)
                for (var index = 0; index + size <= word.Length; index++)
                    yield return word.Substring(index, size);
        }
    }

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
    [GeneratedRegex("[\\p{L}\\p{N}_]+")]
    private static partial Regex Words();
}

public sealed class HistorySearchService(
    SqliteRepository repository,
    ILocalEmbeddingProvider? embeddingProvider = null)
{
    private readonly ILocalEmbeddingProvider _embedding = embeddingProvider ?? new HashingLocalEmbeddingProvider();
    private readonly SemaphoreSlim _indexGate = new(1, 1);

    public async Task<IReadOnlyList<HistorySearchHit>> SearchAsync(
        string conversationId,
        string query,
        int limit = 8,
        string? role = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 20);
        var keyword = await repository.SearchMessagesAsync(conversationId, query, Math.Min(20, limit * 2), role, from, to);
        IReadOnlyList<HistorySearchHit> semantic;
        try
        {
            await EnsureIndexedAsync(conversationId, cancellationToken);
            semantic = await SearchSemanticAsync(conversationId, query, Math.Min(20, limit * 2), role, from, to);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            semantic = [];
        }

        const double rrfK = 60d;
        var merged = new Dictionary<string, (HistorySearchHit Hit, double Score, HashSet<string> Sources)>(StringComparer.Ordinal);
        Add(keyword, "keyword");
        Add(semantic, "vector");
        return merged.Values
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Hit.Sequence)
            .Take(limit)
            .Select(item => item.Hit with { Score = item.Score, MatchSource = string.Join("+", item.Sources.Order()) })
            .ToArray();

        void Add(IReadOnlyList<HistorySearchHit> hits, string source)
        {
            for (var index = 0; index < hits.Count; index++)
            {
                var contribution = 1d / (rrfK + index + 1);
                if (merged.TryGetValue(hits[index].MessageId, out var existing))
                {
                    existing.Sources.Add(source);
                    merged[hits[index].MessageId] = (existing.Hit, existing.Score + contribution, existing.Sources);
                }
                else merged[hits[index].MessageId] = (hits[index], contribution, new HashSet<string>(StringComparer.Ordinal) { source });
            }
        }
    }

    public Task<ConversationMessage?> GetMessageAsync(string conversationId, string messageId) =>
        repository.GetMessageAsync(conversationId, messageId);

    private async Task EnsureIndexedAsync(string conversationId, CancellationToken cancellationToken)
    {
        await _indexGate.WaitAsync(cancellationToken);
        try
        {
            var messages = (await repository.LoadMessagesAsync(conversationId))
                .Where(item => item.Status is "complete" or "interrupted" && item.Role is "user" or "assistant")
                .ToArray();
            var existing = (await repository.LoadMessageEmbeddingsAsync(conversationId, _embedding.ModelId))
                .ToDictionary(item => item.MessageId, StringComparer.Ordinal);
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message.Content)));
                if (existing.TryGetValue(message.Id, out var indexed) && indexed.ContentHash == hash) continue;
                await repository.UpsertMessageEmbeddingAsync(new MessageEmbeddingRecord(
                    message.Id, conversationId, hash, _embedding.ModelId, _embedding.Embed(message.Content), DateTimeOffset.UtcNow));
            }
            await repository.UpdateContextWatermarksAsync(conversationId, search: messages.Length == 0 ? 0 : messages.Max(item => item.Sequence));
        }
        finally { _indexGate.Release(); }
    }

    private async Task<IReadOnlyList<HistorySearchHit>> SearchSemanticAsync(
        string conversationId, string query, int limit, string? role, DateTimeOffset? from, DateTimeOffset? to)
    {
        var queryVector = _embedding.Embed(query);
        var embeddings = await repository.LoadMessageEmbeddingsAsync(conversationId, _embedding.ModelId);
        var messages = (await repository.LoadMessagesAsync(conversationId)).ToDictionary(item => item.Id, StringComparer.Ordinal);
        return embeddings
            .Where(item => messages.ContainsKey(item.MessageId))
            .Select(item => (Embedding: item, Message: messages[item.MessageId], Score: Dot(queryVector, item.Vector)))
            .Where(item => item.Score > 0 && (role is null || item.Message.Role == role) &&
                (!from.HasValue || item.Message.CreatedAt >= from) && (!to.HasValue || item.Message.CreatedAt <= to))
            .OrderByDescending(item => item.Score)
            .Take(limit)
            .Select(item => new HistorySearchHit(item.Message.Id, conversationId, item.Message.Sequence, item.Message.CreatedAt,
                item.Message.Role, item.Message.Status, Snippet(item.Message.Content), item.Score, "vector"))
            .ToArray();
    }

    private static double Dot(float[] left, float[] right)
    {
        var sum = 0d;
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++) sum += left[index] * right[index];
        return sum;
    }

    private static string Snippet(string content) => content.Length <= 320 ? content : content[..320] + "…";
}
