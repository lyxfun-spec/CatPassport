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
        AssistantCoordinator? coordinator = null;
        RpcServer? server = null;
        try
        {
            var repository = new SqliteRepository();
            await repository.InitializeAsync();
            coordinator = new AssistantCoordinator(new BleGateway(), new DashScopeClient(), repository, new SecureApiKeyStore(), await repository.LoadSettingsAsync());
            var rpcServer = new RpcServer(Console.In, Console.Out, request => HandleAsync(coordinator, request));
            server = rpcServer;
            coordinator.StateChanged += (_, state) => _ = rpcServer.PublishAsync("device.changed", state);
            coordinator.Conversations.Changed += (_, update) => _ = rpcServer.PublishAsync(update.Type, update);
            await coordinator.StartAsync();
            await rpcServer.PublishAsync("backend.ready", new { version = RpcServer.ProtocolVersion, state = await BootstrapAsync(coordinator) });
            await rpcServer.RunAsync();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Backend fatal error: {error}");
            return 1;
        }
        finally
        {
            if (coordinator is not null) await coordinator.DisposeAsync();
            if (server is not null) await server.DisposeAsync();
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
            case "conversations.start":
                return await coordinator.Conversations.StartAsync(
                    Optional<string>(request.Params, "mode") ?? "chat",
                    Required<string>(request.Params, "text"),
                    "desktop");
            case "conversations.rename":
                await coordinator.Conversations.RenameAsync(Required<string>(request.Params, "id"), Required<string>(request.Params, "title"));
                return await coordinator.Conversations.GetAsync(Required<string>(request.Params, "id"));
            case "conversations.pin":
                await coordinator.Conversations.SetPinnedAsync(Required<string>(request.Params, "id"), Required<bool>(request.Params, "pinned"));
                return await BootstrapAsync(coordinator);
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
            case "work.workspace.get":
                return await coordinator.Conversations.GetWorkWorkspaceAsync(Required<string>(request.Params, "id"));
            case "work.inputs.add":
                return new { path = await coordinator.Conversations.CopyWorkInputAsync(
                    Required<string>(request.Params, "id"), Required<string>(request.Params, "sourcePath")) };
            case "work.approvals.list":
                return await coordinator.Conversations.ListPendingWorkApprovalsAsync(Required<string>(request.Params, "id"));
            case "work.approvals.resolve":
                await coordinator.Conversations.ResolveWorkApprovalAsync(
                    Required<string>(request.Params, "id"), Required<string>(request.Params, "approvalId"), Required<bool>(request.Params, "approved"));
                return await coordinator.Conversations.ListPendingWorkApprovalsAsync(Required<string>(request.Params, "id"));
            case "work.requests.resolve":
                return await coordinator.Conversations.ResolveWorkRequestAsync(
                    Required<string>(request.Params, "requestId"), Required<bool>(request.Params, "approved"));
            case "work.artifacts.revealPath":
                return new { path = await coordinator.Conversations.GetWorkArtifactRevealPathAsync(
                    Required<string>(request.Params, "id"), Required<string>(request.Params, "artifactId")) };
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
                    Required<string>(request.Params, "workModel"),
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
        var isWork = active?.Conversation.Mode == "work";
        var approvals = isWork ? await coordinator.Conversations.ListPendingWorkApprovalsAsync(activeId!) : [];
        var toolCalls = isWork ? await coordinator.Conversations.ListWorkToolCallsAsync(activeId!) : [];
        var artifacts = isWork ? await coordinator.Conversations.ListWorkArtifactsAsync(activeId!) : [];
        return new BootstrapState(conversations, activeId, active, coordinator.Snapshot(), coordinator.PublicSettings(), approvals, toolCalls, artifacts);
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
