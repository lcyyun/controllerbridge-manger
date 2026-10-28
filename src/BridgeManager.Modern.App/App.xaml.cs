using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using BridgeManager.Core;

namespace BridgeManager.Modern;

public partial class App : Application
{
    private Window? _window;
    private DispatcherQueueTimer? _launchSmokeTimer;
    private ManagerResourceLease? _instanceLease;

    public App()
    {
        UnhandledException += (_, args) => AppDiagnostics.Write("xaml-unhandled", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error) AppDiagnostics.Write("unhandled", error);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            AppDiagnostics.Write("unobserved-task", args.Exception);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var commandLine = Environment.GetCommandLineArgs();
        var shellSmoke = Array.IndexOf(commandLine, "--shell-smoke");
        if (shellSmoke >= 0)
        {
            if (shellSmoke + 1 >= commandLine.Length) { Environment.ExitCode = 2; Exit(); return; }
            _window = new MainWindow(discoverDevices: false);
            _window.Activate();
            _ = RunShellSmokeAsync(commandLine[shellSmoke + 1]);
            return;
        }
        var mappingSmoke = Array.IndexOf(commandLine, "--mapping-smoke");
        if (mappingSmoke >= 0)
        {
            if (mappingSmoke + 1 >= commandLine.Length)
            {
                Environment.ExitCode = 2;
                Exit();
                return;
            }
            _window = new Window { Title = "Mapping UI Test - simulated transport" };
            _window.Activate();
            _ = RunMappingSmokeAsync(commandLine[mappingSmoke + 1]);
            return;
        }
        // Offline render fixtures above never discover/open hardware. All
        // interactive launches share one guard across folders and versions.
        if (!ManagerResourceLease.TryAcquire("manager-interactive-ui", out _instanceLease))
        {
            ShowAlreadyRunning();
            return;
        }
        _window = new MainWindow();
        _window.Activate();

        if (Environment.GetCommandLineArgs()
            .Contains("--launch-smoke", StringComparer.OrdinalIgnoreCase))
        {
            _launchSmokeTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _launchSmokeTimer.Interval = TimeSpan.FromSeconds(20);
            _launchSmokeTimer.IsRepeating = false;
            _launchSmokeTimer.Tick += (_, _) => Exit();
            _launchSmokeTimer.Start();
        }
    }

    private void ShowAlreadyRunning()
    {
        var close = new Button { Content = "关闭此提示", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => { _window!.Close(); Exit(); };
        var panel = new StackPanel { Padding = new Thickness(24), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = "ControllerBridge Manager 已在运行", FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "请使用已打开的窗口。重复启动会争用接收器管理回复，导致报错和反复重连。此窗口未连接设备。",
            TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(close);
        _window = new Window { Title = "ControllerBridge Manager — 已在运行", Content = panel };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(500, 260));
        _window.Activate();
    }

    private async Task RunMappingSmokeAsync(string outputDirectory)
    {
        try
        {
            await DynamicModulePageRenderer.RunSmokeTestsAsync(_window!, outputDirectory);
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "error.txt"), ex.ToString());
            Environment.ExitCode = 1;
        }
        finally { Exit(); }
    }

    private async Task RunShellSmokeAsync(string outputDirectory)
    {
        var passed = false;
        try
        {
            await ((MainWindow)_window!).RunShellSmokeAsync(outputDirectory);
            passed = true;
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "error.txt"), ex.ToString());
            Environment.ExitCode = 1;
        }
        finally
        {
            if (!passed || !Environment.GetCommandLineArgs().Contains("--keep-open", StringComparer.Ordinal))
            {
                _window!.Close();
                Exit();
            }
        }
    }
}
