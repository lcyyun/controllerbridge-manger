using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace BridgeManager.Modern;

public sealed partial class MainWindow
{
    internal async Task VerifyDpiSmokeAsync(string output)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        SmokeCapture.Require(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(hwnd), new IntPtr(-4)),
            "Executable must opt into PerMonitorV2 before creating its window.");
        WindowSizing.ResizeInDips(this, 1000, 720);
        await Task.Delay(100);
        var root = (FrameworkElement)Content;
        SmokeCapture.Require(Math.Abs(root.XamlRoot.RasterizationScale - scale) < 0.001 &&
            AppWindow.Size.Width == (int)Math.Round(1000 * scale) &&
            AppWindow.Size.Height == (int)Math.Round(720 * scale),
            "Window sizing or XAML rasterization does not match the monitor's DPI.");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "dpi-result.json"),
            JsonSerializer.Serialize(new { result = "PASS", perMonitorV2 = true, dpi,
                root.XamlRoot.RasterizationScale, pixelWidth = AppWindow.Size.Width,
                pixelHeight = AppWindow.Size.Height }));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

    // Manual/native-input companion to the automated layout checks. Uses the
    // actual page tree while hardware discovery and all polling stay disabled.
    internal void PrepareWheelSmoke(string output)
    {
        SmokeCapture.Require(!_discoverDevices, "Wheel smoke must not discover hardware.");
        var root = (FrameworkElement)Content;
        if (!root.IsLoaded)
        {
            RoutedEventHandler? loaded = null;
            loaded = (_, _) => { root.Loaded -= loaded; PrepareWheelSmoke(output); };
            root.Loaded += loaded;
            return;
        }
        Directory.CreateDirectory(output);
        Title = "Wheel and DPI regression - offline";
        _inputWatchdog.Stop();
        _managerInputTimer.Stop();
        _pollTimer.Stop();
        ActivateModule(_moduleRegistry.Find("bl616-unified")!);
        WindowSizing.ResizeInDips(this, 1000, 720);
        MainNavigation.SelectedItem = HomeNavItem;
        var path = Path.Combine(output, "wheel-events.jsonl");
        void Log(object entry) => File.AppendAllText(path, JsonSerializer.Serialize(entry) + "\n");
        Log(new { kind = "ready", pid = Environment.ProcessId, root.XamlRoot.RasterizationScale });
        root.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, args) =>
        {
            var point = args.GetCurrentPoint(root);
            Log(new { kind = "wheel", point.Properties.MouseWheelDelta, args.Handled,
                source = args.OriginalSource?.GetType().Name,
                name = (args.OriginalSource as FrameworkElement)?.Name });
        }), handledEventsToo: true);
        foreach (var page in new[] { HomePage, InputPage, FeedbackPage, FirmwarePage, AdvancedPage, DynamicModulePage })
            page.ViewChanged += (_, args) => Log(new { kind = "view", page.Name,
                page.VerticalOffset, page.ScrollableHeight, args.IsIntermediate });
        root.SizeChanged += (_, _) => Log(new { kind = "size", root.ActualWidth, root.ActualHeight,
            root.XamlRoot.RasterizationScale });
    }
}
