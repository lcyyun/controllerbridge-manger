using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BridgeManager.Modern;

public partial class App : Application
{
    private Window? _window;
    private DispatcherQueueTimer? _launchSmokeTimer;

    public App()
    {
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
        try
        {
            await ((MainWindow)_window!).RunShellSmokeAsync(outputDirectory);
        }
        catch (Exception ex)
        {
            Directory.CreateDirectory(outputDirectory);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "error.txt"), ex.ToString());
            Environment.ExitCode = 1;
        }
        finally
        {
            _window!.Close();
            Exit();
        }
    }
}
