using System.Text.Json;
using CatAssistant.Backend.Ipc;
using CatAssistant.Backend.Services;

namespace CatAssistant.Backend;

internal static class Program
{
    private static async Task<int> Main()
    {
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var repository = new SqliteRepository();
        await repository.InitializeAsync();
        var coordinator = new AssistantCoordinator(new BleGateway(), new DashScopeClient(), repository, new SecureApiKeyStore(), await repository.LoadSettingsAsync());
        RpcServer? server = null;
        server = new RpcServer(Console.In, Console.Out, request => HandleAsync(coordinator, request));
        coordinator.StateChanged += (_, state) => _ = server.PublishAsync("state.changed", state);
        coordinator.StreamChanged += (_, update) => _ = server.PublishAsync("conversation.stream", update);
        try
        {
            await coordinator.StartAsync();
            await server.PublishAsync("backend.ready", new { version = RpcServer.ProtocolVersion, state = coordinator.Snapshot() });
            await server.RunAsync();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Backend fatal error: {error}");
            return 1;
        }
        finally
        {
            await coordinator.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    private static async Task<object?> HandleAsync(AssistantCoordinator coordinator, RpcRequest request)
    {
        switch (request.Method)
        {
            case "state.get": return coordinator.Snapshot();
            case "devices.scan": await coordinator.ScanAsync(); return coordinator.Snapshot();
            case "devices.connect":
                await coordinator.ConnectAsync(Required<ulong>(request.Params, "address"), Optional<string>(request.Params, "name"));
                return coordinator.Snapshot();
            case "devices.forget": await coordinator.ForgetAsync(); return coordinator.Snapshot();
            case "conversation.new": await coordinator.NewConversationAsync(); return coordinator.Snapshot();
            case "conversation.cancel": await coordinator.CancelCurrentTurnAsync(); return coordinator.Snapshot();
            case "conversation.send": await coordinator.SendDesktopMessageAsync(Required<string>(request.Params, "text")); return coordinator.Snapshot();
            case "settings.save":
                await coordinator.SaveSettingsAsync(
                    Optional<string>(request.Params, "apiKey"),
                    Required<string>(request.Params, "asrModel"),
                    Required<string>(request.Params, "chatModel"),
                    Required<uint>(request.Params, "screenTimeoutSeconds"),
                    Required<bool>(request.Params, "autoReconnect"));
                return coordinator.Snapshot();
            case "app.shutdown": return new { shuttingDown = true };
            default: throw new ArgumentException($"未知 IPC 方法：{request.Method}");
        }
    }

    private static T Required<T>(JsonElement? parameters, string name)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property)) throw new ArgumentException($"缺少参数 {name}。");
        return property.Deserialize<T>() ?? throw new ArgumentException($"参数 {name} 无效。");
    }

    private static T? Optional<T>(JsonElement? parameters, string name)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return default;
        return property.Deserialize<T>();
    }
}
