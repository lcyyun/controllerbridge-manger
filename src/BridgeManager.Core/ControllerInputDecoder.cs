namespace BridgeManager.Core;

public sealed record ControllerInputSnapshot(
    string Source,
    uint Buttons,
    int LeftX,
    int LeftY,
    int RightX,
    int RightY,
    int LeftTrigger,
    int RightTrigger,
    int AccelX,
    int AccelY,
    int AccelZ,
    int GyroX,
    int GyroY,
    int GyroZ,
    bool MotionValid,
    int? BatteryPercent);

public static class ControllerInputDecoder
{
    public const byte DualSenseReportId = 0x01;
    public const byte NintendoReportId = 0x05;

    public static bool TryDecode(byte reportId,
                                 ReadOnlySpan<byte> report,
                                 out ControllerInputSnapshot? snapshot)
    {
        snapshot = null;
        if (report.Length > 0 && report[0] == reportId &&
            ((reportId == DualSenseReportId && report.Length == 64) ||
             (reportId == NintendoReportId && report.Length == 64)))
        {
            report = report[1..];
        }

        snapshot = reportId switch
        {
            DualSenseReportId => DecodeDualSense(report),
            NintendoReportId => DecodeNintendo(report),
            _ => null
        };
        return snapshot is not null;
    }

    private static ControllerInputSnapshot? DecodeDualSense(ReadOnlySpan<byte> body)
    {
        if (body.Length < 53)
        {
            return null;
        }

        uint buttons = 0;
        var b7 = body[7];
        var b8 = body[8];
        var b9 = body[9];
        DecodeDpad((byte)(b7 & 0x0f), ref buttons);
        Set(ref buttons, 2, (b7 & 0x10) != 0);
        Set(ref buttons, 0, (b7 & 0x20) != 0);
        Set(ref buttons, 1, (b7 & 0x40) != 0);
        Set(ref buttons, 3, (b7 & 0x80) != 0);
        Set(ref buttons, 8, (b8 & 0x01) != 0);
        Set(ref buttons, 9, (b8 & 0x02) != 0);
        Set(ref buttons, 10, (b8 & 0x04) != 0);
        Set(ref buttons, 11, (b8 & 0x08) != 0);
        Set(ref buttons, 12, (b8 & 0x10) != 0);
        Set(ref buttons, 13, (b8 & 0x20) != 0);
        Set(ref buttons, 14, (b8 & 0x40) != 0);
        Set(ref buttons, 15, (b8 & 0x80) != 0);
        Set(ref buttons, 16, (b9 & 0x01) != 0);
        Set(ref buttons, 17, (b9 & 0x02) != 0);
        Set(ref buttons, 18, (b9 & 0x04) != 0);
        Set(ref buttons, 22, (b9 & 0x10) != 0);
        Set(ref buttons, 23, (b9 & 0x20) != 0);
        Set(ref buttons, 20, (b9 & 0x40) != 0);
        Set(ref buttons, 21, (b9 & 0x80) != 0);

        var batteryLevel = body[52] & 0x0f;
        var accelX = ReadInt16(body, 21);
        var accelY = ReadInt16(body, 23);
        var accelZ = ReadInt16(body, 25);
        var gyroX = ReadInt16(body, 15);
        var gyroY = ReadInt16(body, 19);
        var gyroZ = ReadInt16(body, 17);
        var motionValid = accelX != 0 || accelY != 0 || accelZ != 0 ||
                          gyroX != 0 || gyroY != 0 || gyroZ != 0;
        return new ControllerInputSnapshot(
            "DS5 USB input", buttons,
            AxisFromByte(body[0]), AxisFromByte(body[1]),
            AxisFromByte(body[2]), AxisFromByte(body[3]),
            body[4] * 257, body[5] * 257,
            accelX, accelY, accelZ, gyroX, gyroY, gyroZ,
            motionValid, batteryLevel <= 10 ? batteryLevel * 10 : null);
    }

    private static ControllerInputSnapshot? DecodeNintendo(ReadOnlySpan<byte> body)
    {
        if (body.Length < 60)
        {
            return null;
        }

        uint buttons = 0;
        var face = body[4];
        var center = body[5];
        var left = body[6];
        var paddles = body[7];
        Set(ref buttons, 2, (face & 0x01) != 0);
        Set(ref buttons, 3, (face & 0x02) != 0);
        Set(ref buttons, 0, (face & 0x04) != 0);
        Set(ref buttons, 1, (face & 0x08) != 0);
        Set(ref buttons, 9, (face & 0x40) != 0);
        Set(ref buttons, 11, (face & 0x80) != 0);
        Set(ref buttons, 12, (center & 0x01) != 0);
        Set(ref buttons, 13, (center & 0x02) != 0);
        Set(ref buttons, 15, (center & 0x04) != 0);
        Set(ref buttons, 14, (center & 0x08) != 0);
        Set(ref buttons, 16, (center & 0x10) != 0);
        Set(ref buttons, 19, (center & 0x20) != 0);
        Set(ref buttons, 24, (center & 0x40) != 0);
        Set(ref buttons, 5, (left & 0x01) != 0);
        Set(ref buttons, 4, (left & 0x02) != 0);
        Set(ref buttons, 7, (left & 0x04) != 0);
        Set(ref buttons, 6, (left & 0x08) != 0);
        Set(ref buttons, 8, (left & 0x40) != 0);
        Set(ref buttons, 10, (left & 0x80) != 0);
        Set(ref buttons, 21, (paddles & 0x01) != 0);
        Set(ref buttons, 20, (paddles & 0x02) != 0);

        var (leftX, leftY) = Unpack12(body, 10);
        var (rightX, rightY) = Unpack12(body, 13);
        var accelX = ReadInt16(body, 48);
        var accelY = ReadInt16(body, 50);
        var accelZ = ReadInt16(body, 52);
        var gyroX = ReadInt16(body, 54);
        var gyroY = ReadInt16(body, 56);
        var gyroZ = ReadInt16(body, 58);
        var motionValid = accelX != 0 || accelY != 0 || accelZ != 0 ||
                          gyroX != 0 || gyroY != 0 || gyroZ != 0;
        return new ControllerInputSnapshot(
            "NS2 USB input", buttons,
            AxisFrom12(leftX), AxisFrom12(leftY),
            AxisFrom12(rightX), AxisFrom12(rightY),
            (left & 0x80) != 0 ? ushort.MaxValue : 0,
            (face & 0x80) != 0 ? ushort.MaxValue : 0,
            accelX, accelY, accelZ, gyroX, gyroY, gyroZ,
            motionValid, null);
    }

    private static void DecodeDpad(byte dpad, ref uint buttons)
    {
        Set(ref buttons, 4, dpad is 0 or 1 or 7);
        Set(ref buttons, 7, dpad is 1 or 2 or 3);
        Set(ref buttons, 5, dpad is 3 or 4 or 5);
        Set(ref buttons, 6, dpad is 5 or 6 or 7);
    }

    private static void Set(ref uint buttons, int bit, bool pressed)
    {
        if (pressed)
        {
            buttons |= 1u << bit;
        }
    }

    private static (int X, int Y) Unpack12(ReadOnlySpan<byte> data, int offset) =>
        (data[offset] | ((data[offset + 1] & 0x0f) << 8),
         (data[offset + 1] >> 4) | (data[offset + 2] << 4));

    private static short ReadInt16(ReadOnlySpan<byte> data, int offset) =>
        (short)(data[offset] | (data[offset + 1] << 8));

    private static int AxisFromByte(byte value) => value >= 128
        ? (int)(((long)(value - 128) * 32767 + 63) / 127)
        : -(int)(((long)(128 - value) * 32768 + 64) / 128);

    private static int AxisFrom12(int value) => value >= 2048
        ? (int)(((long)(value - 2048) * 32767 + 1023) / 2047)
        : -(int)(((long)(2048 - value) * 32768 + 1024) / 2048);
}
