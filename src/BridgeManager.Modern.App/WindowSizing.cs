using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace BridgeManager.Modern;

internal static class WindowSizing
{
    // XAML dimensions are DIPs; AppWindow.Resize takes physical pixels. Keep
    // the same usable layout when Windows scales a per-monitor-aware window.
    internal static void ResizeInDips(Window window, int width, int height, bool fitWorkArea = false)
    {
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var size = new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale));
        if (fitWorkArea)
        {
            var area = DisplayArea.GetFromWindowId(window.AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            size.Width = Math.Min(size.Width, area.Width);
            size.Height = Math.Min(size.Height, area.Height);
            window.AppWindow.MoveAndResize(new RectInt32(
                area.X + (area.Width - size.Width) / 2,
                area.Y + (area.Height - size.Height) / 2,
                size.Width, size.Height));
            return;
        }
        window.AppWindow.Resize(size);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
