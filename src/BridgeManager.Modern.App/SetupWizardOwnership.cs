using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace BridgeManager.Modern;

internal static class SetupWizardOwnership
{
    // An owned window stays above its Manager owner, not above unrelated apps.
    internal static void SetOwner(Window wizard, Window manager)
    {
        var wizardHandle = WinRT.Interop.WindowNative.GetWindowHandle(wizard);
        var managerHandle = WinRT.Interop.WindowNative.GetWindowHandle(manager);
        SetWindowLongPtr(wizardHandle, -8, managerHandle);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);
}
