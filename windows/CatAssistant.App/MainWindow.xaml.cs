using CatAssistant.App.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace CatAssistant.App;

public sealed partial class MainWindow : Window
{
    private readonly TrayIconService _tray;
    private bool _allowClose;
    public AssistantCoordinator Coordinator { get; }

    public MainWindow(AssistantCoordinator coordinator)
    {
        Coordinator = coordinator;
        InitializeComponent();
        ScreenTimeoutBox.Value = coordinator.Settings.ScreenTimeoutSeconds;
        AutoReconnectBox.IsChecked = coordinator.Settings.AutoReconnect;
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this));
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(980, 680));
        appWindow.Closing += (_, args) => { if (!_allowClose) { args.Cancel = true; appWindow.Hide(); } };
        _tray = new TrayIconService("小猫助手", ShowWindow, ExitApplication);
    }

    private void ShowWindow() { AppWindow.Show(); Activate(); }
    private async void ExitApplication()
    {
        _allowClose = true; _tray.Dispose(); await Coordinator.DisposeAsync(); Close();
    }

    private void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        ConnectionPanel.Visibility = Visibility.Collapsed; StatusPanel.Visibility = Visibility.Collapsed; SettingsPanel.Visibility = Visibility.Collapsed;
        if (args.IsSettingsSelected) SettingsPanel.Visibility = Visibility.Visible;
        else if ((args.SelectedItemContainer?.Tag as string) == "status") StatusPanel.Visibility = Visibility.Visible;
        else ConnectionPanel.Visibility = Visibility.Visible;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await Coordinator.ScanAsync();
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is CatDevice device) await Coordinator.ConnectAsync(device);
    }
    private async void Forget_Click(object sender, RoutedEventArgs e) => await Coordinator.ForgetAsync();
    private async void NewConversation_Click(object sender, RoutedEventArgs e) => await Coordinator.NewConversationAsync();
    private async void CancelTurn_Click(object sender, RoutedEventArgs e) => await Coordinator.CancelCurrentTurnAsync();
    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var timeout = double.IsNaN(ScreenTimeoutBox.Value) ? 60u : (uint)ScreenTimeoutBox.Value;
        await Coordinator.SaveSettingsAsync(ApiKeyBox.Password, AsrModelBox.Text, ChatModelBox.Text, timeout, AutoReconnectBox.IsChecked == true);
        ApiKeyBox.Password = "";
    }
}
