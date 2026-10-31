using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BridgeManager.Core;
using BridgeManager.Core.FirmwareModules;
using BridgeManager.Core.Protocol;
using BridgeManager.Core.Transports;
using HidSharp.Reports;

internal static class Esp32S3Tests
{
    public static void Run()
    {
        Protocol();
        Telemetry();
        Flash();
        Module();
    }

    private static void Protocol()
    {
        var command = "mapping set ns2pro right_function right_paddle";
        var packets = NsmFeatureProtocol.BuildCommand(command, 0x11223344);
        Require(packets.Count == 2 && packets[0].Length == 63 && packets[0][7] == 1 &&
            packets[1][7] == 0 && packets[1][18] == 41 && packets[0][8] == 0x44,
            "NSM1 long request fragmentation failed");
        Require(Encoding.UTF8.GetString(packets.SelectMany(packet => packet.Skip(22).Take(packet[20])).ToArray()) == command,
            "NSM1 command data was changed");
        Reject<ArgumentException>(() => NsmFeatureProtocol.BuildCommand(new string('a', 513), 1));
        Reject<ArgumentException>(() => NsmFeatureProtocol.BuildCommand("status\0reboot", 1));
        var transport = new NsmTransport();
        var client = new ManagerCommandClient(transport);
        using (var reply = client.SendCommandAsync(command).GetAwaiter().GetResult())
            Require(reply.RootElement.GetProperty("command").GetString() == command && transport.Writes == 2 && transport.Reads > 3,
                "NSM1 Busy/stale replies/fragmented JSON roundtrip failed");
        var calls = Enumerable.Range(0, 4).Select(index => client.SendCommandAsync("echo " + index)).ToArray();
        Task.WaitAll(calls);
        for (var index = 0; index < calls.Length; index++)
        {
            using var reply = calls[index].Result;
            Require(reply.RootElement.GetProperty("command").GetString() == "echo " + index, "Concurrent NSM1 replies mixed");
        }
        foreach (var fault in new[] { "offset", "length", "flags", "status" })
        {
            var broken = new ManagerCommandClient(new NsmTransport { Fault = fault });
            Reject<ManagerCommandException>(() => broken.SendCommandAsync("status").GetAwaiter().GetResult());
        }
        using (var routed = new ManagerCommandClient(new NsmTransport(0x5a)).SendCommandAsync("status").GetAwaiter().GetResult())
            Require(routed.RootElement.GetProperty("command").GetString() == "status", "NSM1 ignored the descriptor-selected report ID");
        var wrongId = Reply(1, 0, 0, 0, [], 0x7f);
        Reject<ManagerCommandException>(() => NsmFeatureProtocol.ParseReply(wrongId, 0x7e));
        var malformed = Reply(1, 0, 0, 0, []);
        malformed[22] = 21;
        Reject<ManagerCommandException>(() => NsmFeatureProtocol.ParseReply(malformed, 0x7e));
    }

    private static void Telemetry()
    {
        var root = JsonNode.Parse("""
            {"profile":"ns2pro","platform":"esp32s3","input_source":"ns2pro_ble",
             "source_epoch":1,"source_sequence":42,"source_timestamp_us":0,"battery_valid":false,
             "input_rate_supported":true,"unique_input_reports":42,"input_rate_time_ms":100,
             "active_input":"ns2pro","input_valid":true,"input_stale":false,"input_age_ms":3,
             "buttons":32505856,"lx":-32768,"ly":32767,"rx":123,"ry":-456,"lt":65535,"rt":0,
             "motion_valid":true,"ax":1,"ay":2,"az":3,"gx":4,"gy":5,"gz":6,"input_battery_percent":null}
            """)!.AsObject();
        bool Decode(out ControllerInputSnapshot? input)
        {
            var valid = ManagerInputDecoder.TryDecode(JsonSerializer.SerializeToElement(root), out var frame);
            input = frame?.Input;
            return valid;
        }
        Require(Decode(out var parsed) && parsed!.LeftX == -32768 && parsed.LeftY == 32767 &&
            parsed.LeftTrigger == 65535 && parsed.MotionValid && parsed.GyroZ == 6 && parsed.BatteryPercent is null &&
            parsed.Buttons == 32505856, "Physical extended buttons/axes/triggers/motion/unknown battery failed");
        Require(BridgeButtonMapping.CanCapture("ns2pro", "ds5", BridgePhysicalInput.NintendoNs2Pro,
            parsed!.Source, BridgeButtonMapping.ControlIds.ToDictionary(id => id, id => id)), "Physical source capture incorrectly depends on USB output mapping");
        root["input_stale"] = true;
        Require(!Decode(out _), "Stale physical input accepted");
        root["input_stale"] = false; root["input_age_ms"] = 1001;
        Require(!Decode(out _), "Expired physical input accepted");
        root["input_age_ms"] = 1; root["active_input"] = "none";
        Require(!Decode(out _), "Disconnected physical input accepted");
        root["active_input"] = "ns2pro"; root["lx"] = 40000;
        Require(!Decode(out _), "Out-of-range physical axis accepted");
        root["lx"] = 0; root["input_battery_percent"] = 52; root["battery_valid"] = true;
        Require(Decode(out parsed) && parsed!.BatteryPercent == 52, "Reported physical battery lost");
    }

    private static void Flash()
    {
        var root = Path.Combine(Path.GetTempPath(), "esp32s3 flash tests " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var app = new byte[512]; app[0] = 0xe9; app[1] = 3; app[2] = 2; app[3] = 0x4f; app[12] = 9;
            var partition = Enumerable.Repeat((byte)0xff, 0xc00).ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(partition, 0x50aa); partition[2] = 0; partition[3] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(partition.AsSpan(4), 0x10000);
            BinaryPrimitives.WriteUInt32LittleEndian(partition.AsSpan(8), 0x100000);
            var images = new[] { ("bootloader", 0u, app), ("partition", 0x8000u, partition), ("application", 0x10000u, app) };
            foreach (var (kind, _, bytes) in images) File.WriteAllBytes(Path.Combine(root, kind + ".bin"), bytes);
            var manifest = Path.Combine(root, "flash-manifest.json");
            JsonObject Definition() => JsonSerializer.SerializeToNode(new
            {
                schemaVersion = 1, chip = "esp32s3", board = "n16r8", flashSizeMb = 16,
                psramSizeMb = 8, psramMode = "octal", flashMode = "dio", flashFrequencyMhz = 80, baudRate = 460800,
                files = images.Select(file => new { kind = file.Item1, path = file.Item1 + ".bin", address = $"0x{file.Item2:x}",
                    size = file.Item3.Length, sha256 = Convert.ToHexString(SHA256.HashData(file.Item3)).ToLowerInvariant() })
            })!.AsObject();
            void Write(JsonObject definition) => File.WriteAllText(manifest, definition.ToJsonString());
            Write(Definition());
            var firmware = new BridgeFirmwareDefinition("esp-test", "ESP", "1.0", "", ["esp32s3-n16r8"],
                FirmwareFlashMethod.EspressifSerial, "flash-manifest.json", "");
            var module = new TestModule(root);
            var tool = Environment.ProcessPath!;
            Require(Path.GetFileName(tool).Equals("BridgeManager.Core.Tests.exe", StringComparison.OrdinalIgnoreCase),
                "Flasher regression must run the test executable only");
            var service = new FirmwareFlashService(() => null, () => ["COM7", "COM8", "COM9", "COM11"], () => null, () => tool);
            Require(service.Check(module, firmware, "COM7").Ready, "Valid ESP32-S3 bundle rejected");
            var details = FirmwareFlashService.DescribeArtifact(module, firmware);
            Require(details.Available && details.FileCount == 3 && details.TotalBytes == 4096, "Three images not described");
            Require(!service.Check(module, firmware with { BoardIds = ["esp32s3-n8r8"] }, "COM7").Ready, "Mismatched module board accepted");
            Require(!service.Check(module, firmware, "COM700").Ready, "Missing COM port accepted");
            Require(!service.Check(module, firmware, "COM7 --erase-all").Ready, "Command text accepted as serial port");
            Require(!new FirmwareFlashService(() => null, () => ["COM7"], () => null, () => null)
                .Check(module, firmware, "COM7").Ready, "Missing pinned tool accepted");
            var start = FirmwareFlashService.CreateEspressifProcess(tool, manifest, "COM7", "write_flash");
            Require(!start.UseShellExecute && start.CreateNoWindow && start.ArgumentList.Contains("esp32s3") &&
                start.ArgumentList.Contains("0x10000") && start.ArgumentList.Contains(Path.Combine(root, "application.bin")) &&
                !start.ArgumentList.Contains("--force") && !start.ArgumentList.Contains("erase_flash"), "Unsafe flash arguments");
            var result = service.FlashAsync(module, firmware, "COM7", null, CancellationToken.None).GetAwaiter().GetResult();
            Require(result.Success, "Fake flash/probe/verify launch failed: " + result.Message);
            Require(File.ReadAllLines(Path.Combine(root, "fake-commands.txt")).SequenceEqual(["COM7 flash_id", "COM7 write_flash", "COM7 verify_flash"]),
                "Probe, write and verify were not sequenced");
            foreach (var port in new[] { "COM8", "COM9" })
                Require(!service.FlashAsync(module, firmware, port, null, CancellationToken.None).GetAwaiter().GetResult().Success,
                    "Wrong physical chip/flash capacity accepted");
            Require(!File.ReadAllText(Path.Combine(root, "fake-commands.txt")).Contains("COM8 write_flash") &&
                !File.ReadAllText(Path.Combine(root, "fake-commands.txt")).Contains("COM9 write_flash"), "Write ran after failed probe");
            Require(!service.FlashAsync(module, firmware, "COM11", null, CancellationToken.None).GetAwaiter().GetResult().Success,
                "Readback failure falsely reported success");
            foreach (var mutation in new Action<JsonObject>[]
            {
                json => json["chip"] = "esp32", json => json["board"] = "n8r8", json => json["psramSizeMb"] = 2,
                json => json["flashSizeMb"] = 2147483648L, json => json.Remove("chip"),
                json => json["flashMode"] = "qio", json => json["files"]![2]!["sha256"] = new string('0', 64),
                json => json["files"]![2]!["address"] = "0x8000", json => json["files"]![2]!["size"] = 99,
                json => json["files"]![2]!["path"] = "../application.bin", json => json["files"]![2]!["path"] = "application.bin:ads"
            })
            {
                var definition = Definition(); mutation(definition); Write(definition);
                Require(!service.Check(module, firmware, "COM7").Ready, "Invalid manifest accepted: " + definition);
            }
            Write(Definition());
            var corruptImage = app.ToArray(); corruptImage[12] = 0;
            File.WriteAllBytes(Path.Combine(root, "application.bin"), corruptImage);
            var badHeader = Definition(); badHeader["files"]![2]!["sha256"] = Convert.ToHexString(SHA256.HashData(corruptImage)); Write(badHeader);
            Require(!service.Check(module, firmware, "COM7").Ready, "Non-S3 app with valid package hash accepted");
            File.WriteAllBytes(Path.Combine(root, "application.bin"), app);
            BinaryPrimitives.WriteUInt32LittleEndian(partition.AsSpan(8), 0x100);
            File.WriteAllBytes(Path.Combine(root, "partition.bin"), partition);
            Write(Definition());
            Require(!service.Check(module, firmware, "COM7").Ready, "Application exceeding factory partition accepted");
            var tools = Path.Combine(root, "tools", "esptool"); Directory.CreateDirectory(tools);
            File.WriteAllBytes(Path.Combine(tools, "esptool.exe"), [1, 2, 3]);
            Require(FirmwareFlashService.ResolveEspressifTool(root) is null, "Unpinned executable accepted");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Module()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? path = null;
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "modules", "esp32s3-ns2-bridge", "esp32s3-ns2-bridge.bridge-module.json");
            if (File.Exists(candidate)) { path = candidate; break; }
            directory = directory.Parent;
        }
        if (path is null) throw new Exception("ESP32-S3 source module template is missing.");
        var module = BridgeModulePackageLoader.LoadFile(path);
        Require(module.HidDevices.Count == 4 && module.UsbRoles.Count == 4 && module.InputSources.Count == 1 &&
            module.WirelessControllers.Count == 1 && module.Boards.Count >= 1 &&
            module.Firmware.Count == module.Boards.Count && module.Pages.Count > 0,
            "ESP module does not separate four USB outputs from single physical input");
        var registry = new BridgeFirmwareModuleRegistry(moduleDirectories: [Path.GetDirectoryName(path)!]);
        Require(new Esp32S3Ns2BridgeFirmwareModule().HidDevices.All(profile => profile.ManagerFeatureReportId == 0x7e),
            "Built-in ESP fallback retained the retired NSM1 report ID");
        foreach (var profile in module.HidDevices)
        {
            // Parse descriptor bytes through the real HidSharp parser. A feature
            // in a gamepad collection cannot satisfy the vendor collection match.
            byte[] Collection(bool vendor, byte id, byte count = 63) =>
                [.. (vendor ? new byte[] { 0x06, 0x00, 0xff, 0x09, 0x01 } :
                    new byte[] { 0x05, 0x01, 0x09, 0x05 }),
                 0xa1, 0x01, 0x85, id, 0x09, 0x01, 0x15, 0x00, 0x26, 0xff, 0x00,
                 0x75, 0x08, 0x95, count, 0xb1, 0x02, 0xc0];
            bool Matches(byte[] bytes, string? product = null) =>
                WindowsHidTransportFactory.MatchesManagerCollection(profile,
                    new ReportDescriptor(bytes), "unique-chip-id", product ?? profile.LegacyManagerProductName);
            Require(Matches(Collection(true, 0x7e)) && !Matches(Collection(false, 0x7e)) &&
                !Matches(Collection(true, 0x7f)) && !Matches(Collection(true, 0x7e, 62)) &&
                !Matches([.. Collection(true, 0x7f), .. Collection(false, 0x7e)]) &&
                !Matches(Collection(true, 0x7e), "DualSense HID"),
                "NSM1 discovery admitted the wrong HID collection, feature, length or identity");
            var descriptor = new DeviceDescriptor("fixture-" + profile.Key, "fixture", profile.VendorId, profile.ProductId,
                DeviceTransportKind.Hid, "ESP32-S3", true, false, SerialNumber: "unique-chip-id",
                ProductName: profile.LegacyManagerProductName, ManagerFeatureReportId: 0x7e);
            var normalized = registry.NormalizeDescriptor(descriptor);
            Require(normalized.ProfileKey == profile.Key && normalized.ManagerWireProtocol == BridgeManagerWireProtocol.Nsm1 &&
                registry.Resolve(normalized).UsbRoles.Count == 4 && registry.Resolve(normalized).ModuleVersion == module.ModuleVersion,
                "Built-in ESP module constrained the installed four-role manifest");
            Require(profile.ManagerWireProtocol == BridgeManagerWireProtocol.Nsm1 && !profile.SupportsInputReports &&
                profile.ManagerFeatureReportId == 0x7e && profile.CommandOutputReportId is null &&
                profile.MatchesManagerIdentity("unique-chip-id", profile.LegacyManagerProductName) &&
                !profile.MatchesManagerIdentity("DualSense HID", "DualSense HID"), "NSM1 identity/discovery profile invalid");
        }
        var mapper = module.Pages.SelectMany(page => page.Sections).SelectMany(section => section.Controls)
            .Single(control => control.Type == BridgeModuleControlType.MappingEditor);
        Require(mapper.MappingProfile == "ns2pro" && mapper.MappingOutput is null, "UI invented firmware mapping dimensions");
        Require(module.Firmware.All(firmware => firmware.FlashMethod == FirmwareFlashMethod.EspressifSerial && firmware.BoardIds.Count == 1),
            "ESP firmware declared metadata-only or claimed another board");
    }

    public static bool TryRunFakeFlasher(string[] args)
    {
        if (!args.Contains("--chip") || !args.Contains("esp32s3")) return false;
        var port = args[Array.IndexOf(args, "--port") + 1];
        var command = args.Single(argument => argument is "flash_id" or "write_flash" or "verify_flash");
        File.AppendAllText("fake-commands.txt", port + " " + command + "\n");
        Console.Error.WriteLine("FAKE_ESPTOOL_STDERR");
        if (command == "flash_id")
        {
            Console.WriteLine(port == "COM9" ? "Chip is ESP32-C3" : "Chip is ESP32-S3 (QFN56) (revision v0.2)");
            Console.WriteLine(port == "COM8" ? "Detected flash size: 8MB" : "Detected flash size: 16MB");
        }
        else
        {
            Require(args.Contains("16MB") && args.Contains("0x0") && args.Contains("0x8000") && args.Contains("0x10000"), "Fake flash arguments incomplete");
            if (command == "verify_flash")
            {
                Require(args.Contains("hard_reset"), "Final reset omitted");
                if (port == "COM11") { Environment.ExitCode = 23; return true; }
                Console.WriteLine("Verification successful");
            }
            else { Require(args.Contains("no_reset"), "Reset before verification"); Console.WriteLine("Hash of data verified."); }
        }
        return true;
    }

    private static byte[] Reply(uint transaction, ushort status, int total, int offset, byte[] data, byte reportId = 0x7e)
    {
        var report = new byte[64]; report[0] = reportId;
        var p = report.AsSpan(1); "NSM1"u8.CopyTo(p); p[4] = 1; p[6] = 1;
        p[7] = (byte)((offset + data.Length < total ? 1 : 0) | (status != 0 ? 2 : 0));
        BinaryPrimitives.WriteUInt32LittleEndian(p[8..], transaction);
        BinaryPrimitives.WriteUInt16LittleEndian(p[12..], NsmFeatureProtocol.ExecuteCommand);
        BinaryPrimitives.WriteUInt16LittleEndian(p[14..], status);
        BinaryPrimitives.WriteUInt16LittleEndian(p[16..], (ushort)total);
        BinaryPrimitives.WriteUInt16LittleEndian(p[18..], (ushort)offset);
        p[20] = (byte)data.Length; p[21] = 22; data.CopyTo(p[22..]); return report;
    }

    private sealed class NsmTransport(byte featureReportId = 0x7e) : IDeviceTransport
    {
        public DeviceDescriptor Descriptor { get; } = new("fake", "fake", 0x054c, 0x0ce6,
            DeviceTransportKind.Hid, "ESP32-S3", true, false, ManagerFeatureReportId: featureReportId)
            { ManagerWireProtocol = BridgeManagerWireProtocol.Nsm1 };
        public string? Fault { get; init; }
        public int Writes, Reads;
        private readonly List<byte> _request = [];
        private readonly Queue<byte[]> _responses = [];
        public Task OpenAsync(CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task WriteOutputReportAsync(byte id, ReadOnlyMemory<byte> payload, CancellationToken token) => throw new Exception("NSM1 used interrupt output");
        public Task WriteFeatureReportAsync(byte id, ReadOnlyMemory<byte> payload, CancellationToken token)
        {
            Writes++; Require(id == featureReportId, "NSM1 incorrect report id");
            var p = payload.Span;
            var offset = BinaryPrimitives.ReadUInt16LittleEndian(p[18..]);
            if (offset == 0) _request.Clear();
            Require(offset == _request.Count, "NSM1 outgoing request was not contiguous");
            _request.AddRange(p.Slice(22, p[20]).ToArray());
            if ((p[7] & 1) == 0)
            {
                var transaction = BinaryPrimitives.ReadUInt32LittleEndian(p[8..]);
                var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = true, command = Encoding.UTF8.GetString(_request.ToArray()), padding = new string('x', 160) }));
                _responses.Enqueue(Reply(transaction - 1, 8, 0, 0, [], featureReportId));
                _responses.Enqueue(Reply(transaction, 8, 0, 0, [], featureReportId));
                for (var current = 0; current < data.Length; current += 41)
                {
                    var packet = Reply(transaction, 0, data.Length, current, data.Skip(current).Take(41).ToArray(), featureReportId);
                    if (current == 41 && Fault == "offset") packet[19]++;
                    if (current == 41 && Fault == "length") packet[17]++;
                    if (current == 0 && Fault == "flags") packet[8] = 0;
                    if (current == 0 && Fault == "status") { packet[15] = 7; packet[8] |= 2; }
                    _responses.Enqueue(packet);
                }
            }
            return Task.CompletedTask;
        }
        public Task<byte[]> ReadFeatureReportAsync(byte id, CancellationToken token)
        {
            Require(id == featureReportId, "NSM1 read ignored the profile report ID");
            Reads++; return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class TestModule(string root) : BridgeFirmwareModuleBase
    {
        public override string Id => "esp-test";
        public override string DisplayName => "Test";
        public override string BoardFamily => "Test";
        public override string Description => "";
        public override string? PackageRoot => root;
        public override BridgeCapability Capabilities => BridgeCapability.FirmwareFlashing;
        public override bool Matches(DeviceDescriptor descriptor) => false;
    }

    private static void Require(bool value, string error) { if (!value) throw new Exception(error); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected rejection: " + typeof(T).Name);
    }
}
