using System.Text.Json;

namespace BridgeManager.Core.FirmwareModules;

public static class BridgeButtonMapping
{
    public static IReadOnlyList<string> ControlIds { get; } = Array.AsReadOnly(
        new[]
        {
            "south", "east", "west", "north",
            "dpad_up", "dpad_down", "dpad_left", "dpad_right",
            "left_shoulder", "right_shoulder", "left_trigger", "right_trigger",
            "back", "start", "left_stick", "right_stick", "guide", "touchpad",
            "mute", "capture", "left_paddle", "right_paddle",
            "left_function", "right_function", "c"
        });

    public static Dictionary<string, string> Parameters(
        BridgeModuleControlDefinition definition, string? target = null,
        string? source = null)
    {
        var parameters = new Dictionary<string, string>();
        if (definition.MappingProfile is { Length: > 0 } profile)
            parameters["profile"] = profile;
        if (target is not null) parameters["target"] = target;
        if (source is not null) parameters["source"] = source;
        return parameters;
    }

    public static Dictionary<string, string> ReadReply(
        BridgeModuleControlDefinition definition, JsonElement reply)
    {
        // A legacy/global reply must never be shown as an independent profile.
        if (definition.MappingProfile is { Length: > 0 } profile &&
            (reply.ValueKind != JsonValueKind.Object ||
             !reply.TryGetProperty("profile", out var returnedProfile) ||
             returnedProfile.ValueKind != JsonValueKind.String ||
             returnedProfile.GetString() != profile))
        {
            throw new InvalidDataException(
                "设备未确认独立映射配置，请更新对应固件后重新读取。");
        }
        if (reply.ValueKind == JsonValueKind.Object &&
            reply.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            throw new InvalidDataException("设备拒绝映射操作。");
        }
        if (!BridgeModuleOperationEngine.TrySelectJsonPointer(
                reply, definition.Binding ?? "", out var entries))
        {
            throw new InvalidDataException($"映射回复缺少 {definition.Binding}。");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string? target, JsonElement source)
        {
            if (target is null || !ControlIds.Contains(target) ||
                source.ValueKind != JsonValueKind.String ||
                source.GetString() is not { } sourceId ||
                (sourceId != "none" && !ControlIds.Contains(sourceId)) ||
                !result.TryAdd(target, sourceId))
                throw new InvalidDataException("映射回复包含无效或重复的按键。");
        }

        if (entries.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in entries.EnumerateObject())
                Add(property.Name, property.Value);
        }
        else if (entries.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in entries.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && index < ControlIds.Count)
                    Add(ControlIds[index], item);
                else if (item.ValueKind == JsonValueKind.Object &&
                         item.TryGetProperty("target", out var target) &&
                         target.ValueKind == JsonValueKind.String &&
                         item.TryGetProperty("source", out var source))
                    Add(target.GetString(), source);
                else
                    throw new InvalidDataException("映射数组包含无效按键。");
                index++;
            }
        }
        else
            throw new InvalidDataException("映射 entries 必须是对象或数组。");

        if (result.Count != ControlIds.Count)
            throw new InvalidDataException("映射回复不完整，未覆盖全部按键。");
        return result;
    }

    public static bool CanCapture(string? profile, BridgePhysicalInput input,
        string usbReportSource, IReadOnlyDictionary<string, string> deviceMapping)
    {
        // USB reports contain mapped output, not the original physical buttons.
        // Only a native identity path gives an unambiguous physical capture.
        return deviceMapping.Count == ControlIds.Count &&
               deviceMapping.All(pair => pair.Key == pair.Value) &&
               ((profile == "ds5" && input == BridgePhysicalInput.DualSense &&
                 usbReportSource == "DS5 USB input") ||
                (profile == "ns2pro" && input == BridgePhysicalInput.NintendoNs2Pro &&
                 usbReportSource == "NS2 USB input"));
    }
}
