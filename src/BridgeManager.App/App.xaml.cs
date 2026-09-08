using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BridgeManager.App;

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
}
