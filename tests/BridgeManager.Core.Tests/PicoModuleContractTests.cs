using System.Text.Json;
using BridgeManager.Core;
using BridgeManager.Core.FirmwareModules;
using BridgeManager.Core.Transports;

internal static class PicoModuleContractTests
{
    public static void Run(string manifest)
    {
        const string bootInfo = "UF2 Bootloader v3.0\r\nModel: Raspberry Pi RP2350\r\nBoard-ID: RP2350\r\n";
        if (!FirmwareFlashService.IsPico2BootVolume("RP2350", bootInfo) ||
            !FirmwareFlashService.IsPico2BootVolume("RPI-RP2", bootInfo) ||
            FirmwareFlashService.IsPico2BootVolume("RPI-RP2", bootInfo.Replace("RP2350", "RPI-RP2")) ||
            FirmwareFlashService.IsPico2BootVolume("RP2350", "") ||
            FirmwareFlashService.IsPico2BootVolume("RP2350", bootInfo + "Board-ID: RP2040\n") ||
            FirmwareFlashService.IsPico2BootVolume("SYSTEM", bootInfo))
            throw new Exception("BOOTSEL must recognize RP2350 and reject wrong-chip/ambiguous volumes.");
        var root = Path.Combine(Path.GetTempPath(), "pico-module-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.Copy(manifest, Path.Combine(root, "module.json"));
            var registry = new BridgeFirmwareModuleRegistry(moduleDirectories: [root]);
            if (registry.LoadIssues.Count != 0) throw new Exception(string.Join("; ", registry.LoadIssues));
            var module = registry.Find("pico-unified-bridge") ?? throw new Exception("Pico module missing.");
            if (module.RuntimeApiVersion != 2 || module.ModuleVersion != "0.2.0-preview.1")
                throw new Exception("Packaged module did not replace the built-in.");
            if (module.UsbRoles.Select(role => role.Role).Distinct().Count() != 4)
                throw new Exception("Four independently selectable USB identities required.");
            var fixtures = new[]
            {
                (Vid: 0xcafe, Pid: 0x4012, Product: "Pico 2 W Controller Bridge Manager", Key: "pico-auto-manager"),
                (Vid: 0x054c, Pid: 0x0ce6, Product: "Pico 2 W DualSense HID", Key: "pico-dualsense"),
                (Vid: 0x054c, Pid: 0x0df2, Product: "Pico 2 W DualSense Edge HID", Key: "pico-dualsense-edge"),
                (Vid: 0xcafe, Pid: 0x4013, Product: "Pico 2 W XInput Bridge", Key: "pico-xinput"),
                (Vid: 0x057e, Pid: 0x2069, Product: "Pico 2 W NS2Pro Bridge", Key: "pico-ns2pro")
            };
            foreach (var fixture in fixtures)
            {
                // Both Sony feature IDs exist: the JSON 7F profile must win over legacy F6.
                var profile = WindowsHidTransportFactory.OrderProfilesForDiscovery(registry.GetHidDeviceProfiles())
                    .First(item => item.Matches((ushort)fixture.Vid, (ushort)fixture.Pid) &&
                        item.MatchesManagerIdentity("CBPICO-TEST-0001", fixture.Product) &&
                        item.ManagerFeatureReportId is 0x7f or 0xf6);
                if (profile.Key != fixture.Key || profile.ManagerFeatureReportId != 0x7f)
                    throw new Exception("Wrong discovery protocol for " + fixture.Key);
                var descriptor = new DeviceDescriptor(fixture.Key, fixture.Product, profile.VendorId,
                    profile.ProductId, DeviceTransportKind.Hid, profile.DisplayName, true, false,
                    ProfileKey: profile.Key, SerialNumber: "CBPICO-TEST-0001", ProductName: fixture.Product,
                    ManagerFeatureReportId: profile.ManagerFeatureReportId);
                if (registry.Resolve(descriptor).Id != module.Id)
                    throw new Exception("Wrong module resolution for " + fixture.Key);
            }
            var controls = module.Pages.SelectMany(page => page.Sections).SelectMany(section => section.Controls)
                .Where(control => control.Type == BridgeModuleControlType.MappingEditor).ToArray();
            if (controls.Length != 2 || controls.Any(control => control.MappingOutput is not null) ||
                !controls.Select(control => control.MappingProfile).Order().SequenceEqual(new[] { "ds5", "ns2pro" }))
                throw new Exception("Mapping pages must match the two physical-source tables.");
            foreach (var control in controls)
            {
                var entries = BridgeButtonMapping.ControlIds.ToDictionary(id => id, id => id);
                entries["left_function"] = "c";
                entries["right_paddle"] = "mute";
                var reply = JsonSerializer.SerializeToElement(new { ok = true, profile = control.MappingProfile, entries });
                if (BridgeButtonMapping.ReadReply(control, reply).Count != 25)
                    throw new Exception("Extended button mapping incomplete.");
                if (module.Operations[control.ApplyAction!].Request != "mapping set {profile} {target} {source}")
                    throw new Exception("Mapping operation dimensions do not match firmware.");
            }
            foreach (var operation in new[] { "settings.read", "settings.save", "settings.reset", "system.reboot", "system.bootloader" })
                if (!module.Operations.ContainsKey(operation)) throw new Exception("Missing recovery operation " + operation);
            Console.WriteLine("PASS Pico runtime 2 module: identity discovery, 7F precedence, four roles, 25-key per-source mapping and recovery");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
