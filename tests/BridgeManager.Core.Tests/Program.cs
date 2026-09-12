using System.IO.Compression;
using System.Security.Cryptography;
using BridgeManager.Core;
using BridgeManager.Core.FirmwareModules;
using BridgeManager.Core.Protocol;
using BridgeManager.Core.Transports;

if (args.Length == 1 && args[0] == "--list-local")
{
    await using var controllers = new LocalControllerService();
    var devices = await controllers.GetDevicesAsync(CancellationToken.None);
    Console.WriteLine($"LOCAL-CONTROLLERS count={devices.Count}");
    foreach (var device in devices)
        Console.WriteLine($"{device.DisplayName} | {device.TransportLabel}");
    return;
}

if (args.Length >= 3 && args[0] == "--command")
{
    var descriptor = new DeviceDescriptor(
        args[1], $"SF32LB52 Serial ({args[1]})", 0, 0,
        DeviceTransportKind.Serial, "SF32LB52 Serial", true, false,
        DiagnosticOnly: true);
    await using var transport = new SerialBridgeTransport(descriptor);
    await transport.OpenAsync(CancellationToken.None);
    var client = new ManagerCommandClient(transport);
    using var reply = await client.SendCommandAsync(string.Join(' ', args.Skip(2)),
                                                     CancellationToken.None);
    Console.WriteLine(reply.RootElement.GetRawText());
    return;
}

if (args.Length >= 2 && args[0] == "--flash-check")
{
    var registry = new BridgeFirmwareModuleRegistry();
    var module = registry.Find(args[1]) ??
        throw new InvalidOperationException($"Unknown firmware module: {args[1]}");
    var firmware = module.Firmware.FirstOrDefault() ??
        throw new InvalidOperationException("Module has no firmware artifact.");
    var port = args.Length >= 3 ? args[2] : null;
    var check = new FirmwareFlashService().Check(module, firmware, port);
    Console.WriteLine($"FLASH-CHECK ready={check.Ready} target={check.Target} artifact={check.ArtifactPath}");
    Console.WriteLine(check.Message);
    Environment.ExitCode = check.Ready ? 0 : 2;
    return;
}

if (args.Length == 2 && args[0] == "--install-module-check")
{
    var installRoot = Path.Combine(Path.GetTempPath(),
        "bridge-module-install-" + Guid.NewGuid().ToString("N"));
    try
    {
        var installed = BridgeModulePackageInstaller.Install(args[1], installRoot);
        var registry = new BridgeFirmwareModuleRegistry(
            moduleDirectories: [installRoot]);
        var module = registry.Find(installed.ModuleId) ??
            throw new InvalidOperationException("Installed module was not loaded.");
        AssertEqual(installed.ModuleVersion, module.ModuleVersion);
        AssertEqual(true, module.Firmware.Count > 0);
        var firmware = module.Firmware[0];
        if (firmware.FlashMethod == FirmwareFlashMethod.None &&
            string.IsNullOrWhiteSpace(firmware.ArtifactRelativePath))
        {
            Console.WriteLine($"INFO module {module.Id} uses external platform flashing");
        }
        else
        {
            AssertEqual(true, FirmwareFlashService.ResolveArtifact(
                module, firmware) is not null);
        }
        Console.WriteLine($"PASS installed module {module.Id} {module.ModuleVersion} with verified artifact");
    }
    finally
    {
        if (Directory.Exists(installRoot))
        {
            Directory.Delete(installRoot, recursive: true);
        }
    }
    return;
}

if (args.Length == 1 && args[0] == "--github-download-check")
{
    var service = new GithubModuleReleaseService();
    var assets = await service.GetModuleAssetsAsync();
    var latest = assets.First(asset => asset.Name == "sf32-unified-0.7.0-dev.cbmodule");
    var path = await service.DownloadAsync(latest);
    try
    {
        AssertEqual(latest.Size, new FileInfo(path).Length);
        Console.WriteLine($"PASS anonymous firmware download and SHA256: {latest.Name}");
    }
    finally { File.Delete(path); }
    var update = await new ManagerUpdateService().CheckAsync(includePrerelease: true);
    Console.WriteLine($"PASS anonymous manager update check: {update?.Tag ?? "no newer version"}");
    return;
}

if (args.Length == 1 && args[0] == "--github-module-list")
{
    var assets = await new GithubModuleReleaseService()
        .GetModuleAssetsAsync(cancellationToken: CancellationToken.None);
    Console.WriteLine($"GITHUB-MODULES count={assets.Count}");
    foreach (var asset in assets)
    {
        Console.WriteLine($"{asset.Tag} {asset.Name} {asset.Size}");
    }
    return;
}

if (args.Length >= 2 && args[0] == "--hid-raw")
{
    var factory = new WindowsHidTransportFactory();
    var devices = await factory.GetDevicesAsync(CancellationToken.None);
    var descriptor = devices.FirstOrDefault() ??
        throw new InvalidOperationException("No manager HID device was found.");
    await using var transport = await factory.OpenAsync(
        descriptor, CancellationToken.None);
    var command = string.Join(' ', args.Skip(1));
    await transport.WriteFeatureReportAsync(
        descriptor.ManagerFeatureReportId,
        FeatureReportProtocol.BuildFeatureCommandPayload(
            command, includeReportId: false),
        CancellationToken.None);
    for (var attempt = 0; attempt < 6; attempt++)
    {
        if (attempt > 0) await Task.Delay(20);
        var report = await transport.ReadFeatureReportAsync(
            descriptor.ManagerFeatureReportId, CancellationToken.None);
        var head = Convert.ToHexString(report.AsSpan(0,
            Math.Min(20, report.Length)));
        try
        {
            var chunk = FeatureReportProtocol.ParseReplyChunk(
                report, descriptor.ManagerFeatureReportId);
            Console.WriteLine($"HID-RAW attempt={attempt} valid=true " +
                $"total={chunk.TotalLength} offset={chunk.Offset} " +
                $"head={head} text={FeatureReportProtocol.DecodeReply(
                    chunk.Payload, chunk.Payload.Length)}");
        }
        catch (ManagerCommandException ex)
        {
            Console.WriteLine($"HID-RAW attempt={attempt} valid=false " +
                $"head={head} error={ex.Message}");
        }
    }
    return;
}

var tests = new (string Name, Action Run)[]
{
    ("validates manager update channels, versions and origin", ManagerUpdateSelection),
    ("builds Windows feature command payload", BuildsWindowsFeatureCommandPayload),
    ("builds output command payload", BuildsOutputCommandPayload),
    ("parses reply with report id", ParsesReplyWithReportId),
    ("parses reply without report id", ParsesReplyWithoutReportId),
    ("retries a native DS5 feature before manager reply", RetriesNativeDs5Feature),
    ("finds SF32LB52 Xbox 360 manager profile", FindsSf32lb52Xbox360ManagerProfile),
    ("finds SF32LB52 DualSense manager profile", FindsSf32lb52DualSenseManagerProfile),
    ("finds SF32LB52 DualSense Edge manager profile", FindsSf32lb52DualSenseEdgeManagerProfile),
    ("finds SF32LB52 Nintendo manager profile", FindsSf32lb52NintendoManagerProfile),
    ("resolves SF32 unified firmware module", ResolvesSf32UnifiedFirmwareModule),
    ("distinguishes SF32 and Pico Nintendo identities", DistinguishesNintendoFirmwareModules),
    ("keeps unknown Nintendo identities board-neutral", KeepsUnknownNintendoIdentityNeutral),
    ("resolves ESP32-S3 NS2Pro firmware module", ResolvesEsp32S3Ns2FirmwareModule),
    ("resolves Pico unified firmware module", ResolvesPicoUnifiedFirmwareModule),
    ("falls back for unknown manager firmware", FallsBackForUnknownManagerFirmware),
    ("filters firmware by board", FiltersFirmwareByBoard),
    ("does not offer Nano firmware to the LCD board", RejectsNanoFirmwareForLcdBoard),
    ("loads an external module override", LoadsExternalModuleOverride),
    ("loads HID discovery from a firmware module", LoadsModuleHidDiscovery),
    ("ignores an older external module", IgnoresOlderExternalModule),
    ("rejects a module requiring a newer runtime", RejectsNewerModuleRuntime),
    ("rejects null module collections", RejectsNullModuleCollections),
    ("loads Runtime API 2 dynamic pages", LoadsRuntimeApi2DynamicPages),
    ("keeps Runtime API 1 modules compatible", KeepsRuntimeApi1ModulesCompatible),
    ("rejects invalid Runtime API 2 action references", RejectsInvalidRuntimeApi2ActionReferences),
    ("rejects malformed Runtime API 2 operations", RejectsMalformedRuntimeApi2Operations),
    ("rejects incomplete MappingEditor controls", RejectsIncompleteMappingEditorControls),
    ("validates independent mapping profiles", ValidatesMappingProfiles),
    ("rejects incomplete or wrong-profile mapping replies", ValidatesMappingReplies),
    ("gates physical capture on native identity USB reports", GatesMappingCapture),
    ("coalesces input frames and preserves edges", InputReportBufferTests.Run),
    ("expands all four source-output mapping command templates", MappingPairTests.Templates),
    ("gates pair replies and preserves legacy mapping parsers", MappingPairTests.Replies),
    ("captures only native identity source-output pairs", MappingPairTests.Capture),
    ("validates source-output MappingEditor definitions", MappingPairTests.Validation),
    ("loads the four SF32 mapping pages and schema contract", MappingPairTests.Manifest),
    ("rejects invalid dynamic numeric ranges", RejectsInvalidDynamicNumericRanges),
    ("rejects Runtime API 2 fields in an API 1 module", RejectsApi2FieldsInApi1Module),
    ("rejects unknown dynamic module fields", RejectsUnknownDynamicModuleFields),
    ("expands and selects dynamic module operations", ExpandsDynamicModuleOperation),
    ("rejects failed dynamic module operations", RejectsFailedDynamicModuleOperation),
    ("does not identify a generic COM port as SF32", DoesNotGuessSerialBoard),
    ("rejects a single-file module without hashes", RejectsUnhashedModulePackage),
    ("uses Semantic Versioning for module precedence", ComparesSemanticModuleVersions),
    ("rejects firmware module downgrades", RejectsModuleDowngrade),
    ("keeps packaged firmware artifacts inside the module", KeepsArtifactInsideModule),
    ("rejects a single-file module without a license", RejectsUnlicensedModulePackage),
    ("recovers an interrupted firmware module install", RecoversInterruptedModuleInstall),
    ("validates bundled firmware, real ports and verified flash arguments without device writes", FirmwareFlashTests.Run),
    ("tests independent local controller decoding and lifecycle without physical devices", LocalControllerTests.Run),
    ("matches current and legacy Sony manager identities", MatchesSonyManagerIdentities),
    ("known profiles have unique profile keys", KnownProfilesHaveUniqueProfileKeys),
    ("decodes DS5 USB input reports", DecodesDs5UsbInput),
    ("decodes NS2 USB input reports", DecodesNs2UsbInput),
    ("builds full test commands", BuildsFullTestCommands),
    ("maps supported USB roles and two physical inputs", MapsRolesAndInputs),
    ("parses current source-aware bridge status", ParsesCurrentBridgeStatus),
    ("parses older bridge status without DS5 counters", ParsesOlderBridgeStatus),
    ("parses source-aware rumble status", ParsesSourceAwareRumbleStatus),
    ("merges bridge and USB status", MergesBridgeAndUsbStatus),
    ("selects role after USB re-enumeration", SelectsRoleAfterUsbReenumeration),
    ("does not reconnect the previous USB role", DoesNotReconnectPreviousUsbRole),
    ("reconnects diagnostic serial by exact port", ReconnectsSerialByExactPort),
    ("orders USB HID before diagnostic serial", OrdersUsbBeforeSerial),
    ("builds source-aware diagnostics", BuildsSourceAwareDiagnostics),
    ("rejects truncated manager commands", RejectsLongCommands)
};

static void ManagerUpdateSelection()
{
    string Feed(string tag, bool preview = true, string? url = null, string? digest = null)
    {
        var version = tag.TrimStart('v').Split('-')[0];
        var name = $"ControllerBridge-Setup-{version}-win-x64.exe";
        return System.Text.Json.JsonSerializer.Serialize(new[] { new {
            draft = false, prerelease = preview, tag_name = tag,
            assets = new[] { new {
                name, size = 1024,
                browser_download_url = url ?? $"https://github.com/lcyyun/controllerbridge-manger/releases/download/{tag}/{name}",
                digest = digest ?? "sha256:" + new string('a', 64)
            }}
        }});
    }
    var current = new Version(0, 2, 1);
    const string tag = "v0.2.1-preview.1";
    AssertEqual(true, ManagerUpdateService.SelectUpdate(Feed(tag), current, tag, true) is null);
    AssertEqual(true, ManagerUpdateService.SelectUpdate(Feed("v0.2.0-preview.9"), current, tag, true) is null);
    AssertEqual(true, ManagerUpdateService.SelectUpdate(Feed("v0.2.2-preview.1"), current, tag, false) is null);
    AssertEqual("v0.2.1-preview.2", ManagerUpdateService.SelectUpdate(Feed("v0.2.1-preview.2"), current, tag, true)!.Tag);
    AssertEqual("v0.2.1", ManagerUpdateService.SelectUpdate(Feed("v0.2.1", false), current, tag, false)!.Tag);
    AssertEqual(true, ManagerUpdateService.SelectUpdate(Feed("v0.2.2", false, "https://example.com/setup.exe"), current, tag, false) is null);
    AssertEqual(true, ManagerUpdateService.SelectUpdate(Feed("v0.2.2", false, digest: "sha256:bad"), current, tag, false) is null);
    AssertEqual(true, ManagerUpdateService.SelectUpdate(Feed("v0.2.1-preview.2"), current, "v0.2.1", true) is null);
}

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}

if (args.Length == 1 && args[0] == "--list-hid")
{
    var devices = await new WindowsHidTransportFactory()
        .GetDevicesAsync(CancellationToken.None);
    foreach (var device in devices)
    {
        Console.WriteLine($"HID {device.VendorId:x4}:{device.ProductId:x4} " +
                          $"role={device.UsbRole.DisplayName()} {device.DisplayName}");
    }
    Console.WriteLine($"PASS verified manager HID devices={devices.Count}");
}

if (args.Length == 2 && args[0] == "--serial")
{
    var descriptor = new DeviceDescriptor(
        args[1], $"SF32LB52 Serial ({args[1]})", 0, 0,
        DeviceTransportKind.Serial, "SF32LB52 Serial", true, false,
        DiagnosticOnly: true);
    await using var transport = new SerialBridgeTransport(descriptor);
    await transport.OpenAsync(CancellationToken.None);
    var client = new ManagerCommandClient(transport);
    using var status = await client.SendCommandAsync("bridge status", CancellationToken.None);
    var root = status.RootElement;
    var profile = root.GetProperty("profile").GetString() ?? "";
    AssertEqual(true, profile is "bridge" or "ds5" or "ns2");
    _ = root.GetProperty("role").GetString();
    using var input = await client.SendCommandAsync(ManagerCommands.BridgeInput, CancellationToken.None);
    AssertEqual("bridge_input", input.RootElement.GetProperty("profile").GetString() ?? "");
    _ = input.RootElement.GetProperty("buttons").GetUInt32();
    _ = input.RootElement.GetProperty("gyro_x").GetInt32();
    Console.WriteLine($"PASS serial manager {args[1]} role={root.GetProperty("role").GetString()} input-schema=ok");
}

static void BuildsFullTestCommands()
{
    AssertEqual("role set xbox", ManagerCommands.SetRole("xbox"));
    AssertEqual("role set dse", ManagerCommands.SetRole("dse"));
    AssertEqual("input set ns2pro", ManagerCommands.SetInput("ns2pro"));
    AssertEqual("pair ds5", ManagerCommands.Pair("ds5"));
    AssertEqual("connect ns2", ManagerCommands.Connect("ns2"));
    AssertEqual("disconnect ds5", ManagerCommands.Disconnect("ds5"));
    AssertEqual("forget ns2", ManagerCommands.Forget("ns2"));
    AssertEqual("role set ds5", ManagerCommands.SetRole(BridgeUsbRole.DualSense));
    AssertEqual("input set auto", ManagerCommands.SetInput(BridgeInputPreference.Auto));
    AssertEqual("rumble", ManagerCommands.RumbleStatus);
}

static void ResolvesSf32UnifiedFirmwareModule()
{
    var registry = new BridgeFirmwareModuleRegistry();
    var descriptor = new DeviceDescriptor(
        "sf32-ds5", "SF32 DS5", 0x054c, 0x0ce6,
        DeviceTransportKind.Hid, "DualSense Manager", true, true, true,
        BridgeUsbRole.DualSense, ProfileKey: "dualsense",
        SerialNumber: "DualSense HID");
    var module = registry.Resolve(descriptor);
    AssertEqual("sf32-unified", module.Id);
    AssertEqual(true, module.Capabilities.Has(BridgeCapability.UsbAudio));
    AssertEqual(2, module.UsbRoles.Count);
    AssertEqual(2, module.WirelessControllers.Count);
}

static void DistinguishesNintendoFirmwareModules()
{
    var registry = new BridgeFirmwareModuleRegistry();
    var sf32 = new DeviceDescriptor(
        "sf32-ns2", "SF32 Nintendo", 0x057e, 0x2069,
        DeviceTransportKind.Hid, "NS2", true, true, true,
        BridgeUsbRole.NintendoNs2Pro, ProfileKey: "ns2pro-nintendo",
        SerialNumber: "HA2F83JI");
    var pico = sf32 with { Id = "pico-ns2", SerialNumber = "HA2F83JF" };
    AssertEqual("sf32-unified", registry.Resolve(sf32).Id);
    AssertEqual("pico-unified-bridge", registry.Resolve(pico).Id);
}

static void KeepsUnknownNintendoIdentityNeutral()
{
    var registry = new BridgeFirmwareModuleRegistry();
    var unknown = new DeviceDescriptor(
        "unknown-ns2", "Unknown Nintendo", 0x057e, 0x2069,
        DeviceTransportKind.Hid, "NS2", true, true, true,
        BridgeUsbRole.NintendoNs2Pro, ProfileKey: "ns2pro-nintendo",
        SerialNumber: null);
    AssertEqual(registry.Fallback.Id, registry.Resolve(unknown).Id);
    AssertEqual(registry.Fallback.Id,
        registry.Resolve(unknown with { SerialNumber = "THIRD-PARTY" }).Id);
}

static void ResolvesEsp32S3Ns2FirmwareModule()
{
    var descriptor = new DeviceDescriptor(
        "esp32s3-ns2", "ESP32-S3 NS2Pro", 0x057e, 0x2069,
        DeviceTransportKind.Hid, "Nintendo Switch 2", true, true, true,
        BridgeUsbRole.NintendoNs2Pro, ProfileKey: "ns2pro-nintendo",
        SerialNumber: "NS2BRIDGE-S3-N-MGR2");
    var module = new BridgeFirmwareModuleRegistry().Resolve(descriptor);
    AssertEqual("esp32s3-ns2-bridge", module.Id);
    AssertEqual(1, module.WirelessControllers.Count);
    AssertEqual(false, module.Capabilities.Has(BridgeCapability.UsbAudio));
}

static void ResolvesPicoUnifiedFirmwareModule()
{
    var descriptor = new DeviceDescriptor(
        "pico-auto", "Pico Auto", 0xcafe, 0x4012,
        DeviceTransportKind.Hid, "Pico Auto", true, false,
        ProfileKey: "pico-auto-manager");
    var module = new BridgeFirmwareModuleRegistry().Resolve(descriptor);
    AssertEqual("pico-unified-bridge", module.Id);
    AssertEqual(true,
        module.Capabilities.Has(BridgeCapability.UsbRoleSelection));
    AssertEqual(2, module.WirelessControllers.Count);
    AssertEqual("input status", module.InputStatusCommand);
}

static void FallsBackForUnknownManagerFirmware()
{
    var descriptor = new DeviceDescriptor(
        "unknown", "Unknown", 0x1234, 0xabcd,
        DeviceTransportKind.Hid, "Unknown", true, false,
        ProfileKey: "unknown-profile");
    var registry = new BridgeFirmwareModuleRegistry();
    AssertEqual(registry.Fallback.Id, registry.Resolve(descriptor).Id);
}

static void FiltersFirmwareByBoard()
{
    var firmware = new BridgeFirmwareModuleRegistry()
        .GetFirmwareForBoard("pico-2-w");
    AssertEqual(true, firmware.Any(item => item.Module.Id == "pico-unified-bridge"));
    AssertEqual(false, firmware.Any(item => item.Module.Id == "pico-auto-bridge"));
    AssertEqual(false, firmware.Any(item => item.Module.Id == "pico-ns2-bridge"));
    AssertEqual(false, firmware.Any(item => item.Module.Id == "sf32-unified"));
}

static void RejectsNanoFirmwareForLcdBoard()
{
    var registry = new BridgeFirmwareModuleRegistry();
    AssertEqual(false, registry.GetBoards().Any(board =>
        board.Id == "sf32lb52-lcd-n16r8"));
    AssertEqual(0, registry.GetFirmwareForBoard(
        "sf32lb52-lcd-n16r8").Count);
}

static void LoadsExternalModuleOverride()
{
    var root = Path.Combine(Path.GetTempPath(),
        "bridge-module-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        File.WriteAllText(Path.Combine(root, "override.bridge-module.json"), """
        {
          "schemaVersion": 1,
          "runtimeApiVersion": 1,
          "id": "sf32-unified",
          "moduleVersion": "9.9.9",
          "displayName": "Updated SF32 Module",
          "boardFamily": "SF32",
          "description": "external override",
          "priority": 100,
          "capabilities": "DeviceStatus, RawCommands",
          "matchAny": [{"profileKeys":["dualsense"]}],
          "statusCommands": ["new status"],
          "selfTestCommands": ["new status"],
          "primaryStatusCommand": "new status",
          "inputStatusCommand": "new input"
        }
        """);
        var registry = new BridgeFirmwareModuleRegistry(
            moduleDirectories: [root]);
        var descriptor = new DeviceDescriptor(
            "sf32", "sf32", 0x054c, 0x0ce6,
            DeviceTransportKind.Hid, "test", true, true,
            ProfileKey: "dualsense");
        var module = registry.Resolve(descriptor);
        AssertEqual("9.9.9", module.ModuleVersion);
        AssertEqual("new status", module.PrimaryStatusCommand);
        AssertEqual(0, registry.LoadIssues.Count);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void IgnoresOlderExternalModule()
{
    var root = Path.Combine(Path.GetTempPath(),
        "bridge-module-version-test-" + Guid.NewGuid().ToString("N"));
    var currentRoot = Path.Combine(root, "current");
    var olderRoot = Path.Combine(root, "older");
    Directory.CreateDirectory(currentRoot);
    Directory.CreateDirectory(olderRoot);
    try
    {
        const string template = """
        {
          "schemaVersion": 1,
          "runtimeApiVersion": 1,
          "id": "versioned-module",
          "moduleVersion": "VERSION",
          "displayName": "Versioned",
          "boardFamily": "Test",
          "description": "version test",
          "capabilities": "DeviceStatus",
          "matchAny": [{"profileKeys":["versioned"]}]
        }
        """;
        File.WriteAllText(Path.Combine(currentRoot, "module.json"),
            template.Replace("VERSION", "2.0.0"));
        File.WriteAllText(Path.Combine(olderRoot, "module.json"),
            template.Replace("VERSION", "1.0.0"));
        var registry = new BridgeFirmwareModuleRegistry(
            moduleDirectories: [currentRoot, olderRoot]);
        AssertEqual("2.0.0",
            registry.Find("versioned-module")?.ModuleVersion);
        AssertEqual(1, registry.LoadIssues.Count);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void LoadsModuleHidDiscovery()
{
    var root = NewTestDirectory("bridge-hid-discovery-test-");
    try
    {
        File.WriteAllText(Path.Combine(root, "module.json"),
            ModuleManifest("dynamic-hid", "1.0.0", """
            ,"hidDevices":[{
              "key":"dynamic-manager","displayName":"Dynamic Manager",
              "vendorId":4660,"productId":22136,
              "managerUsagePage":65280,"managerUsageId":1,
              "managerFeatureReportId":126,"commandOutputReportId":3,
              "usbRole":"Unknown","tags":["dynamic"],
              "supportsInputReports":true
            }]
            """));
        var registry = new BridgeFirmwareModuleRegistry(
            modules: [], moduleDirectories: [root]);
        var profile = registry.GetHidDeviceProfiles().Single(item =>
            item.Key == "dynamic-manager");
        AssertEqual((ushort)0x1234, profile.VendorId);
        AssertEqual((ushort)0x5678, profile.ProductId);
        AssertEqual((byte)0x7e, profile.ManagerFeatureReportId);
        AssertEqual((byte?)0x03, profile.CommandOutputReportId);
        AssertEqual(true, profile.SupportsInputReports);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void RejectsNewerModuleRuntime()
{
    var root = Path.Combine(Path.GetTempPath(),
        "bridge-module-api-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var path = Path.Combine(root, "future.bridge-module.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 1,
          "runtimeApiVersion": 999,
          "id": "future-module",
          "moduleVersion": "1.0.0",
          "displayName": "Future",
          "boardFamily": "Future",
          "description": "future",
          "capabilities": "DeviceStatus",
          "matchAny": [{"profileKeys":["future"]}]
        }
        """);
        var rejected = false;
        try
        {
            _ = BridgeModulePackageLoader.LoadFile(path);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        AssertEqual(true, rejected);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void RejectsNullModuleCollections()
{
    var root = Path.Combine(Path.GetTempPath(),
        "bridge-module-null-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var path = Path.Combine(root, "module.json");
        File.WriteAllText(path, """
        {
          "schemaVersion": 1,
          "runtimeApiVersion": 1,
          "id": "bad-null-module",
          "moduleVersion": "1.0.0",
          "displayName": "Bad Null",
          "boardFamily": "Test",
          "description": "invalid",
          "capabilities": "DeviceStatus",
          "matchAny": null
        }
        """);
        var rejected = false;
        try
        {
            _ = BridgeModulePackageLoader.LoadFile(path);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        AssertEqual(true, rejected);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void LoadsRuntimeApi2DynamicPages()
{
    var root = NewTestDirectory("bridge-module-api2-test-");
    try
    {
        var path = Path.Combine(root, "module.json");
        File.WriteAllText(path, RuntimeApi2Manifest());
        var module = BridgeModulePackageLoader.LoadFile(path);

        AssertEqual(2, module.RuntimeApiVersion);
        AssertEqual(1, module.Pages.Count);
        AssertEqual("controller", module.Pages[0].Id);
        AssertEqual(1, module.Pages[0].Sections.Length);
        AssertEqual(3, module.Pages[0].Sections[0].Controls.Length);

        var mapping = module.Pages[0].Sections[0].Controls[0];
        AssertEqual(BridgeModuleControlType.MappingEditor, mapping.Type);
        AssertEqual("/mapping/profiles/default/entries", mapping.Binding);
        AssertEqual("controller.inputControls", mapping.SourceCatalog);
        AssertEqual("bridge.outputControls", mapping.TargetCatalog);
        AssertEqual("mapping.read", mapping.ReadAction);
        AssertEqual("mapping.replace", mapping.ApplyAction);
        AssertEqual("mapping.reset", mapping.ResetAction);
        AssertEqual("settings.save", mapping.SaveAction);
        AssertEqual(System.Text.Json.JsonValueKind.Array,
            mapping.Default?.ValueKind);
        AssertEqual("/features/mapping", mapping.VisibleWhen?.Binding);

        var rate = module.Pages[0].Sections[0].Controls[1];
        AssertEqual(BridgeModuleControlType.Slider, rate.Type);
        AssertEqual(60m, rate.Min);
        AssertEqual(1000m, rate.Max);
        AssertEqual(10m, rate.Step);
        AssertEqual("Hz", rate.Unit);

        var mode = module.Pages[0].Sections[0].Controls[2];
        AssertEqual(2, mode.Options.Length);
        AssertEqual("automatic", mode.Options[0].Value);

        AssertEqual(6, module.Operations.Count);
        AssertEqual(BridgeModuleOperationTransport.ManagerCommand,
            module.Operations["mapping.read"].Transport);
        AssertEqual("mapping get", module.Operations["mapping.read"].Request);
        AssertEqual("/mappings",
            module.Operations["mapping.read"].Response?.Select);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void KeepsRuntimeApi1ModulesCompatible()
{
    var root = NewTestDirectory("bridge-module-api1-compat-test-");
    try
    {
        var path = Path.Combine(root, "module.json");
        File.WriteAllText(path, ModuleManifest("api1-compatible", "1.0.0"));
        var module = BridgeModulePackageLoader.LoadFile(path);

        AssertEqual(1, module.RuntimeApiVersion);
        AssertEqual(0, module.Pages.Count);
        AssertEqual(0, module.Operations.Count);
        AssertEqual(0, module.BuildSettingsCommands(new BridgeModuleSettings(
            250, false, true, 100, 140, 30, 3)).Count);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void RejectsInvalidRuntimeApi2ActionReferences()
{
    AssertManifestRejected(
        RuntimeApi2Manifest().Replace(
            "\"saveAction\": \"settings.save\"",
            "\"saveAction\": \"settings.missing\""),
        "references unknown operation 'settings.missing'");
}

static void RejectsMalformedRuntimeApi2Operations()
{
    AssertManifestRejected(
        RuntimeApi2Manifest().Replace(
            "\"request\": \"mapping get\"",
            "\"request\": \"\""),
        "requires a request");
}

static void RejectsIncompleteMappingEditorControls()
{
    AssertManifestRejected(
        RuntimeApi2Manifest().Replace(
            "\"targetCatalog\": \"bridge.outputControls\"",
            "\"targetCatalog\": \"\""),
        "targetCatalog is invalid");
}

static void ValidatesMappingProfiles()
{
    AssertManifestRejected(RuntimeApi2Manifest().Replace(
        "\"targetCatalog\": \"bridge.outputControls\"",
        "\"targetCatalog\": \"bridge.outputControls\", \"mappingProfile\":\"ps5\""),
        "mappingProfile");

    foreach (var profile in new[] { "ds5", "ns2pro" })
    {
        var control = new BridgeModuleControlDefinition { MappingProfile = profile };
        foreach (var verb in new[] { "get", "save", "reset" })
        {
            var operation = new BridgeModuleOperationDefinition
            {
                Request = $"mapping {verb} {{profile}}"
            };
            AssertEqual($"mapping {verb} {profile}", BridgeModuleOperationEngine.BuildRequest(
                operation, BridgeButtonMapping.Parameters(control)));
        }
        AssertEqual($"mapping set {profile} south east",
            BridgeModuleOperationEngine.BuildRequest(
                new BridgeModuleOperationDefinition { Request = "mapping set {profile} {target} {source}" },
                BridgeButtonMapping.Parameters(control, "south", "east")));
    }
    AssertEqual(0, BridgeButtonMapping.Parameters(new BridgeModuleControlDefinition()).Count);
}

static void ValidatesMappingReplies()
{
    var control = new BridgeModuleControlDefinition
    {
        MappingProfile = "ds5", Binding = "/entries"
    };
    var entries = BridgeButtonMapping.ControlIds.ToDictionary(id => id, id => id);
    entries["south"] = "east";
    var valid = System.Text.Json.JsonSerializer.SerializeToElement(
        new { ok = true, profile = "ds5", entries });
    AssertEqual("east", BridgeButtonMapping.ReadReply(control, valid)["south"]);
    void Reject(System.Text.Json.JsonElement reply)
    {
        var rejected = false;
        try { BridgeButtonMapping.ReadReply(control, reply); }
        catch (InvalidDataException) { rejected = true; }
        AssertEqual(true, rejected);
    }
    Reject(System.Text.Json.JsonSerializer.SerializeToElement(
        new { ok = true, profile = "ns2pro", entries }));
    Reject(System.Text.Json.JsonSerializer.SerializeToElement(new { ok = true, entries }));
    Reject(System.Text.Json.JsonSerializer.SerializeToElement(
        new { ok = false, profile = "ds5", entries }));
    entries["east"] = "unknown";
    Reject(System.Text.Json.JsonSerializer.SerializeToElement(
        new { ok = true, profile = "ds5", entries }));
    entries.Remove("east");
    Reject(System.Text.Json.JsonSerializer.SerializeToElement(
        new { ok = true, profile = "ds5", entries }));
    var legacy = new BridgeModuleControlDefinition { Binding = "/entries" };
    var array = System.Text.Json.JsonSerializer.SerializeToElement(
        new { entries = BridgeButtonMapping.ControlIds });
    AssertEqual(25, BridgeButtonMapping.ReadReply(legacy, array).Count);
}

static void GatesMappingCapture()
{
    var identity = BridgeButtonMapping.ControlIds.ToDictionary(id => id, id => id);
    AssertEqual(true, BridgeButtonMapping.CanCapture("ds5", BridgePhysicalInput.DualSense,
        "DS5 USB input", identity));
    AssertEqual(true, BridgeButtonMapping.CanCapture("ns2pro", BridgePhysicalInput.NintendoNs2Pro,
        "NS2 USB input", identity));
    AssertEqual(false, BridgeButtonMapping.CanCapture("ds5", BridgePhysicalInput.NintendoNs2Pro,
        "DS5 USB input", identity));
    AssertEqual(false, BridgeButtonMapping.CanCapture("ns2pro", BridgePhysicalInput.NintendoNs2Pro,
        "DS5 USB input", identity));
    AssertEqual(false, BridgeButtonMapping.CanCapture("ds5", BridgePhysicalInput.Unknown,
        "DS5 USB input", identity));
    identity["south"] = "east";
    AssertEqual(false, BridgeButtonMapping.CanCapture("ds5", BridgePhysicalInput.DualSense,
        "DS5 USB input", identity));
}

static void RejectsInvalidDynamicNumericRanges()
{
    AssertManifestRejected(
        RuntimeApi2Manifest().Replace("\"max\": 1000", "\"max\": 50"),
        "min must not exceed max");
}

static void RejectsApi2FieldsInApi1Module()
{
    AssertManifestRejected(
        RuntimeApi2Manifest().Replace(
            "\"runtimeApiVersion\": 2", "\"runtimeApiVersion\": 1"),
        "require runtimeApiVersion 2");
}

static void RejectsUnknownDynamicModuleFields()
{
    AssertManifestRejected(
        RuntimeApi2Manifest().Replace(
            "\"label\": \"Controls\"",
            "\"label\": \"Controls\", \"lable\": \"typo\""),
        "could not be mapped");
}

static void ExpandsDynamicModuleOperation()
{
    var operation = new BridgeModuleOperationDefinition
    {
        Transport = BridgeModuleOperationTransport.ManagerCommand,
        Request = "mapping set {target} {source}",
        Response = new BridgeModuleOperationResponseDefinition
            { Select = "/mapping/entries" }
    };
    var request = BridgeModuleOperationEngine.BuildRequest(operation,
        new Dictionary<string, string>
        {
            ["target"] = "south",
            ["source"] = "east"
        });
    AssertEqual("mapping set south east", request);

    using var document = System.Text.Json.JsonDocument.Parse(
        """{"ok":true,"mapping":{"entries":{"south":"east"}}}""");
    var selected = BridgeModuleOperationEngine.SelectSuccessfulResponse(
        "mapping.set", operation, document.RootElement);
    AssertEqual("east", selected.GetProperty("south").GetString());
}

static void RejectsFailedDynamicModuleOperation()
{
    var operation = new BridgeModuleOperationDefinition
    {
        Transport = BridgeModuleOperationTransport.ManagerCommand,
        Request = "mapping get"
    };
    using var document = System.Text.Json.JsonDocument.Parse(
        """{"ok":false,"error":"unknown_command"}""");
    var rejected = false;
    try
    {
        _ = BridgeModuleOperationEngine.SelectSuccessfulResponse(
            "mapping.read", operation, document.RootElement);
    }
    catch (InvalidOperationException ex)
    {
        rejected = ex.Message.Contains("unknown_command",
            StringComparison.Ordinal);
    }
    AssertEqual(true, rejected);
}

static void DoesNotGuessSerialBoard()
{
    var descriptor = SerialBridgeTransportFactory.CreateCandidateDescriptor(
        "COM999");
    AssertEqual(DeviceTransportKind.Serial, descriptor.TransportKind);
    AssertEqual(true, descriptor.DiagnosticOnly);
    AssertEqual(false, descriptor.Platform.Contains("SF32",
        StringComparison.OrdinalIgnoreCase));
    AssertEqual(null, descriptor.ProfileKey);
}

static void RejectsUnhashedModulePackage()
{
    var root = Path.Combine(Path.GetTempPath(),
        "bridge-unhashed-test-" + Guid.NewGuid().ToString("N"));
    var source = Path.Combine(root, "source");
    var install = Path.Combine(root, "install");
    Directory.CreateDirectory(source);
    try
    {
        File.WriteAllText(Path.Combine(source, "module.json"), """
        {
          "schemaVersion": 1,
          "runtimeApiVersion": 1,
          "id": "unhashed-module",
          "moduleVersion": "1.0.0",
          "displayName": "Unhashed",
          "boardFamily": "Test",
          "description": "invalid package",
          "capabilities": "DeviceStatus",
          "matchAny": []
        }
        """);
        var package = Path.Combine(root, "unhashed.cbmodule");
        ZipFile.CreateFromDirectory(source, package);
        var rejected = false;
        try
        {
            _ = BridgeModulePackageInstaller.Install(package, install);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }
        AssertEqual(true, rejected);
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void ComparesSemanticModuleVersions()
{
    AssertEqual(true, BridgeModuleVersion.IsValid("1.2.3-rc.1+build.7"));
    AssertEqual(true, BridgeModuleVersion.Compare("1.0.0", "1.0.0-rc.1") > 0);
    AssertEqual(true, BridgeModuleVersion.Compare(
        "1.0.0-beta.11", "1.0.0-beta.2") > 0);
    AssertEqual(false, BridgeModuleVersion.IsValid("1.0"));
    AssertEqual(false, BridgeModuleVersion.IsValid("1.0.0-01"));
}

static void RejectsModuleDowngrade()
{
    var root = NewTestDirectory("bridge-downgrade-test-");
    var install = Path.Combine(root, "install");
    try
    {
        var current = CreateModulePackage(root, "versioned", "2.0.0");
        var older = CreateModulePackage(root, "versioned", "1.0.0");
        _ = BridgeModulePackageInstaller.Install(current, install);
        var rejected = false;
        try
        {
            _ = BridgeModulePackageInstaller.Install(older, install);
        }
        catch (InvalidDataException ex)
        {
            rejected = ex.Message.Contains("downgrade",
                StringComparison.OrdinalIgnoreCase);
        }
        AssertEqual(true, rejected);
        var installed = BridgeModulePackageLoader.LoadFile(
            Path.Combine(install, "versioned", "module.json"));
        AssertEqual("2.0.0", installed.ModuleVersion);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void KeepsArtifactInsideModule()
{
    var root = NewTestDirectory("bridge-artifact-root-test-");
    try
    {
        var manifest = Path.Combine(root, "module.json");
        File.WriteAllText(manifest, ModuleManifest(
            "strict-artifact", "1.0.0", """
            ,"boards":[{"id":"test-board","displayName":"Test","family":"Test","description":"Test"}]
            ,"firmware":[{
              "id":"strict-firmware","displayName":"Strict","version":"1.0.0",
              "description":"Strict","boardIds":["test-board"],
              "flashMethod":"None","artifactRelativePath":"BridgeManager.Core.dll",
              "flashHint":"manual"
            }]
            """));
        var module = BridgeModulePackageLoader.LoadFile(manifest);
        AssertEqual<string?>(null, FirmwareFlashService.ResolveArtifact(
            module, module.Firmware[0]));
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void RejectsUnlicensedModulePackage()
{
    var root = NewTestDirectory("bridge-license-test-");
    var install = Path.Combine(root, "install");
    try
    {
        var package = CreateModulePackage(root, "unlicensed", "1.0.0",
            includeLicense: false);
        var rejected = false;
        try
        {
            _ = BridgeModulePackageInstaller.Install(package, install);
        }
        catch (InvalidDataException ex)
        {
            rejected = ex.Message.Contains("LICENSE",
                StringComparison.OrdinalIgnoreCase);
        }
        AssertEqual(true, rejected);
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static void RecoversInterruptedModuleInstall()
{
    var root = NewTestDirectory("bridge-recovery-test-");
    var backup = Path.Combine(root, "recoverable.previous");
    Directory.CreateDirectory(backup);
    try
    {
        File.WriteAllText(Path.Combine(backup, "module.json"),
            ModuleManifest("recoverable", "1.0.0"));
        var registry = new BridgeFirmwareModuleRegistry(
            modules: [], moduleDirectories: [root]);
        AssertEqual("1.0.0", registry.Find("recoverable")?.ModuleVersion);
        AssertEqual(true, Directory.Exists(Path.Combine(root, "recoverable")));
        AssertEqual(false, Directory.Exists(backup));
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static string CreateModulePackage(string root, string id, string version,
                                  bool includeLicense = true)
{
    var source = Path.Combine(root, $"source-{id}-{version}");
    Directory.CreateDirectory(source);
    File.WriteAllText(Path.Combine(source, "module.json"),
        ModuleManifest(id, version));
    if (includeLicense)
    {
        File.WriteAllText(Path.Combine(source, "LICENSE"), "Test license");
    }
    var hashes = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Select(path => $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant()}  " +
            Path.GetRelativePath(source, path).Replace(Path.DirectorySeparatorChar, '/'))
        .ToArray();
    File.WriteAllLines(Path.Combine(source, "MODULE-SHA256.txt"), hashes);
    var package = Path.Combine(root, $"{id}-{version}.cbmodule");
    ZipFile.CreateFromDirectory(source, package);
    return package;
}

static string ModuleManifest(string id, string version, string extra = "") => $$"""
    {
      "schemaVersion": 1,
      "runtimeApiVersion": 1,
      "id": "{{id}}",
      "moduleVersion": "{{version}}",
      "displayName": "Test module",
      "boardFamily": "Test",
      "description": "test",
      "capabilities": "DeviceStatus",
      "matchAny": []
      {{extra}}
    }
    """;

static string RuntimeApi2Manifest() => """
    {
      "schemaVersion": 1,
      "runtimeApiVersion": 2,
      "id": "dynamic-controller",
      "moduleVersion": "2.0.0",
      "displayName": "Dynamic Controller",
      "boardFamily": "Test",
      "description": "Runtime API 2 test module",
      "capabilities": "DeviceStatus, Settings",
      "matchAny": [],
      "operations": {
        "mapping.read": {
          "transport": "ManagerCommand",
          "request": "mapping get",
          "response": { "select": "/mappings" }
        },
        "mapping.replace": {
          "transport": "ManagerCommand",
          "request": "mapping replace {value}"
        },
        "mapping.reset": {
          "transport": "ManagerCommand",
          "request": "mapping reset"
        },
        "settings.read": {
          "transport": "ManagerCommand",
          "request": "settings",
          "response": { "select": "/settings" }
        },
        "settings.apply": {
          "transport": "ManagerCommand",
          "request": "settings set {binding} {value}"
        },
        "settings.save": {
          "transport": "ManagerCommand",
          "request": "settings save"
        }
      },
      "pages": [
        {
          "id": "controller",
          "label": "Controller",
          "description": "Controller configuration",
          "icon": "GameController",
          "sections": [
            {
              "id": "controls",
              "label": "Controls",
              "description": "Input and mapping settings",
              "controls": [
                {
                  "id": "button-mapping",
                  "type": "MappingEditor",
                  "label": "Button mapping",
                  "description": "Map physical inputs to USB controls",
                  "binding": "/mapping/profiles/default/entries",
                  "default": [],
                  "sourceCatalog": "controller.inputControls",
                  "targetCatalog": "bridge.outputControls",
                  "readAction": "mapping.read",
                  "applyAction": "mapping.replace",
                  "resetAction": "mapping.reset",
                  "saveAction": "settings.save",
                  "visibleWhen": {
                    "binding": "/features/mapping",
                    "operator": "Equals",
                    "value": true
                  }
                },
                {
                  "id": "report-rate",
                  "type": "Slider",
                  "label": "USB report rate",
                  "binding": "/settings/reportRateHz",
                  "default": 250,
                  "min": 60,
                  "max": 1000,
                  "step": 10,
                  "unit": "Hz",
                  "readAction": "settings.read",
                  "applyAction": "settings.apply"
                },
                {
                  "id": "input-mode",
                  "type": "Select",
                  "label": "Input mode",
                  "binding": "/settings/inputMode",
                  "default": "automatic",
                  "options": [
                    { "value": "automatic", "label": "Automatic" },
                    { "value": "manual", "label": "Manual", "description": "Use a fixed source" }
                  ],
                  "readAction": "settings.read",
                  "applyAction": "settings.apply"
                }
              ]
            }
          ]
        }
      ]
    }
    """;

static void AssertManifestRejected(string json, string expectedMessage)
{
    var root = NewTestDirectory("bridge-invalid-module-test-");
    try
    {
        var path = Path.Combine(root, "module.json");
        File.WriteAllText(path, json);
        Exception? failure = null;
        try
        {
            _ = BridgeModulePackageLoader.LoadFile(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or
                                   System.Text.Json.JsonException)
        {
            failure = ex;
        }

        AssertEqual(true, failure is not null);
        AssertEqual(true, failure!.Message.Contains(expectedMessage,
            StringComparison.OrdinalIgnoreCase));
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static string NewTestDirectory(string prefix)
{
    var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

static void MapsRolesAndInputs()
{
    var roles = new[]
    {
        BridgeUsbRole.DualSense,
        BridgeUsbRole.NintendoNs2Pro
    };
    foreach (var role in roles)
    {
        AssertEqual(role, BridgeRoles.ParseUsbRole(role.CommandValue()));
    }
    AssertEqual(BridgePhysicalInput.DualSense, BridgeRoles.ParsePhysicalInput("ds5"));
    AssertEqual(BridgePhysicalInput.NintendoNs2Pro, BridgeRoles.ParsePhysicalInput("ns2pro"));
    AssertEqual(BridgePhysicalInput.None, BridgeRoles.ParsePhysicalInput("none"));
}

static void ParsesCurrentBridgeStatus()
{
    using var document = System.Text.Json.JsonDocument.Parse("""
        {
          "ok": true,
          "profile": "ds5",
          "role": "dse",
          "input_preference": "auto",
          "active_input": "ds5",
          "input_valid": true,
          "input_stale": false,
          "audio_capable": true,
          "feedback_forwarded": 41,
          "feedback_failed": 2,
          "ds5_saved": true,
          "ds5_connected": true,
          "ds5_pairing": false,
          "ds5_output_reports": 90,
          "ds5_output_queued": 7,
          "ds5_output_busy": 5,
          "ds5_output_failures": 1,
          "ns2_saved": true,
          "ns2_connected": false,
          "ns2_scanning": true
        }
        """);
    var status = BridgeStatusSnapshot.Parse(document.RootElement);
    AssertEqual(BridgeUsbRole.DualSenseEdge, status.UsbRole);
    AssertEqual(BridgeInputPreference.Auto, status.InputPreference);
    AssertEqual(BridgePhysicalInput.DualSense, status.ActiveInput);
    AssertEqual(true, status.InputValid.GetValueOrDefault());
    AssertEqual((ulong)90, status.Ds5OutputReports.GetValueOrDefault());
    AssertEqual((ulong)7, status.Ds5OutputQueued.GetValueOrDefault());
    AssertEqual((ulong)5, status.Ds5OutputBusy.GetValueOrDefault());
    AssertEqual((ulong)1, status.Ds5OutputFailures.GetValueOrDefault());
}

static void ParsesOlderBridgeStatus()
{
    using var document = System.Text.Json.JsonDocument.Parse("""
        {"profile":"bridge","role":"ns2pro","active_input":"ns2pro","feedback_forwarded":12}
        """);
    var status = BridgeStatusSnapshot.Parse(document.RootElement);
    AssertEqual(BridgeUsbRole.NintendoNs2Pro, status.UsbRole);
    AssertEqual(BridgePhysicalInput.NintendoNs2Pro, status.ActiveInput);
    AssertEqual(false, status.Ds5OutputReports.HasValue);
    AssertEqual((ulong)12, status.FeedbackForwarded.GetValueOrDefault());
}

static void ParsesSourceAwareRumbleStatus()
{
    using var document = System.Text.Json.JsonDocument.Parse("""
        {
          "profile":"ds5","role":"ds5","source":"ds5",
          "output_backend":"ds5_classic","rumble_enabled":true,
          "rumble_active":false,"transport_connected":true,
          "output_pending":false,"output_reports":111,
          "output_queued":9,"output_busy":8,"output_failures":2,
          "source_updates":17,"source_stops":3,"source_ignored":4
        }
        """);
    var status = BridgeStatusSnapshot.Parse(document.RootElement);
    AssertEqual(BridgePhysicalInput.DualSense, status.ActiveInput);
    AssertEqual("ds5_classic", status.RumbleOutputBackend ?? "");
    AssertEqual(true, status.RumbleTransportConnected.GetValueOrDefault());
    AssertEqual((ulong)111, status.RumbleOutputReports.GetValueOrDefault());
    AssertEqual((ulong)9, status.RumbleOutputQueued.GetValueOrDefault());
    AssertEqual((ulong)8, status.RumbleOutputBusy.GetValueOrDefault());
    AssertEqual((ulong)2, status.RumbleOutputFailures.GetValueOrDefault());
    AssertEqual((ulong)17, status.RumbleUpdates.GetValueOrDefault());
    AssertEqual((ulong)3, status.RumbleStops.GetValueOrDefault());
    AssertEqual((ulong)4, status.HidIgnoredReports.GetValueOrDefault());
}

static void MergesBridgeAndUsbStatus()
{
    using var bridge = System.Text.Json.JsonDocument.Parse("""
        {"role":"ds5","active_input":"ds5","input_valid":true,"audio_capable":true}
        """);
    using var usb = System.Text.Json.JsonDocument.Parse("""
        {"mounted":true,"suspended":false,"audio_out_packets":25,"audio_bt_reports":20,
         "audio_opus_errors":0,"audio_haptic_blocks":12,"audio_ns2_active":true,
         "audio_ns2_updates":8,"audio_ns2_stops":1,"audio_ns2_mixed_ticks":3}
        """);
    var status = BridgeStatusSnapshot.Parse(bridge.RootElement)
        .Merge(BridgeStatusSnapshot.Parse(usb.RootElement));
    AssertEqual(BridgeUsbRole.DualSense, status.UsbRole);
    AssertEqual(true, status.UsbMounted.GetValueOrDefault());
    AssertEqual((ulong)25, status.AudioOutPackets.GetValueOrDefault());
    AssertEqual((ulong)20, status.AudioBtReports.GetValueOrDefault());
    AssertEqual((ulong)12, status.AudioHapticBlocks.GetValueOrDefault());
    AssertEqual(true, status.AudioNs2Active.GetValueOrDefault());
    AssertEqual((ulong)8, status.AudioNs2Updates.GetValueOrDefault());
    AssertEqual((ulong)1, status.AudioNs2Stops.GetValueOrDefault());
    AssertEqual((ulong)3, status.AudioNs2MixedTicks.GetValueOrDefault());
}

static void SelectsRoleAfterUsbReenumeration()
{
    var previous = Hid("old-ds5", 0x054c, 0x0ce6, BridgeUsbRole.DualSense);
    var devices = new[]
    {
        Hid("new-ns2", 0x057e, 0x2069, BridgeUsbRole.NintendoNs2Pro),
        Hid("new-dse", 0x054c, 0x0df2, BridgeUsbRole.DualSenseEdge)
    };
    var selected = DeviceReconnectSelector.Select(devices, previous, BridgeUsbRole.DualSenseEdge);
    AssertEqual("new-dse", selected?.Id ?? "");
}

static void DoesNotReconnectPreviousUsbRole()
{
    var previous = Hid("old-ds5", 0x054c, 0x0ce6, BridgeUsbRole.DualSense);
    var selected = DeviceReconnectSelector.Select(
        new[] { previous }, previous, BridgeUsbRole.NintendoNs2Pro);
    AssertEqual<DeviceDescriptor?>(null, selected);
}

static void ReconnectsSerialByExactPort()
{
    var previous = Serial("COM28");
    var devices = new[] { Serial("COM3"), Serial("com28") };
    var selected = DeviceReconnectSelector.Select(devices, previous, BridgeUsbRole.DualSense);
    AssertEqual("com28", selected?.Id ?? "");
}

static void OrdersUsbBeforeSerial()
{
    var ordered = DeviceReconnectSelector.OrderForDisplay(new[]
    {
        Serial("COM28"),
        Hid("ns2", 0x057e, 0x2069, BridgeUsbRole.NintendoNs2Pro),
        Hid("ds5", 0x054c, 0x0ce6, BridgeUsbRole.DualSense)
    });
    AssertEqual(DeviceTransportKind.Hid, ordered[0].TransportKind);
    AssertEqual(DeviceTransportKind.Hid, ordered[1].TransportKind);
    AssertEqual(DeviceTransportKind.Serial, ordered[2].TransportKind);
    AssertEqual(true, ordered[2].DiagnosticOnly);
}

static void BuildsSourceAwareDiagnostics()
{
    var status = new BridgeStatusSnapshot
    {
        UsbRole = BridgeUsbRole.DualSense,
        ActiveInput = BridgePhysicalInput.DualSense,
        InputValid = true,
        UsbMounted = true,
        FeedbackForwarded = 8,
        FeedbackFailed = 0,
        Ds5OutputReports = 10,
        Ds5OutputFailures = 0,
        AudioCapable = true,
        AudioOutPackets = 2,
        AudioBtReports = 2,
        AudioOutErrors = 0
    };
    var checks = BridgeDiagnostics.Evaluate(status, Hid("ds5", 0x054c, 0x0ce6, BridgeUsbRole.DualSense));
    AssertEqual(true, checks.Any(check => check.Name == "震动转发" && check.Detail.Contains("DS5")));
    AssertEqual(true, checks.Any(check => check.Name == "DS5 输出" && check.Detail.Contains("10")));
    AssertEqual(true, checks.Any(check => check.Name == "DS5 USB Audio"));
}

static void RejectsLongCommands()
{
    var rejected = false;
    try
    {
        FeatureReportProtocol.BuildFeatureCommandPayload(new string('x', 80), includeReportId: false);
    }
    catch (ArgumentException)
    {
        rejected = true;
    }
    AssertEqual(true, rejected);
}

static DeviceDescriptor Hid(string id, ushort vid, ushort pid, BridgeUsbRole role) =>
    new(id, id, vid, pid, DeviceTransportKind.Hid, "test", true, true, true, role);

static DeviceDescriptor Serial(string id) =>
    new(id, id, 0, 0, DeviceTransportKind.Serial, "test", true, false,
        DiagnosticOnly: true);

static void DecodesDs5UsbInput()
{
    var body = new byte[63];
    body[0] = 0;
    body[1] = 128;
    body[2] = 255;
    body[3] = 64;
    body[4] = 10;
    body[5] = 20;
    body[7] = 0x20 | 0x02; // Cross + D-pad right.
    body[8] = 0x11; // L1 + Create.
    body[9] = 0x41; // PS + left paddle.
    WriteInt16(body, 15, 101);
    WriteInt16(body, 17, 303);
    WriteInt16(body, 19, 202);
    WriteInt16(body, 21, -11);
    WriteInt16(body, 23, -22);
    WriteInt16(body, 25, -33);
    body[52] = 0x05;

    AssertEqual(true, ControllerInputDecoder.TryDecode(0x01, body, out var input));
    AssertEqual("DS5 USB input", input!.Source);
    AssertEqual(-32768, input.LeftX);
    AssertEqual(0, input.LeftY);
    AssertEqual(32767, input.RightX);
    AssertEqual(true, (input.Buttons & (1u << 0)) != 0);
    AssertEqual(true, (input.Buttons & (1u << 7)) != 0);
    AssertEqual(true, (input.Buttons & (1u << 20)) != 0);
    AssertEqual(101, input.GyroX);
    AssertEqual(202, input.GyroY);
    AssertEqual(303, input.GyroZ);
    AssertEqual(-22, input.AccelY);
    AssertEqual(50, input.BatteryPercent.GetValueOrDefault());
}

static void DecodesNs2UsbInput()
{
    var full = new byte[64];
    full[0] = 0x05;
    full[5] = 0x84; // South + right trigger.
    full[6] = 0x10; // Home.
    full[7] = 0x42; // D-pad up + L1.
    Pack12(full, 11, 0, 4095);
    Pack12(full, 14, 2048, 2048);
    WriteInt16(full, 49, 11);
    WriteInt16(full, 51, 22);
    WriteInt16(full, 53, 33);
    WriteInt16(full, 55, -44);
    WriteInt16(full, 57, -55);
    WriteInt16(full, 59, -66);

    AssertEqual(true, ControllerInputDecoder.TryDecode(0x05, full, out var input));
    AssertEqual("NS2 USB input", input!.Source);
    AssertEqual(-32768, input.LeftX);
    AssertEqual(32767, input.LeftY);
    AssertEqual(0, input.RightX);
    AssertEqual(true, (input.Buttons & (1u << 0)) != 0);
    AssertEqual(true, (input.Buttons & (1u << 4)) != 0);
    AssertEqual(true, (input.Buttons & (1u << 16)) != 0);
    AssertEqual(ushort.MaxValue, input.RightTrigger);
    AssertEqual(11, input.AccelX);
    AssertEqual(-55, input.GyroY);
}

static void Pack12(byte[] report, int offset, int x, int y)
{
    report[offset] = (byte)x;
    report[offset + 1] = (byte)(((x >> 8) & 0x0f) | ((y & 0x0f) << 4));
    report[offset + 2] = (byte)(y >> 4);
}

static void WriteInt16(byte[] report, int offset, short value)
{
    report[offset] = (byte)value;
    report[offset + 1] = (byte)(value >> 8);
}

static void BuildsWindowsFeatureCommandPayload()
{
    var payload = FeatureReportProtocol.BuildFeatureCommandPayload("usb status", includeReportId: true);
    AssertEqual(ManagerProtocol.FeatureReportLength, payload.Length);
    AssertEqual(ManagerProtocol.FeatureReportId, payload[0]);
    AssertAscii("Y7HID1usb status", payload.AsSpan(1, 16));
}

static void BuildsOutputCommandPayload()
{
    var payload = FeatureReportProtocol.BuildOutputCommandPayload("status");
    AssertEqual(ManagerProtocol.CommandPayloadLength, payload.Length);
    AssertAscii("Y7HID1status", payload.AsSpan(0, 12));
}

static void ParsesReplyWithReportId()
{
    var report = BuildReply("ok", includeReportId: true);
    var chunk = FeatureReportProtocol.ParseReplyChunk(report);
    AssertEqual(2, chunk.TotalLength);
    AssertEqual(0, chunk.Offset);
    AssertAscii("ok", chunk.Payload);
}

static void ParsesReplyWithoutReportId()
{
    var report = BuildReply("{\"ok\":true}", includeReportId: false);
    var chunk = FeatureReportProtocol.ParseReplyChunk(report);
    AssertEqual(11, chunk.TotalLength);
    AssertEqual(0, chunk.Offset);
    AssertAscii("{\"ok\":true}", chunk.Payload);
}

static void RetriesNativeDs5Feature()
{
    var native = new byte[ManagerProtocol.FeatureReportLength];
    native[0] = ManagerProtocol.DualSenseFeatureReportId;
    native[1] = 0x01;
    var manager = BuildReply("{\"ok\":true}", includeReportId: true);
    manager[0] = ManagerProtocol.DualSenseFeatureReportId;
    var transport = new ScriptedFeatureTransport(native, manager);
    using var reply = new ManagerCommandClient(transport)
        .SendCommandAsync("status", CancellationToken.None)
        .GetAwaiter().GetResult();
    AssertEqual(true, reply.RootElement.GetProperty("ok").GetBoolean());
    AssertEqual(2, transport.ReadCount);
    transport.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

static void FindsSf32lb52Xbox360ManagerProfile()
{
    var profile = RequireProfile(0x045e, 0x028e);
    AssertEqual("sf32lb52-xbox360", profile.Key);
    AssertEqual(ManagerProtocol.FeatureReportId, profile.ManagerFeatureReportId);
    AssertEqual(BridgeHidUsages.VendorDefinedPage, profile.ManagerUsagePage);
    AssertEqual(BridgeHidUsages.Manager, profile.ManagerUsageId);
    AssertEqual(false, profile.CommandOutputReportId.HasValue);
    AssertEqual(false, profile.SupportsInputReports);
    AssertEqual(BridgeUsbRole.Xbox, profile.UsbRole);
}

static void FindsSf32lb52DualSenseManagerProfile()
{
    var profile = RequireProfile(0x054c, 0x0ce6);
    AssertEqual("dualsense", profile.Key);
    AssertEqual(ManagerProtocol.DualSenseFeatureReportId, profile.ManagerFeatureReportId);
    AssertEqual(BridgeHidUsages.GenericDesktopPage, profile.ManagerUsagePage);
    AssertEqual(BridgeHidUsages.Gamepad, profile.ManagerUsageId);
    AssertEqual(false, profile.CommandOutputReportId.HasValue);
    AssertEqual(true, profile.SupportsInputReports);
    AssertEqual(BridgeUsbRole.DualSense, profile.UsbRole);
}

static void FindsSf32lb52DualSenseEdgeManagerProfile()
{
    var profile = RequireProfile(0x054c, 0x0df2);
    AssertEqual("dualsense-edge", profile.Key);
    AssertEqual(ManagerProtocol.DualSenseFeatureReportId, profile.ManagerFeatureReportId);
    AssertEqual(BridgeHidUsages.GenericDesktopPage, profile.ManagerUsagePage);
    AssertEqual(BridgeHidUsages.Gamepad, profile.ManagerUsageId);
    AssertEqual(false, profile.CommandOutputReportId.HasValue);
    AssertEqual(true, profile.SupportsInputReports);
    AssertEqual(BridgeUsbRole.DualSenseEdge, profile.UsbRole);
}

static void FindsSf32lb52NintendoManagerProfile()
{
    var profile = RequireProfile(0x057e, 0x2069);
    AssertEqual("ns2pro-nintendo", profile.Key);
    AssertEqual(ManagerProtocol.FeatureReportId, profile.ManagerFeatureReportId);
    AssertEqual(true, profile.SupportsInputReports);
    AssertEqual(BridgeHidUsages.VendorDefinedPage, profile.ManagerUsagePage);
    AssertEqual(BridgeHidUsages.Manager, profile.ManagerUsageId);
    AssertEqual(ManagerProtocol.NintendoCommandOutputReportId, profile.CommandOutputReportId.GetValueOrDefault());
    AssertEqual(BridgeUsbRole.NintendoNs2Pro, profile.UsbRole);
}

static void MatchesSonyManagerIdentities()
{
    var ds5 = RequireProfile(0x054c, 0x0ce6);
    var dse = RequireProfile(0x054c, 0x0df2);

    AssertEqual(true, ds5.MatchesManagerIdentity("DualSense HID", null));
    AssertEqual(true, ds5.MatchesManagerIdentity(null, "DualSense HID"));
    AssertEqual(false, ds5.MatchesManagerIdentity(null, "Wireless Controller"));
    AssertEqual(true, dse.MatchesManagerIdentity("DualSense Edge HID", null));
    AssertEqual(true, dse.MatchesManagerIdentity(null, "DualSense Edge HID"));
    AssertEqual(false, dse.MatchesManagerIdentity(
        null, "DualSense Edge Wireless Controller"));
}

static void KnownProfilesHaveUniqueProfileKeys()
{
    var uniqueCount = BridgeDeviceProfiles.All
        .Select(profile => (profile.Key, profile.VendorId, profile.ProductId))
        .Distinct()
        .Count();
    AssertEqual(BridgeDeviceProfiles.All.Count, uniqueCount);
}

static BridgeDeviceProfile RequireProfile(ushort vendorId, ushort productId)
{
    return BridgeDeviceProfiles.Find(vendorId, productId)
        ?? throw new InvalidOperationException($"Missing profile {vendorId:x4}:{productId:x4}.");
}

static byte[] BuildReply(string text, bool includeReportId)
{
    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
    var report = new byte[includeReportId ? 64 : 63];
    var offset = includeReportId ? 1 : 0;
    if (includeReportId)
    {
        report[0] = ManagerProtocol.FeatureReportId;
    }

    System.Text.Encoding.ASCII.GetBytes(ManagerProtocol.ReplyMagic).CopyTo(report, offset);
    report[offset + 6] = (byte)(bytes.Length & 0xff);
    report[offset + 7] = (byte)(bytes.Length >> 8);
    report[offset + 10] = (byte)bytes.Length;
    bytes.CopyTo(report, offset + 11);
    return report;
}

static void AssertAscii(string expected, ReadOnlySpan<byte> actual)
{
    var text = System.Text.Encoding.ASCII.GetString(actual);
    if (text != expected)
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{text}'.");
    }
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

sealed class ScriptedFeatureTransport(params byte[][] reports) : IDeviceTransport
{
    private readonly Queue<byte[]> _reports = new(reports);

    public DeviceDescriptor Descriptor { get; } = new(
        "scripted-ds5", "Scripted DS5", 0x054c, 0x0ce6,
        DeviceTransportKind.Hid, "test", true, false,
        ManagerFeatureReportId: ManagerProtocol.DualSenseFeatureReportId);

    public int ReadCount { get; private set; }

    public Task OpenAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task WriteFeatureReportAsync(byte reportId,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task WriteOutputReportAsync(byte reportId,
        ReadOnlyMemory<byte> payload, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<byte[]> ReadFeatureReportAsync(byte reportId,
        CancellationToken cancellationToken)
    {
        ReadCount++;
        return Task.FromResult(_reports.Dequeue());
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
