using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace BridgeManager.Modern;

internal static class SmokeCapture
{
    internal static async Task SaveAsync(FrameworkElement element, string output, string name)
    {
        await Task.Delay(650);
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || pixels.Distinct().Count() < 10)
            throw new InvalidOperationException("UI smoke rendered a blank surface.");
        Directory.CreateDirectory(output);
        var path = Path.GetFullPath(Path.Combine(output, name));
        using (File.Create(path)) { }
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
    }

    internal static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
