using System.Text.Json;

namespace BridgeManager.Core;

public sealed record ManagerInputFrame(ControllerInputSnapshot Input,
    BridgePhysicalInput Source, uint? SensorTimestamp, uint? InputEpoch = null, uint? SourceSequence = null)
{
    public uint? SampleIdentity => SensorTimestamp ?? SourceSequence;
}

/// <summary>Decodes the physical bridge-input snapshot used by management-only USB roles.</summary>
public static class ManagerInputDecoder
{
    public static bool TryDecode(JsonElement root, out ManagerInputFrame? frame)
    {
        frame = null;
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (root.TryGetProperty("input_snapshot_schema", out _) || Text(root, "platform") == "esp32s3")
            return TryDecodeShortNames(root, out frame);
        if (root.ValueKind != JsonValueKind.Object ||
            ReadBool(root, "valid") != true || ReadBool(root, "stale") == true ||
            ReadBool(root, "snapshot_ok") == false || ReadBool(root, "ok") == false ||
            !root.TryGetProperty("source", out var sourceValue) || sourceValue.ValueKind != JsonValueKind.String)
            return false;
        var source = BridgeRoles.ParsePhysicalInput(sourceValue.GetString());
        if (!TryReadEpoch(root, out var epoch)) return false;
        if (source is not (BridgePhysicalInput.DualSense or BridgePhysicalInput.NintendoNs2Pro) ||
            !root.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Number ||
            !buttons.TryGetUInt32(out var mask) || mask >= (1u << 25) ||
            !ReadRange(root, "left_x", short.MinValue, short.MaxValue, out var lx) ||
            !ReadRange(root, "left_y", short.MinValue, short.MaxValue, out var ly) ||
            !ReadRange(root, "right_x", short.MinValue, short.MaxValue, out var rx) ||
            !ReadRange(root, "right_y", short.MinValue, short.MaxValue, out var ry) ||
            !ReadRange(root, "left_trigger", 0, ushort.MaxValue, out var lt) ||
            !ReadRange(root, "right_trigger", 0, ushort.MaxValue, out var rt)) return false;

        var motion = ReadBool(root, "motion_valid") == true;
        var axes = new int[6];
        var names = new[] { "accel_x", "accel_y", "accel_z", "gyro_x", "gyro_y", "gyro_z" };
        if (motion)
            for (var i = 0; i < names.Length; i++)
                if (!ReadRange(root, names[i], short.MinValue, short.MaxValue, out axes[i])) return false;
        uint? timestamp = null;
        if (ReadBool(root, "timestamp_valid") == true)
        {
            if (!root.TryGetProperty("sensor_timestamp", out var time) ||
                time.ValueKind != JsonValueKind.Number || !time.TryGetUInt32(out var value)) return false;
            timestamp = value;
        }
        int? battery = ReadBool(root, "battery_valid") == true &&
            ReadRange(root, "battery_percent", 0, 100, out var percent) ? percent : null;
        var label = source == BridgePhysicalInput.DualSense ? "DS5 physical input" : "NS2Pro physical input";
        frame = new(new ControllerInputSnapshot(label, mask, lx, ly, rx, ry, lt, rt,
            axes[0], axes[1], axes[2], axes[3], axes[4], axes[5], motion, battery), source, timestamp, epoch);
        return true;
    }

    private static bool TryDecodeShortNames(JsonElement root, out ManagerInputFrame? frame)
    {
        frame = null;
        if (ReadBool(root, "ok") == false || ReadBool(root, "input_valid") != true ||
            ReadBool(root, "input_stale") != false || ReadBool(root, "valid") == false ||
            !TryReadEpoch(root, out var epoch) || epoch is null) return false;

        var sourceName = Text(root, "active_input");
        var source = BridgeRoles.ParsePhysicalInput(sourceName);
        if (sourceName is not ("ds5" or "ns2pro")) return false;
        var pico = root.TryGetProperty("input_snapshot_schema", out _);
        uint sequence;
        if (pico)
        {
            // Pico explicitly versions its canonical, pre-mapping snapshot.
            if (Text(root, "profile") != "pico-unified" ||
                !ReadRange(root, "input_snapshot_schema", 1, 1, out _) ||
                Text(root, "axis_format") != "signed16" || Text(root, "button_format") != "canonical" ||
                !ReadUInt32(root, "input_epoch", out _) ||
                !ReadUInt32(root, "unique_input_reports", out sequence)) return false;
        }
        else
        {
            // ESP's current NSM1 contract is platform-specific and marks the
            // physical BLE source explicitly. Do not infer it from axis names.
            if (Text(root, "platform") != "esp32s3" || Text(root, "profile") != "ns2pro" ||
                Text(root, "input_source") != "ns2pro_ble" || sourceName != "ns2pro" ||
                !ReadRange(root, "input_age_ms", 0, 250, out _) ||
                !ReadUInt32(root, "source_epoch", out _) ||
                !ReadUInt32(root, "source_sequence", out sequence) ||
                !ReadUInt32(root, "source_timestamp_us", out _) || ReadBool(root, "battery_valid") is null)
                return false;
        }
        if (ReadBool(root, "input_rate_supported") is not { } rateSupported ||
            !ReadUInt32(root, "unique_input_reports", out _) ||
            !ReadUInt32(root, "input_rate_time_ms", out _) ||
            !ReadUInt32(root, "buttons", out var mask) || mask >= (1u << 25) ||
            !ReadRange(root, "lx", short.MinValue, short.MaxValue, out var lx) ||
            !ReadRange(root, "ly", short.MinValue, short.MaxValue, out var ly) ||
            !ReadRange(root, "rx", short.MinValue, short.MaxValue, out var rx) ||
            !ReadRange(root, "ry", short.MinValue, short.MaxValue, out var ry) ||
            !ReadRange(root, "lt", 0, ushort.MaxValue, out var lt) ||
            !ReadRange(root, "rt", 0, ushort.MaxValue, out var rt) ||
            ReadBool(root, "motion_valid") is not { } motion) return false;
        var axes = new int[6];
        var names = new[] { "ax", "ay", "az", "gx", "gy", "gz" };
        if (motion)
            for (var i = 0; i < names.Length; i++)
                if (!ReadRange(root, names[i], short.MinValue, short.MaxValue, out axes[i])) return false;
        if (!root.TryGetProperty("input_battery_percent", out var batteryValue)) return false;
        int? battery = null;
        if (batteryValue.ValueKind != JsonValueKind.Null)
        {
            if (!ReadRange(root, "input_battery_percent", -1, 100, out var percent)) return false;
            if (percent >= 0 && (pico || ReadBool(root, "battery_valid") == true)) battery = percent;
        }
        var label = source == BridgePhysicalInput.DualSense ? "DS5 physical input" : "NS2Pro physical input";
        frame = new(new ControllerInputSnapshot(label, mask, lx, ly, rx, ry, lt, rt,
            axes[0], axes[1], axes[2], axes[3], axes[4], axes[5], motion, battery), source, null, epoch,
            rateSupported ? sequence : null);
        return true;
    }

    internal static bool TryReadEpoch(JsonElement root, out uint? epoch)
    {
        epoch = null;
        foreach (var name in new[] { "input_epoch", "source_epoch" })
        {
            if (!root.TryGetProperty(name, out _)) continue;
            if (!ReadUInt32(root, name, out var value) || (epoch is not null && epoch != value)) return false;
            epoch = value;
        }
        return true;
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static bool ReadUInt32(JsonElement root, string name, out uint value)
    {
        value = 0;
        return root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number &&
            item.TryGetUInt32(out value);
    }

    private static bool? ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? item.GetBoolean() : null;

    private static bool ReadRange(JsonElement root, string name, int minimum, int maximum, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number &&
            item.TryGetInt32(out value) && value >= minimum && value <= maximum;
    }
}
