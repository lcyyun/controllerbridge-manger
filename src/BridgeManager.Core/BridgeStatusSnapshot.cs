using System.Text.Json;

namespace BridgeManager.Core;

public sealed record BridgeStatusSnapshot
{
    public string? Profile { get; init; }
    public BridgeUsbRole UsbRole { get; init; } = BridgeUsbRole.Unknown;
    public BridgeInputPreference InputPreference { get; init; } = BridgeInputPreference.Unknown;
    public BridgePhysicalInput ActiveInput { get; init; } = BridgePhysicalInput.Unknown;
    public bool? InputValid { get; init; }
    public bool? InputStale { get; init; }
    public bool? UsbMounted { get; init; }
    public bool? UsbSuspended { get; init; }
    public double? ReportRateHz { get; init; }
    public bool? Ds5Saved { get; init; }
    public bool? Ds5Connected { get; init; }
    public bool? Ds5Pairing { get; init; }
    public bool? Ns2Saved { get; init; }
    public bool? Ns2Connected { get; init; }
    public bool? Ns2Scanning { get; init; }
    public ulong? AcceptedReports { get; init; }
    public ulong? IgnoredReports { get; init; }
    public ulong? FeedbackForwarded { get; init; }
    public ulong? FeedbackFailed { get; init; }
    public bool? RumbleEnabled { get; init; }
    public bool? RumbleActive { get; init; }
    public ulong? RumbleUpdates { get; init; }
    public ulong? RumbleWrites { get; init; }
    public ulong? RumbleStops { get; init; }
    public ulong? RumbleErrors { get; init; }
    public ulong? HidOutputReports { get; init; }
    public ulong? HidRumbleReports { get; init; }
    public ulong? HidIgnoredReports { get; init; }
    public string? RumbleOutputBackend { get; init; }
    public bool? RumbleTransportConnected { get; init; }
    public bool? RumbleOutputPending { get; init; }
    public ulong? RumbleOutputReports { get; init; }
    public ulong? RumbleOutputQueued { get; init; }
    public ulong? RumbleOutputBusy { get; init; }
    public ulong? RumbleOutputFailures { get; init; }
    public ulong? Ds5OutputReports { get; init; }
    public ulong? Ds5OutputQueued { get; init; }
    public ulong? Ds5OutputBusy { get; init; }
    public ulong? Ds5OutputFailures { get; init; }
    public bool? AudioCapable { get; init; }
    public bool? AudioSpeakerOpen { get; init; }
    public bool? AudioMicOpen { get; init; }
    public ulong? AudioOutPackets { get; init; }
    public ulong? AudioOutBytes { get; init; }
    public ulong? AudioOutErrors { get; init; }
    public ulong? AudioInPackets { get; init; }
    public ulong? AudioInBytes { get; init; }
    public ulong? AudioInErrors { get; init; }
    public ulong? AudioBtReports { get; init; }
    public ulong? AudioBtDropped { get; init; }
    public ulong? AudioOpusErrors { get; init; }
    public ulong? AudioHapticBlocks { get; init; }
    public bool? AudioNs2Active { get; init; }
    public ulong? AudioNs2Updates { get; init; }
    public ulong? AudioNs2Stops { get; init; }
    public ulong? AudioNs2MixedTicks { get; init; }

    public static BridgeStatusSnapshot Parse(JsonElement root)
    {
        var ds5Object = TryObject(root, "ds5");
        return new BridgeStatusSnapshot
        {
            Profile = GetString(root, "profile"),
            UsbRole = BridgeRoles.ParseUsbRole(GetString(root, "role")),
            InputPreference = BridgeRoles.ParseInputPreference(GetString(root, "input_preference")),
            ActiveInput = BridgeRoles.ParsePhysicalInput(GetString(root, "active_input") ?? GetString(root, "source")),
            InputValid = GetBool(root, "input_valid") ?? GetBool(root, "valid"),
            InputStale = GetBool(root, "input_stale") ?? GetBool(root, "stale"),
            UsbMounted = GetBool(root, "mounted") ?? GetBool(root, "usb_mounted"),
            UsbSuspended = GetBool(root, "suspended") ?? GetBool(root, "usb_suspended"),
            ReportRateHz = GetDouble(root, "report_rate_hz"),
            Ds5Saved = GetBool(root, "ds5_saved"),
            Ds5Connected = GetBool(root, "ds5_connected"),
            Ds5Pairing = GetBool(root, "ds5_pairing"),
            Ns2Saved = GetBool(root, "ns2_saved"),
            Ns2Connected = GetBool(root, "ns2_connected"),
            Ns2Scanning = GetBool(root, "ns2_scanning"),
            AcceptedReports = GetUInt64(root, "accepted_reports"),
            IgnoredReports = GetUInt64(root, "ignored_reports"),
            FeedbackForwarded = GetUInt64(root, "feedback_forwarded"),
            FeedbackFailed = GetUInt64(root, "feedback_failed"),
            RumbleEnabled = GetBool(root, "rumble_enabled"),
            RumbleActive = GetBool(root, "rumble_active"),
            RumbleUpdates = GetUInt64(root, "rumble_updates") ?? GetUInt64(root, "source_updates"),
            RumbleWrites = GetUInt64(root, "rumble_writes"),
            RumbleStops = GetUInt64(root, "rumble_stops") ?? GetUInt64(root, "source_stops"),
            RumbleErrors = GetUInt64(root, "rumble_errors"),
            HidOutputReports = GetUInt64(root, "hid_out"),
            HidRumbleReports = GetUInt64(root, "hid_rumble"),
            HidIgnoredReports = GetUInt64(root, "hid_ignored") ?? GetUInt64(root, "source_ignored"),
            RumbleOutputBackend = GetString(root, "output_backend"),
            RumbleTransportConnected = GetBool(root, "transport_connected"),
            RumbleOutputPending = GetBool(root, "output_pending"),
            RumbleOutputReports = GetUInt64(root, "output_reports"),
            RumbleOutputQueued = GetUInt64(root, "output_queued"),
            RumbleOutputBusy = GetUInt64(root, "output_busy"),
            RumbleOutputFailures = GetUInt64(root, "output_failures"),
            Ds5OutputReports = GetUInt64(root, "ds5_output_reports") ?? GetUInt64(ds5Object, "output_reports"),
            Ds5OutputQueued = GetUInt64(root, "ds5_output_queued") ?? GetUInt64(ds5Object, "output_queued"),
            Ds5OutputBusy = GetUInt64(root, "ds5_output_busy") ?? GetUInt64(ds5Object, "output_busy"),
            Ds5OutputFailures = GetUInt64(root, "ds5_output_failures") ?? GetUInt64(ds5Object, "output_failures"),
            AudioCapable = GetBool(root, "audio_capable"),
            AudioSpeakerOpen = GetBool(root, "audio_speaker_open"),
            AudioMicOpen = GetBool(root, "audio_mic_open"),
            AudioOutPackets = GetUInt64(root, "audio_out_packets"),
            AudioOutBytes = GetUInt64(root, "audio_out_bytes"),
            AudioOutErrors = GetUInt64(root, "audio_out_errors"),
            AudioInPackets = GetUInt64(root, "audio_in_packets"),
            AudioInBytes = GetUInt64(root, "audio_in_bytes"),
            AudioInErrors = GetUInt64(root, "audio_in_errors"),
            AudioBtReports = GetUInt64(root, "audio_bt_reports"),
            AudioBtDropped = GetUInt64(root, "audio_bt_dropped"),
            AudioOpusErrors = GetUInt64(root, "audio_opus_errors"),
            AudioHapticBlocks = GetUInt64(root, "audio_haptic_blocks"),
            AudioNs2Active = GetBool(root, "audio_ns2_active"),
            AudioNs2Updates = GetUInt64(root, "audio_ns2_updates"),
            AudioNs2Stops = GetUInt64(root, "audio_ns2_stops"),
            AudioNs2MixedTicks = GetUInt64(root, "audio_ns2_mixed_ticks")
        };
    }

    public BridgeStatusSnapshot Merge(BridgeStatusSnapshot newer) => this with
    {
        Profile = newer.Profile ?? Profile,
        UsbRole = newer.UsbRole != BridgeUsbRole.Unknown ? newer.UsbRole : UsbRole,
        InputPreference = newer.InputPreference != BridgeInputPreference.Unknown ? newer.InputPreference : InputPreference,
        ActiveInput = newer.ActiveInput != BridgePhysicalInput.Unknown ? newer.ActiveInput : ActiveInput,
        InputValid = newer.InputValid ?? InputValid,
        InputStale = newer.InputStale ?? InputStale,
        UsbMounted = newer.UsbMounted ?? UsbMounted,
        UsbSuspended = newer.UsbSuspended ?? UsbSuspended,
        ReportRateHz = newer.ReportRateHz ?? ReportRateHz,
        Ds5Saved = newer.Ds5Saved ?? Ds5Saved,
        Ds5Connected = newer.Ds5Connected ?? Ds5Connected,
        Ds5Pairing = newer.Ds5Pairing ?? Ds5Pairing,
        Ns2Saved = newer.Ns2Saved ?? Ns2Saved,
        Ns2Connected = newer.Ns2Connected ?? Ns2Connected,
        Ns2Scanning = newer.Ns2Scanning ?? Ns2Scanning,
        AcceptedReports = newer.AcceptedReports ?? AcceptedReports,
        IgnoredReports = newer.IgnoredReports ?? IgnoredReports,
        FeedbackForwarded = newer.FeedbackForwarded ?? FeedbackForwarded,
        FeedbackFailed = newer.FeedbackFailed ?? FeedbackFailed,
        RumbleEnabled = newer.RumbleEnabled ?? RumbleEnabled,
        RumbleActive = newer.RumbleActive ?? RumbleActive,
        RumbleUpdates = newer.RumbleUpdates ?? RumbleUpdates,
        RumbleWrites = newer.RumbleWrites ?? RumbleWrites,
        RumbleStops = newer.RumbleStops ?? RumbleStops,
        RumbleErrors = newer.RumbleErrors ?? RumbleErrors,
        HidOutputReports = newer.HidOutputReports ?? HidOutputReports,
        HidRumbleReports = newer.HidRumbleReports ?? HidRumbleReports,
        HidIgnoredReports = newer.HidIgnoredReports ?? HidIgnoredReports,
        RumbleOutputBackend = newer.RumbleOutputBackend ?? RumbleOutputBackend,
        RumbleTransportConnected = newer.RumbleTransportConnected ?? RumbleTransportConnected,
        RumbleOutputPending = newer.RumbleOutputPending ?? RumbleOutputPending,
        RumbleOutputReports = newer.RumbleOutputReports ?? RumbleOutputReports,
        RumbleOutputQueued = newer.RumbleOutputQueued ?? RumbleOutputQueued,
        RumbleOutputBusy = newer.RumbleOutputBusy ?? RumbleOutputBusy,
        RumbleOutputFailures = newer.RumbleOutputFailures ?? RumbleOutputFailures,
        Ds5OutputReports = newer.Ds5OutputReports ?? Ds5OutputReports,
        Ds5OutputQueued = newer.Ds5OutputQueued ?? Ds5OutputQueued,
        Ds5OutputBusy = newer.Ds5OutputBusy ?? Ds5OutputBusy,
        Ds5OutputFailures = newer.Ds5OutputFailures ?? Ds5OutputFailures,
        AudioCapable = newer.AudioCapable ?? AudioCapable,
        AudioSpeakerOpen = newer.AudioSpeakerOpen ?? AudioSpeakerOpen,
        AudioMicOpen = newer.AudioMicOpen ?? AudioMicOpen,
        AudioOutPackets = newer.AudioOutPackets ?? AudioOutPackets,
        AudioOutBytes = newer.AudioOutBytes ?? AudioOutBytes,
        AudioOutErrors = newer.AudioOutErrors ?? AudioOutErrors,
        AudioInPackets = newer.AudioInPackets ?? AudioInPackets,
        AudioInBytes = newer.AudioInBytes ?? AudioInBytes,
        AudioInErrors = newer.AudioInErrors ?? AudioInErrors,
        AudioBtReports = newer.AudioBtReports ?? AudioBtReports,
        AudioBtDropped = newer.AudioBtDropped ?? AudioBtDropped,
        AudioOpusErrors = newer.AudioOpusErrors ?? AudioOpusErrors,
        AudioHapticBlocks = newer.AudioHapticBlocks ?? AudioHapticBlocks,
        AudioNs2Active = newer.AudioNs2Active ?? AudioNs2Active,
        AudioNs2Updates = newer.AudioNs2Updates ?? AudioNs2Updates,
        AudioNs2Stops = newer.AudioNs2Stops ?? AudioNs2Stops,
        AudioNs2MixedTicks = newer.AudioNs2MixedTicks ?? AudioNs2MixedTicks
    };

    private static JsonElement? TryObject(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.Object ? property : null;

    private static string? GetString(JsonElement? root, string name) =>
        root is JsonElement value && value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static bool? GetBool(JsonElement? root, string name) =>
        root is JsonElement value && value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean() : null;

    private static double? GetDouble(JsonElement? root, string name) =>
        root is JsonElement value && value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.TryGetDouble(out var number)
            ? number : null;

    private static ulong? GetUInt64(JsonElement? root, string name) =>
        root is JsonElement value && value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.TryGetUInt64(out var number)
            ? number : null;
}
