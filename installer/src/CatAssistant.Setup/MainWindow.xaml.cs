using System.Diagnostics;
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CatAssistant.Setup.Services;
using Microsoft.Win32;

namespace CatAssistant.Setup;

public partial class MainWindow : Window
{
    private readonly InstallerLog log = new();
    private readonly Dictionary<WizardPage, FrameworkElement> pages = new();
    private WizardPage currentPage = WizardPage.Welcome;
    private bool checksPassed;
    private bool installationStarted;
    private bool installationSucceeded;
    private int installPercent;
    private InstallResult? result;
    private readonly Version packageVersion;
    private CancellationTokenSource? locationCheckCancellation;
    private bool locationCheckPassed;
    private bool allowWindowClose;

    public MainWindow()
    {
        InitializeComponent();
        var applicationVersion = GetApplicationVersion();
        packageVersion = Version.TryParse(applicationVersion, out var parsedVersion) ? parsedVersion : new Version(1, 0, 0);
        AppVersionText.Text = applicationVersion;
        InstallPath.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CatAssistant");
        pages[WizardPage.Welcome] = WelcomePage;
        pages[WizardPage.AppVersion] = VersionPage;
        pages[WizardPage.Check] = CheckPage;
        pages[WizardPage.Options] = OptionsPage;
        pages[WizardPage.Installing] = InstallingPage;
        pages[WizardPage.Complete] = CompletePage;
        log.Write("Setup UI started.");
        UpdateNavigation();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        switch (currentPage)
        {
            case WizardPage.Welcome:
                ShowPage(WizardPage.AppVersion);
                break;
            case WizardPage.AppVersion:
                if (!CanContinueForInstalledVersion()) return;
                ShowPage(WizardPage.Check);
                await RunChecksAsync();
                break;
            case WizardPage.Check:
                if (checksPassed)
                {
                    ShowPage(WizardPage.Options);
                    await ValidateInstallLocationAsync(false);
                }
                break;
            case WizardPage.Options:
                await ValidateInstallLocationAsync(false);
                if (!locationCheckPassed) return;
                ShowPage(WizardPage.Installing);
                await RunInstallationAsync();
                break;
            case WizardPage.Complete:
                if (installationSucceeded && LaunchAfterInstall.IsChecked == true) LaunchInstalledApplication();
                Close();
                break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (installationStarted) return;
        var previous = currentPage switch
        {
            WizardPage.AppVersion => WizardPage.Welcome,
            WizardPage.Check => WizardPage.AppVersion,
            WizardPage.Options => WizardPage.Check,
            _ => currentPage
        };
        ShowPage(previous);
    }

    private void ShowPage(WizardPage page)
    {
        foreach (var element in pages.Values) element.Visibility = Visibility.Collapsed;
        currentPage = page;
        var target = pages[page];
        target.Visibility = Visibility.Visible;

        if (!SystemParameters.ClientAreaAnimation)
        {
            target.Opacity = 1;
            target.RenderTransform = Transform.Identity;
            UpdateNavigation();
            return;
        }

        target.Opacity = 0;
        var transform = new TranslateTransform(18, 0);
        target.RenderTransform = transform;
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(290))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        UpdateNavigation();
    }

    private async Task RunChecksAsync()
    {
        CheckResultsPanel.Children.Clear();
        CheckSummary.Visibility = Visibility.Collapsed;
        AddCheckCard("正在检查", "请稍候…", null);
        var results = await EnvironmentCheckService.RunAsync(MsiInstaller.HasPayload);
        CheckResultsPanel.Children.Clear();
        foreach (var check in results) AddCheckCard(check.Name, check.Detail, check.Passed);
        checksPassed = results.All(item => item.Passed || !item.Blocking);
        CheckSummary.Visibility = checksPassed ? Visibility.Visible : Visibility.Collapsed;
        log.Write($"Environment checks completed. passed={checksPassed}; {string.Join("; ", results.Select(x => $"{x.Name}={x.Passed}"))}");
        UpdateNavigation();
    }

    private void AddCheckCard(string name, string detail, bool? passed)
    {
        var color = passed switch { true => "#4E7663", false => "#A94F49", null => "#657080" };
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        panel.ColumnDefinitions.Add(new ColumnDefinition());

        var badge = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(9),
            Background = BrushFrom(passed == false ? "#FBEFEE" : passed == true ? "#E8F4EE" : "#F2F5F7"),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        badge.Child = passed switch
        {
            null => new TextBlock { Text = "…", Foreground = BrushFrom(color), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            true => CreateCheckMark(color),
            false => new TextBlock { Text = "!", Foreground = BrushFrom(color), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        texts.Children.Add(new TextBlock { Text = detail, Foreground = BrushFrom("#657080"), FontSize = 10, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(texts, 1);
        panel.Children.Add(badge);
        panel.Children.Add(texts);

        var card = new Border
        {
            Width = 374,
            MinHeight = 76,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(14),
            BorderBrush = BrushFrom("#E4E9ED"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Background = Brushes.White,
            Child = panel
        };
        CheckResultsPanel.Children.Add(card);
    }

    private async Task RunInstallationAsync()
    {
        installationStarted = true;
        UpdateNavigation();
        SetInstallProgress(new InstallationProgress(0, "正在准备安装资源…"));
        StartProgressAnimation();
        var options = new InstallOptions(InstallPath.Text, DesktopShortcut.IsChecked == true, StartMenuShortcut.IsChecked == true, LaunchAfterInstall.IsChecked == true);
        log.Write($"Installation requested. directory={options.InstallDirectory}; desktopShortcut={options.DesktopShortcut}; startMenuShortcut={options.StartMenuShortcut}");
        var installer = new MsiInstaller(log);
        var progress = new Progress<InstallationProgress>(SetInstallProgress);
        result = await installer.InstallAsync(options, progress, CancellationToken.None);
        installationSucceeded = result.Succeeded;
        if (result.Succeeded) SetInstallProgress(new InstallationProgress(100, "安装完成"));
        CompleteTitle.Text = result.Succeeded ? "欢迎回来，小猫助手。" : result.Cancelled ? "安装已取消" : "安装未完成";
        CompleteMessage.Text = result.Message;
        LogPathText.Text = result.LogPath;
        LogPanel.Visibility = result.Succeeded ? Visibility.Collapsed : Visibility.Visible;
        ShowPage(WizardPage.Complete);
    }

    private void StartProgressAnimation()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            ProgressShimmer.Opacity = 0.25;
            return;
        }

        var transform = new TranslateTransform(-230, 0);
        ProgressShimmer.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-230, Math.Max(900, ProgressTrack.ActualWidth + 160), TimeSpan.FromSeconds(1.55))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    private void UpdateNavigation()
    {
        BackButton.Visibility = currentPage is WizardPage.AppVersion or WizardPage.Check or WizardPage.Options ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = currentPage == WizardPage.Installing ? Visibility.Collapsed : Visibility.Visible;
        NextButton.IsEnabled = currentPage switch
        {
            WizardPage.Check => checksPassed,
            WizardPage.Options => locationCheckPassed,
            _ => true
        };
        NextButton.Content = currentPage switch
        {
            WizardPage.Options => "开始安装",
            WizardPage.Complete => installationSucceeded && LaunchAfterInstall.IsChecked == true ? "启动小猫助手" : "完成",
            _ => "继续"
        };
    }

    private async void RetryChecks_Click(object sender, RoutedEventArgs e) => await RunChecksAsync();
    private async void InstallPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) await ValidateInstallLocationAsync(true);
    }

    private async Task ValidateInstallLocationAsync(bool debounce)
    {
        locationCheckCancellation?.Cancel();
        locationCheckCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        locationCheckCancellation = cancellation;
        locationCheckPassed = false;
        LocationStatusBorder.Background = BrushFrom("#F2F5F7");
        LocationStatusText.Foreground = BrushFrom("#657080");
        LocationStatusText.Text = "正在检查安装位置…";
        UpdateNavigation();

        try
        {
            if (debounce) await Task.Delay(250, cancellation.Token);
            var check = await EnvironmentCheckService.CheckInstallLocationAsync(InstallPath.Text, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            locationCheckPassed = check.IsUsable;
            LocationStatusBorder.Background = BrushFrom(check.IsUsable ? "#E8F4EE" : "#FBEFEE");
            LocationStatusText.Foreground = BrushFrom(check.IsUsable ? "#46715F" : "#A94F49");
            LocationStatusText.Text = check.Message;
            UpdateNavigation();
        }
        catch (OperationCanceledException)
        {
            // A newer path value is already being checked.
        }
    }

    private void ProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateProgressFill();

    private void SetInstallProgress(InstallationProgress progress)
    {
        installPercent = Math.Clamp(progress.Percent, 0, 100);
        InstallStatus.Text = progress.Status;
        ProgressPercentText.Text = $"{installPercent}%";
        UpdateProgressFill();
    }

    private void UpdateProgressFill()
    {
        if (ProgressTrack.ActualWidth <= 0) return;
        ProgressFill.Width = ProgressTrack.ActualWidth * installPercent / 100d;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var current = InstallPath.Text.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var currentLeaf = Path.GetFileName(current);
        var initialDirectory = currentLeaf.Equals("CatAssistant", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(current)?.FullName
            : current;
        if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory))
            initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var dialog = new OpenFolderDialog
        {
            Title = "选择小猫助手安装位置",
            InitialDirectory = initialDirectory
        };
        if (dialog.ShowDialog(this) != true) return;

        var selected = dialog.FolderName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        InstallPath.Text = Path.GetFileName(selected).Equals("CatAssistant", StringComparison.OrdinalIgnoreCase)
            ? selected
            : Path.Combine(selected, "CatAssistant");
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var path = result?.LogPath ?? log.Path;
        if (Path.GetDirectoryName(path) is not null)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private void LaunchInstalledApplication()
    {
        var executable = Path.Combine(InstallPath.Text, "CatAssistant.exe");
        if (File.Exists(executable))
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = InstallPath.Text });
    }

    private bool CanContinueForInstalledVersion()
    {
        var state = InstalledProductService.GetState(packageVersion);
        log.Write($"Installed product check. package={packageVersion}; highestInstalled={state.HighestInstalledVersion}; disposition={state.Disposition}");
        if (state.Disposition != VersionInstallDisposition.BlockNewerVersion) return true;

        MessageBox.Show(
            this,
            $"电脑中已经安装了更高版本的小猫助手（{state.HighestInstalledVersion}）。\n\n当前安装包版本为 {packageVersion}，为避免意外降级，本次安装将取消。",
            "无法安装较旧版本",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
        Close();
        return false;
    }

    private void RootShell_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RootShell.Clip = new RectangleGeometry(new Rect(0, 0, RootShell.ActualWidth, RootShell.ActualHeight), 20, 20);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (installationStarted && currentPage == WizardPage.Installing)
        {
            ShowInstallationInProgressMessage();
            return;
        }
        allowWindowClose = true;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (allowWindowClose || !installationStarted || currentPage != WizardPage.Installing) return;
        e.Cancel = true;
        ShowInstallationInProgressMessage();
    }

    private void ShowInstallationInProgressMessage()
    {
        MessageBox.Show(
            this,
            "小猫助手正在安装，暂时无法关闭安装程序。请等待安装完成。",
            "安装正在进行",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private static SolidColorBrush BrushFrom(string color) => new((Color)ColorConverter.ConvertFromString(color));

    private static string GetApplicationVersion() =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "CatAssistantAppVersion")?.Value ?? "1.0.0";

    private static System.Windows.Shapes.Path CreateCheckMark(string color) => new()
    {
        Data = Geometry.Parse("M 4,9 L 8,13 L 16,5"),
        Stroke = BrushFrom(color),
        StrokeThickness = 2,
        StrokeStartLineCap = PenLineCap.Flat,
        StrokeEndLineCap = PenLineCap.Flat,
        StrokeLineJoin = PenLineJoin.Miter,
        Stretch = Stretch.Uniform,
        Width = 16,
        Height = 16,
        SnapsToDevicePixels = true,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
}
