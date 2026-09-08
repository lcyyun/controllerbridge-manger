namespace BridgeManager.Core;

public static class ManagerProtocol
{
    public const byte FeatureReportId = 0x7f;
    public const byte DualSenseFeatureReportId = 0xf6;
    public const byte NintendoCommandOutputReportId = 0x02;
    public const int FeatureReportLength = 64;
    public const int CommandPayloadLength = 63;
    public const string SetMagic = "Y7HID1";
    public const string ReplyMagic = "Y7HRS1";
}
