using System.Buffers.Binary;
using Windows.Gaming.Input;

namespace BridgeManager.Core;

internal static class LocalControllerInputConverter
{
    internal static ControllerInputSnapshot FromGamepad(GamepadReading reading)
    {
        uint buttons = 0;
        Add(GamepadButtons.A, 0);
        Add(GamepadButtons.B, 1);
        Add(GamepadButtons.X, 2);
        Add(GamepadButtons.Y, 3);
        Add(GamepadButtons.DPadUp, 4);
        Add(GamepadButtons.DPadDown, 5);
        Add(GamepadButtons.DPadLeft, 6);
        Add(GamepadButtons.DPadRight, 7);
        Add(GamepadButtons.LeftShoulder, 8);
        Add(GamepadButtons.RightShoulder, 9);
        Add(GamepadButtons.View, 12);
        Add(GamepadButtons.Menu, 13);
        Add(GamepadButtons.LeftThumbstick, 14);
        Add(GamepadButtons.RightThumbstick, 15);
        var leftTrigger = Trigger(reading.LeftTrigger);
        var rightTrigger = Trigger(reading.RightTrigger);
        // WGI has no separate digital trigger switches. Use nonzero travel.
        if (leftTrigger > 0) buttons |= 1u << 10;
        if (rightTrigger > 0) buttons |= 1u << 11;

        return new ControllerInputSnapshot(
            "Windows Gamepad input (local)", buttons,
            Axis(reading.LeftThumbstickX), Axis(reading.LeftThumbstickY),
            Axis(reading.RightThumbstickX), Axis(reading.RightThumbstickY),
            leftTrigger, rightTrigger, 0, 0, 0, 0, 0, 0, false, null);

        void Add(GamepadButtons flag, int bit)
        {
            if ((reading.Buttons & flag) != 0) buttons |= 1u << bit;
        }
    }

    internal static int Axis(double value)
    {
        RequireFinite(value);
        value = Math.Clamp(value, -1, 1);
        return (int)Math.Round(value * (value < 0 ? 32768 : 32767),
            MidpointRounding.AwayFromZero);
    }

    internal static int Trigger(double value)
    {
        RequireFinite(value);
        return (int)Math.Round(Math.Clamp(value, 0, 1) * 65535,
            MidpointRounding.AwayFromZero);
    }

    internal static bool TryDecodeDualSense(
        ReadOnlySpan<byte> report, bool isEdge,
        out ControllerInputSnapshot? snapshot, out string? error)
    {
        snapshot = null;
        error = null;
        scoped ReadOnlySpan<byte> body;
        var simple = false;
        string source;
        Span<byte> expanded = stackalloc byte[63];
        expanded.Clear();

        if (report.Length == 64 && report[0] == 0x01)
        {
            body = report[1..];
            source = "DS5 USB input (local)";
        }
        else if (report.Length == 78 && report[0] == 0x31)
        {
            // Bluetooth adds a sequence/tag byte before the common state.
            if (!HasValidBluetoothCrc(report))
            {
                error = "DualSense Bluetooth input failed its CRC check.";
                return false;
            }
            body = report.Slice(2, 63);
            source = "DS5 Bluetooth input (local)";
        }
        else if (report.Length is 10 or 78 && report[0] == 0x01)
        {
            // Windows can pad the 10-byte Bluetooth simple report to 78 bytes.
            report.Slice(1, 4).CopyTo(expanded);
            expanded[4] = report[8];
            expanded[5] = report[9];
            expanded[7] = report[5];
            expanded[8] = report[6];
            // The remaining six bits are a counter, not mute/Edge buttons.
            expanded[9] = (byte)(report[7] & 0x03);
            body = expanded;
            simple = true;
            source = "DS5 Bluetooth simple input (local; no motion)";
        }
        else
        {
            error = report.IsEmpty
                ? "DualSense returned an empty input report."
                : $"Unsupported DualSense input report 0x{report[0]:X2} ({report.Length} bytes).";
            return false;
        }

        if (!ControllerInputDecoder.TryDecode(0x01, body, out var decoded) ||
            decoded is null)
        {
            error = "DualSense input report is incomplete.";
            return false;
        }

        snapshot = decoded with
        {
            Source = source,
            Buttons = isEdge ? decoded.Buttons : decoded.Buttons & ((1u << 19) - 1),
            LeftY = UpAxis(body[1]),
            RightY = UpAxis(body[3]),
            // Native IMU fields are X/Y/Z; the bridge decoder exposes Y/Z swapped.
            GyroY = simple ? 0 : BinaryPrimitives.ReadInt16LittleEndian(body[17..]),
            GyroZ = simple ? 0 : BinaryPrimitives.ReadInt16LittleEndian(body[19..]),
            MotionValid = !simple,
            BatteryPercent = simple ? null : Battery(body[52])
        };
        return true;
    }

    private static int UpAxis(byte value) => value <= 128
        ? ((128 - value) * 32767 + 64) / 128
        : -((value - 128) * 32768 + 63) / 127;

    private static int? Battery(byte status)
    {
        var level = status & 0x0f;
        return (status >> 4) switch
        {
            0 or 1 when level <= 10 => Math.Min(level * 10 + 5, 100),
            2 => 100,
            _ => null
        };
    }

    private static bool HasValidBluetoothCrc(ReadOnlySpan<byte> report)
    {
        uint crc = uint.MaxValue;
        Update(0xa1); // HIDP input header participates in the CRC.
        foreach (var value in report[..^4]) Update(value);
        return ~crc == BinaryPrimitives.ReadUInt32LittleEndian(report[^4..]);

        void Update(byte value)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
    }

    private static void RequireFinite(double value)
    {
        if (!double.IsFinite(value))
            throw new InvalidDataException("Windows returned a non-finite controller axis.");
    }
}
