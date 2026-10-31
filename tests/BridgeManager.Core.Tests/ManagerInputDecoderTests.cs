using System.Text.Json;
using System.Text.Json.Nodes;
using BridgeManager.Core;

internal static class ManagerInputDecoderTests
{
    public static void Run()
    {
        var fixture = JsonNode.Parse("""
            {"ok":true,"profile":"bridge_input","snapshot_ok":true,"source":"ns2pro","valid":true,
             "stale":false,"buttons":17825793,"left_x":-32768,"left_y":32767,"right_x":123,"right_y":-456,
             "left_trigger":65535,"right_trigger":32768,"motion_valid":true,"accel_x":1,"accel_y":2,
             "accel_z":8192,"gyro_x":-3,"gyro_y":4,"gyro_z":5,"timestamp_valid":true,
             "sensor_timestamp":4294967295,"battery_valid":false,"battery_percent":100}
            """)!.AsObject();
        ManagerInputFrame Decode(JsonObject value)
        {
            if (!ManagerInputDecoder.TryDecode(JsonSerializer.SerializeToElement(value), out var frame) || frame is null)
                throw new InvalidOperationException("Valid firmware bridge_input snapshot was rejected.");
            return frame;
        }
        var decoded = Decode(fixture);
        Require(decoded.Source == BridgePhysicalInput.NintendoNs2Pro && decoded.Input.Source == "NS2Pro physical input" &&
            decoded.Input.Buttons == 17825793 && decoded.Input.LeftX == -32768 && decoded.Input.LeftY == 32767 &&
            decoded.Input.LeftTrigger == 65535 && decoded.Input.RightTrigger == 32768 &&
            decoded.Input.AccelZ == 8192 && decoded.Input.GyroX == -3 && decoded.SensorTimestamp == uint.MaxValue &&
            decoded.Input.BatteryPercent is null, "Physical axes, extended buttons, timestamp and unknown battery must survive.");
        fixture["source"] = "ds5";
        fixture["sensor_timestamp"] = 0;
        fixture["battery_valid"] = true;
        fixture["battery_percent"] = 70;
        decoded = Decode(fixture);
        Require(decoded.Input.Source == "DS5 physical input" && decoded.SensorTimestamp == 0 &&
            decoded.Input.BatteryPercent == 70, "Timestamp wrap and explicit DS5 battery are valid.");
        foreach (var (field, value) in new (string, JsonNode?)[] {
            ("valid", JsonValue.Create(false)), ("stale", JsonValue.Create(true)),
            ("snapshot_ok", JsonValue.Create(false)), ("source", JsonValue.Create("none")),
            ("buttons", JsonValue.Create("bad")), ("left_x", JsonValue.Create(32768)),
            ("left_trigger", JsonValue.Create(-1)), ("gyro_x", JsonValue.Create(40000)),
            ("sensor_timestamp", JsonValue.Create(-1)) })
        {
            var invalid = fixture.DeepClone().AsObject();
            invalid[field] = value;
            Require(!ManagerInputDecoder.TryDecode(JsonSerializer.SerializeToElement(invalid), out _),
                $"Invalid {field} must not paint stale or invented input.");
        }
        fixture["motion_valid"] = false;
        fixture.Remove("gyro_x");
        decoded = Decode(fixture);
        Require(!decoded.Input.MotionValid && decoded.Input.GyroX == 0, "Unavailable motion is explicitly unknown.");
        Require(!ManagerInputDecoder.TryDecode(JsonSerializer.SerializeToElement(new { role = "xbox", input_valid = true }), out _),
            "Ordinary status cannot be mistaken for a physical input sample.");
        ShortNameContracts();
    }

    private static void ShortNameContracts()
    {
        var pico = JsonNode.Parse("""
            {"ok":true,"profile":"pico-unified","input_snapshot_schema":1,"active_input":"ds5",
             "input_valid":true,"input_stale":false,"unique_input_reports":42,"input_rate_time_ms":5000,
             "input_rate_supported":true,"input_epoch":4294967295,"input_battery_percent":-1,
             "buttons":17825793,"lx":-32768,"ly":32767,"rx":123,"ry":-456,"lt":65535,"rt":32768,
             "motion_valid":true,"ax":1,"ay":2,"az":8192,"gx":-3,"gy":4,"gz":5,
             "axis_format":"signed16","button_format":"canonical"}
            """)!.AsObject();
        ManagerInputFrame Decode(JsonObject value)
        {
            Require(ManagerInputDecoder.TryDecode(JsonSerializer.SerializeToElement(value), out var decoded) && decoded is not null,
                "Actual short-name firmware contract was rejected.");
            return decoded!;
        }
        void Reject(JsonObject value, string field, JsonNode? change)
        {
            var invalid = value.DeepClone().AsObject();
            invalid[field] = change;
            Require(!ManagerInputDecoder.TryDecode(JsonSerializer.SerializeToElement(invalid), out _),
                $"Malformed physical snapshot {field} accepted.");
        }
        var first = Decode(pico);
        Require(first.Input.Source == "DS5 physical input" && first.Input.LeftY == 32767 &&
            first.Input.RightTrigger == 32768 && first.Input.GyroX == -3 && first.Input.Buttons == 17825793 &&
            first.Input.BatteryPercent is null && first.InputEpoch == uint.MaxValue && first.SampleIdentity == 42,
            "Pico canonical controls, unknown battery or counter/epoch identity lost.");
        pico["unique_input_reports"] = 43;
        var stationary = Decode(pico);
        Require(stationary.Input == first.Input && stationary.SampleIdentity != first.SampleIdentity,
            "Stationary fresh Pico reports need distinct physical identities.");
        pico["input_epoch"] = 0;
        Require(Decode(pico).InputEpoch == 0, "Wrapped epoch is a valid new connection.");
        Reject(pico, "input_snapshot_schema", JsonValue.Create(2));
        Reject(pico, "axis_format", JsonValue.Create("unsigned12"));
        Reject(pico, "button_format", JsonValue.Create("usb-output"));
        Reject(pico, "profile", JsonValue.Create("usb_status"));
        Reject(pico, "active_input", JsonValue.Create("dse"));
        Reject(pico, "input_epoch", JsonValue.Create(-1));
        Reject(pico, "source_epoch", JsonValue.Create(99));
        Reject(pico, "unique_input_reports", JsonValue.Create(-1));
        Reject(pico, "input_battery_percent", JsonValue.Create(101));
        Reject(pico, "input_stale", JsonValue.Create(true));
        Reject(pico, "gy", JsonValue.Create(32768));
        var weak = pico.DeepClone().AsObject();
        weak.Remove("input_snapshot_schema");
        Require(!ManagerInputDecoder.TryDecode(JsonSerializer.SerializeToElement(weak), out _),
            "Unversioned arbitrary short-axis JSON must not masquerade as physical input.");

        var esp = pico.DeepClone().AsObject();
        foreach (var name in new[] { "input_snapshot_schema", "axis_format", "button_format", "input_epoch" }) esp.Remove(name);
        esp["profile"] = "ns2pro";
        esp["platform"] = "esp32s3";
        esp["input_source"] = "ns2pro_ble";
        esp["active_input"] = "ns2pro";
        esp["source_epoch"] = 7;
        esp["source_sequence"] = 65535;
        esp["source_timestamp_us"] = 0;
        esp["input_age_ms"] = 250;
        esp["battery_valid"] = false;
        esp["input_battery_percent"] = null;
        var input = Decode(esp);
        Require(input.Input.Source == "NS2Pro physical input" && input.InputEpoch == 7 &&
            input.SourceSequence == 65535 && input.Input.BatteryPercent is null,
            "ESP NSM1 physical source, epoch, wire sequence or unknown battery lost.");
        esp["battery_valid"] = true;
        esp["input_battery_percent"] = 52;
        Require(Decode(esp).Input.BatteryPercent == 52, "Explicit ESP battery lost.");
        Reject(esp, "active_input", JsonValue.Create("ds5"));
        Reject(esp, "platform", JsonValue.Create("esp32"));
        Reject(esp, "input_source", JsonValue.Create("usb"));
        Reject(esp, "input_age_ms", JsonValue.Create(251));
        Reject(esp, "source_epoch", JsonValue.Create("7"));
        Reject(esp, "input_epoch", JsonValue.Create(8));
        Reject(esp, "source_sequence", JsonValue.Create(-1));
        Reject(esp, "source_timestamp_us", JsonValue.Create(-1));
        Reject(esp, "input_valid", JsonValue.Create(false));
        Reject(esp, "buttons", JsonValue.Create(1u << 25));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
