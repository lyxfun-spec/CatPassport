using CatAssistant.App.Services;
using Microsoft.UI.Xaml;

namespace CatAssistant.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private MainWindow? _window;
    public App() => InitializeComponent();

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var repository = new SqliteRepository();
        await repository.InitializeAsync();
        var settings = await repository.LoadSettingsAsync();
        var secureStore = new SecureApiKeyStore();
        var ble = new BleGateway();
        var coordinator = new AssistantCoordinator(ble, new DashScopeClient(), repository, secureStore, settings);
        _window = new MainWindow(coordinator);
        _window.Activate();
        await coordinator.StartAsync();
    }
}
