using System.Security.Cryptography;
using System.Text;

namespace BridgeManager.Core;

// An OS-held file handle, not a PID marker: process exit/crash automatically
// releases ownership. No thread affinity, so async HID disposal is safe.
public sealed class ManagerResourceLease : IDisposable
{
    private readonly FileStream _handle;
    private ManagerResourceLease(FileStream handle) => _handle = handle;

    public static bool TryAcquire(string resource, out ManagerResourceLease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ControllerBridge", "locks");
        Directory.CreateDirectory(root);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resource.ToUpperInvariant())));
        try
        {
            lease = new ManagerResourceLease(new FileStream(Path.Combine(root, key + ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
            return true;
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            lease = null;
            return false;
        }
    }

    public void Dispose() => _handle.Dispose();
}

public sealed class ManagerChannelBusyException : IOException
{
    public ManagerChannelBusyException() : base(
        "接收器管理通道已被另一个 Manager 或调试工具占用。请关闭其他管理连接后重试。") { }
}
