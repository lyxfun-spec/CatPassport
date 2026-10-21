using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using CatAssistant.Backend.Models;
using CatAssistant.Backend.Services.Work;

namespace CatAssistant.Backend.Services;

public sealed partial class WorkHistoryExporter(SqliteRepository repository)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ExportAsync(string conversationId, WorkWorkspacePaths workspace, CancellationToken cancellationToken)
    {
        var contextDirectory = Path.Combine(workspace.Work, ".context");
        Directory.CreateDirectory(contextDirectory);
        Directory.CreateDirectory(Path.Combine(contextDirectory, "tool-results"));
        var messages = await repository.LoadMessagesAsync(conversationId);
        var toolCalls = await repository.ListWorkToolCallsAsync(conversationId);
        var summary = await repository.GetActiveSummaryAsync(conversationId);
        var watermark = messages.Count == 0 ? 0 : messages.Max(item => item.Sequence);

        var historyPath = Path.Combine(contextDirectory, "history.jsonl");
        var temporaryPath = historyPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            foreach (var message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    type = "message",
                    id = message.Id,
                    conversationId = message.ConversationId,
                    sequence = message.Sequence,
                    role = message.Role,
                    status = message.Status,
                    createdAt = message.CreatedAt,
                    runId = message.RunId,
                    content = message.Content
                }, JsonOptions));
            }
            foreach (var tool in toolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    type = "tool",
                    id = tool.Id,
                    conversationId = tool.ConversationId,
                    runId = tool.RunId,
                    toolCallId = tool.ToolCallId,
                    ordinal = tool.Ordinal,
                    name = tool.ToolName,
                    state = tool.State,
                    arguments = RedactJson(tool.ArgumentsJson),
                    result = RedactJson(tool.ResultJson),
                    errorCode = tool.ErrorCode,
                    errorMessage = tool.ErrorMessage,
                    createdAt = tool.CreatedAt,
                    completedAt = tool.CompletedAt
                }, JsonOptions));
            }
        }
        File.Move(temporaryPath, historyPath, true);
        await using var historyStream = new FileStream(historyPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var historyHash = Convert.ToHexString(await SHA256.HashDataAsync(historyStream, cancellationToken));

        var scriptPath = Path.Combine(contextDirectory, "search_history.py");
        if (!File.Exists(scriptPath) || await File.ReadAllTextAsync(scriptPath, cancellationToken) != SearchScript)
            await File.WriteAllTextAsync(scriptPath, SearchScript, new UTF8Encoding(false), cancellationToken);
        await repository.UpdateContextWatermarksAsync(conversationId, search: watermark, export: watermark);
        var state = await repository.GetContextStateAsync(conversationId);

        var statusPath = Path.Combine(contextDirectory, "status.json");
        var statusTemporaryPath = statusPath + ".tmp";
        await File.WriteAllTextAsync(statusTemporaryPath, JsonSerializer.Serialize(new
        {
            conversationId,
            exportedAt = DateTimeOffset.UtcNow,
            watermark,
            authoritativeWatermark = watermark,
            stale = false,
            contextVersion = state.Version,
            activeSummaryVersion = summary?.Version,
            historySha256 = historyHash,
            formatVersion = 1
        }, JsonOptions), new UTF8Encoding(false), cancellationToken);
        File.Move(statusTemporaryPath, statusPath, true);

    }

    public async Task<WorkToolResult> ExternalizeToolResultAsync(
        WorkWorkspacePaths workspace,
        string runId,
        WorkModelToolCall call,
        WorkToolResult result,
        CancellationToken cancellationToken)
    {
        const int inlineLimit = 12_000;
        if (result.OutputJson.Length <= inlineLimit) return result;
        var directory = Path.Combine(workspace.Work, ".context", "tool-results");
        Directory.CreateDirectory(directory);
        var safeId = InvalidFileName().Replace($"{runId}-{call.ToolCallId}", "_");
        var relativePath = Path.Combine("work", ".context", "tool-results", $"{safeId}.json").Replace('\\', '/');
        var fullPath = Path.Combine(workspace.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var temporaryPath = fullPath + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, result.OutputJson, new UTF8Encoding(false), cancellationToken);
        File.Move(temporaryPath, fullPath, true);
        var preview = result.OutputJson[..Math.Min(2_000, result.OutputJson.Length)];
        return result with
        {
            OutputJson = JsonSerializer.Serialize(new
            {
                stored = true,
                path = relativePath,
                characters = result.OutputJson.Length,
                preview,
                note = "完整工具结果已保存到工作区文件；需要更多内容时按路径读取。"
            }, JsonOptions)
        };
    }

    private static object? RedactJson(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        try
        {
            using var document = JsonDocument.Parse(value);
            return Redact(document.RootElement);
        }
        catch (JsonException) { return value; }
    }

    private static object? Redact(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(
            property => property.Name,
            property => SensitiveName(property.Name) ? (object?)"[REDACTED]" : Redact(property.Value),
            StringComparer.OrdinalIgnoreCase),
        JsonValueKind.Array => value.EnumerateArray().Select(Redact).ToArray(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var number) ? number : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static bool SensitiveName(string name) =>
        name.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("apikey", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("access_token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("[^A-Za-z0-9._-]", RegexOptions.CultureInvariant)]
    private static partial Regex InvalidFileName();

    private const string SearchScript = """
#!/usr/bin/env python3
import argparse, hashlib, json, math, pathlib, re, sys
from datetime import datetime

ROOT = pathlib.Path(__file__).resolve().parent
HISTORY = ROOT / "history.jsonl"
STATUS = ROOT / "status.json"

def grams(text):
    normalized = re.sub(r"\s+", " ", text.lower()).strip()
    words = re.findall(r"[\w\u3400-\u9fff]+", normalized)
    result = set(words)
    for word in words:
        if len(word) > 1:
            result.update(word[i:i+2] for i in range(len(word)-1))
        if len(word) > 2:
            result.update(word[i:i+3] for i in range(len(word)-2))
    return result

def vector(text, dimensions=512):
    values = {}
    for gram in grams(text):
        digest = hashlib.sha256(gram.encode("utf-8")).digest()
        index = int.from_bytes(digest[:4], "little") % dimensions
        values[index] = values.get(index, 0.0) + (1.0 if digest[4] & 1 else -1.0)
    norm = math.sqrt(sum(value * value for value in values.values())) or 1.0
    return {key: value / norm for key, value in values.items()}

def cosine(left, right):
    return sum(value * right.get(key, 0.0) for key, value in left.items())

def main():
    parser = argparse.ArgumentParser(description="Search the exported history copy for this Work conversation.")
    parser.add_argument("query", nargs="?", help="natural-language or exact-text query")
    parser.add_argument("--message-id")
    parser.add_argument("--role", choices=["user", "assistant"])
    parser.add_argument("--from", dest="from_time", help="inclusive ISO-8601 start time")
    parser.add_argument("--to", dest="to_time", help="inclusive ISO-8601 end time")
    parser.add_argument("--top-k", type=int, default=8)
    parser.add_argument("--full", action="store_true")
    args = parser.parse_args()
    if not HISTORY.exists():
        raise SystemExit("history copy is missing; ask the application to refresh the Work workspace")
    history_bytes = HISTORY.read_bytes()
    status = json.loads(STATUS.read_text("utf-8")) if STATUS.exists() else {"stale": True}
    if status.get("historySha256") != hashlib.sha256(history_bytes).hexdigest().upper(): status["stale"] = True
    rows = [json.loads(line) for line in history_bytes.decode("utf-8").splitlines() if line.strip()]
    messages = [row for row in rows if row.get("type") == "message"]
    if args.message_id:
        matches = [row for row in messages if row.get("id") == args.message_id]
        print(json.dumps({"status": status, "results": matches}, ensure_ascii=False, indent=2))
        return
    if not args.query:
        parser.error("query or --message-id is required")
    query_terms, query_vector = grams(args.query), vector(args.query)
    try:
        from_time = datetime.fromisoformat(args.from_time.replace("Z", "+00:00")) if args.from_time else None
        to_time = datetime.fromisoformat(args.to_time.replace("Z", "+00:00")) if args.to_time else None
    except ValueError as error:
        raise SystemExit(f"invalid ISO-8601 time: {error}")
    ranked = []
    for row in messages:
        if args.role and row.get("role") != args.role: continue
        created = datetime.fromisoformat(row.get("createdAt", "").replace("Z", "+00:00"))
        if from_time and created < from_time: continue
        if to_time and created > to_time: continue
        content = row.get("content", "")
        terms = grams(content)
        keyword = len(query_terms & terms) / max(1, len(query_terms))
        semantic = cosine(query_vector, vector(content))
        if keyword <= 0 and semantic <= 0: continue
        score = 0.6 * keyword + 0.4 * max(0.0, semantic)
        item = dict(row)
        item["score"] = round(score, 6)
        item["matchSource"] = "keyword+local_vector"
        if not args.full and len(content) > 320: item["content"] = content[:320] + "…"
        ranked.append(item)
    ranked.sort(key=lambda row: (row["score"], row.get("sequence", 0)), reverse=True)
    print(json.dumps({"status": status, "results": ranked[:max(1, min(args.top_k, 20))]}, ensure_ascii=False, indent=2))

if __name__ == "__main__": main()
""";
}
