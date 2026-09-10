using System.Diagnostics;

namespace BridgeManager.Modern;

internal static class AppDiagnostics
{
    private static readonly object Gate = new();
    internal static void Write(string context, Exception error)
    {
        try
        {
            lock (Gate)
            {
                var directory = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), "ControllerBridge", "logs");
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "manager-errors.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path,
                    $"[{DateTimeOffset.Now:O}] {context} | {Environment.ProcessPath}\n{error}\n");
            }
        }
        catch (Exception loggingError) { Debug.WriteLine(loggingError); }
    }
}
