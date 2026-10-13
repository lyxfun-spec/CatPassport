using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CatAssistant.Backend.Ipc;

public sealed record RpcRequest(int Version, long Id, string Method, JsonElement? Params);

public sealed class RpcServer : IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Func<RpcRequest, Task<object?>> _handler;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, Task> _requests = new();

    public RpcServer(TextReader input, TextWriter output, Func<RpcRequest, Task<object?>> handler)
    {
        _input = input; _output = output; _handler = handler;
    }

    public async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested && await _input.ReadLineAsync(_stop.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var task = HandleLineAsync(line);
                _requests[task.Id] = task.Task;
                _ = task.Task.ContinueWith(_ => _requests.TryRemove(task.Id, out var removed), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        await Task.WhenAll(_requests.Values);
    }

    private (long Id, Task Task) HandleLineAsync(string line)
    {
        RpcRequest? request = null;
        try { request = JsonSerializer.Deserialize<RpcRequest>(line, JsonOptions); }
        catch (JsonException) { }
        var id = request?.Id ?? -Interlocked.Increment(ref _invalidId);
        return (id, HandleRequestAsync(request));
    }
    private long _invalidId;

    private async Task HandleRequestAsync(RpcRequest? request)
    {
        if (request is null) { await WriteAsync(new { kind = "response", id = (long?)null, error = new { code = "invalid_json", message = "请求不是有效 JSON。" } }); return; }
        if (request.Version != ProtocolVersion) { await WriteErrorAsync(request.Id, "unsupported_version", $"仅支持 IPC v{ProtocolVersion}。"); return; }
        try
        {
            var result = await _handler(request);
            await WriteAsync(new { kind = "response", id = request.Id, result });
            if (request.Method == "app.shutdown") _stop.Cancel();
        }
        catch (OperationCanceledException) { await WriteErrorAsync(request.Id, "cancelled", "操作已取消。"); }
        catch (Exception error) { await WriteErrorAsync(request.Id, ErrorCode(error), error.Message); }
    }

    public Task PublishAsync(string eventName, object data) => WriteAsync(new { kind = "event", @event = eventName, data });
    private Task WriteErrorAsync(long id, string code, string message) => WriteAsync(new { kind = "response", id, error = new { code, message } });
    private static string ErrorCode(Exception error) => error.GetType().Name switch
    {
        "LinkException" => "ble_error", "CloudException" => "cloud_error", "ArgumentException" => "invalid_argument", _ => "backend_error"
    };

    private async Task WriteAsync(object payload)
    {
        var line = JsonSerializer.Serialize(payload, JsonOptions);
        await _writeLock.WaitAsync();
        try { await _output.WriteLineAsync(line); await _output.FlushAsync(); }
        finally { _writeLock.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested) _stop.Cancel();
        _stop.Dispose(); _writeLock.Dispose(); await Task.CompletedTask;
    }
}
