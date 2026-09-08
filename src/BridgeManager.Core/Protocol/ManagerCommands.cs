namespace BridgeManager.Core.Protocol;

public static class ManagerCommands
{
    public const string Status = "status";
    public const string BridgeStatus = "bridge status";
    public const string BridgeInput = "bridge input";
    public const string UsbStatus = "usb status";
    public const string Settings = "settings";
    public const string MotionStatus = "motion status";
    public const string Ns2Status = "ns2 status";
    public const string RumbleStatus = "rumble";
    public const string SaveSettings = "settings save";

    public static string SetRole(string role) => $"role set {role}";

    public static string SetRole(BridgeUsbRole role) => SetRole(role.CommandValue());

    public static string SetInput(string source) => $"input set {source}";

    public static string SetInput(BridgeInputPreference source) => SetInput(source.CommandValue());

    public static string Pair(string source) => $"pair {source}";

    public static string Connect(string source) => $"connect {source}";

    public static string Disconnect(string source) => $"disconnect {source}";

    public static string Forget(string source) => $"forget {source}";

    public static string RumbleTune(int scalePercent, int holdMs, int tickMs, int stopPackets) =>
        $"rumble tune {scalePercent} {holdMs} {tickMs} {stopPackets}";

    public static string UsbRate(int reportHz) => $"usb rate {reportHz}";

    public static string UsbRaw(bool enabled) => $"usb raw {(enabled ? "on" : "off")}";

    public static string WebParse(bool enabled) => $"web parse {(enabled ? "on" : "off")}";

    public static string RumbleTest(string target) => $"rumble test {target}";
}
