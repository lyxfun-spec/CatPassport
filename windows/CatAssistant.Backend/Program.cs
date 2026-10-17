using System.Text.Json;
using CatAssistant.Backend.Ipc;
using CatAssistant.Backend.Models;
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
        coordinator.StateChanged += (_, state) => _ = server.PublishAsync("device.changed", state);
        coordinator.Conversations.Changed += (_, update) => _ = server.PublishAsync(update.Type, update);
        try
        {
            await coordinator.StartAsync();
            await server.PublishAsync("backend.ready", new { version = RpcServer.ProtocolVersion, state = await BootstrapAsync(coordinator) });
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
            case "app.bootstrap":
            case "state.get": return await BootstrapAsync(coordinator);
            case "conversations.list": return await coordinator.Conversations.ListAsync();
            case "conversations.get": return await coordinator.Conversations.GetAsync(Required<string>(request.Params, "id"));
            case "conversations.create":
                return await coordinator.Conversations.CreateAsync(Optional<string>(request.Params, "mode") ?? "chat");
            case "conversations.rename":
                await coordinator.Conversations.RenameAsync(Required<string>(request.Params, "id"), Required<string>(request.Params, "title"));
                return await coordinator.Conversations.GetAsync(Required<string>(request.Params, "id"));
            case "conversations.delete":
                await coordinator.Conversations.DeleteAsync(Required<string>(request.Params, "id"));
                return await BootstrapAsync(coordinator);
            case "conversations.activate":
                await coordinator.Conversations.ActivateDesktopAsync(Required<string>(request.Params, "id"));
                return await coordinator.Conversations.GetAsync(Required<string>(request.Params, "id"));
            case "conversations.send":
                return await coordinator.Conversations.SendAsync(
                    Required<string>(request.Params, "id"), Required<string>(request.Params, "text"), "desktop");
            case "conversations.cancel":
                await coordinator.Conversations.CancelAsync(Required<string>(request.Params, "id"));
                return await coordinator.Conversations.GetAsync(Required<string>(request.Params, "id"));
            case "devices.scan": await coordinator.ScanAsync(); return coordinator.Snapshot();
            case "devices.connect":
                await coordinator.ConnectAsync(Required<ulong>(request.Params, "address"), Optional<string>(request.Params, "name"));
                return coordinator.Snapshot();
            case "devices.forget": await coordinator.ForgetAsync(); return coordinator.Snapshot();
            case "devices.followDesktop":
                await coordinator.Conversations.FollowDesktopAsync();
                return coordinator.Snapshot();
            case "conversation.new": await coordinator.NewConversationAsync(); return await BootstrapAsync(coordinator);
            case "conversation.cancel": await coordinator.CancelCurrentTurnAsync(); return await BootstrapAsync(coordinator);
            case "conversation.send": await coordinator.SendDesktopMessageAsync(Required<string>(request.Params, "text")); return await BootstrapAsync(coordinator);
            case "settings.save":
                await coordinator.SaveSettingsAsync(
                    Optional<string>(request.Params, "apiKey"),
                    Required<string>(request.Params, "asrModel"),
                    Required<string>(request.Params, "chatModel"),
                    Required<uint>(request.Params, "screenTimeoutSeconds"),
                    Required<bool>(request.Params, "autoReconnect"),
                    Required<bool>(request.Params, "webSearchEnabled"));
                return coordinator.PublicSettings();
            case "app.shutdown": return new { shuttingDown = true };
            default: throw new ArgumentException($"未知 IPC 方法：{request.Method}");
        }
    }

    private static async Task<BootstrapState> BootstrapAsync(AssistantCoordinator coordinator)
    {
        var conversations = await coordinator.Conversations.ListAsync();
        var activeId = coordinator.Conversations.DesktopActiveConversationId;
        var active = activeId is null ? null : await coordinator.Conversations.GetAsync(activeId);
        return new BootstrapState(conversations, activeId, active, coordinator.Snapshot(), coordinator.PublicSettings());
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
