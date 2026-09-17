using System.Security.Cryptography;
using System.Text.Json;
using BridgeManager.Core;
using BridgeManager.Core.FirmwareModules;

internal static class FirmwareFlashTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "bridge flash tests " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var module = new TestModule(root);
            var firmware = new BridgeFirmwareDefinition("test", "test", "1.0", "",
                ["nano"], FirmwareFlashMethod.SifliSerial, "sftool_param.json", "");
            var main = Path.Combine(root, "main.bin");
            File.WriteAllBytes(main, Enumerable.Range(0, 512).Select(index => (byte)index).ToArray());
            var parameters = Path.Combine(root, "sftool_param.json");
            void WriteParameters(params (string Path, string Address)[] files) =>
                File.WriteAllText(parameters, JsonSerializer.Serialize(new
                {
                    chip = "SF32LB52", memory = "NOR",
                    write_flash = new { files = files.Select(file => new { path = file.Path, address = file.Address }) }
                }));
            WriteParameters(("main.bin", "0x12020000"));
            var info = FirmwareFlashService.DescribeArtifact(module, firmware);
            Require(info.Available && info.TotalBytes == 512 && info.FileCount == 1,
                "Valid bundled firmware was not recognized");
            Require(info.MainSha256 == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(main))).ToLowerInvariant(),
                "Displayed firmware hash does not match bundled main.bin");
            var service = new FirmwareFlashService(() => "test-sftool.exe", () => ["COM7"]);
            Require(service.Check(module, firmware, "COM7").Ready, "Enumerated port failed preflight");
            Require(!service.Check(module, firmware, null).Ready, "Missing port accepted");
            Require(!service.Check(module, firmware, "COM700").Ready, "Nonexistent port accepted");
            Require(!new FirmwareFlashService(() => null, () => ["COM7"])
                .Check(module, firmware, "COM7").Ready, "Missing download tool accepted");
            var toolRoot = Path.Combine(root, "app", "tools", "sftool");
            Directory.CreateDirectory(toolRoot);
            var tool = Path.Combine(toolRoot, "sftool.exe");
            File.WriteAllBytes(tool, [1, 2, 3]);
            Require(FirmwareFlashService.ResolveSifliTool(Path.Combine(root, "app")) == tool,
                "Bundled tool was not preferred");
            var command = FirmwareFlashService.CreateSifliWriteProcess(tool, parameters, "COM7");
            var args = command.ArgumentList.ToArray();
            Require(!command.UseShellExecute && command.CreateNoWindow &&
                args.Contains("--verify") && !args.Contains("--erase-all") &&
                args.Contains($"{main}@0x12020000") && args[1] == "COM7",
                "Safe verified flashing arguments were not preserved");
            // All tests below validate files only. No process is started or COM port opened.
            WriteParameters();
            Require(!service.Check(module, firmware, "COM7").Ready, "Empty write list accepted");
            WriteParameters(("main.bin", "0x12020000"), ("main.bin", "0x12020080"));
            Require(!service.Check(module, firmware, "COM7").Ready, "Overlapping writes accepted");
            WriteParameters(("main.bin", "0x12fffff0"));
            Require(!service.Check(module, firmware, "COM7").Ready, "Write beyond NOR boundary accepted");
            WriteParameters(("../main.bin", "0x12020000"));
            Require(!service.Check(module, firmware, "COM7").Ready, "Path traversal accepted");
            WriteParameters(("main.bin:stream", "0x12020000"));
            Require(!service.Check(module, firmware, "COM7").Ready, "Alternate data stream accepted");
            WriteParameters(("missing.bin", "0x12020000"));
            Require(!service.Check(module, firmware, "COM7").Ready, "Missing image accepted");
            WriteParameters(("main.bin", "0x12020000"));
            File.WriteAllBytes(main, []);
            Require(!service.Check(module, firmware, "COM7").Ready, "Empty main.bin accepted");
            Require(!FirmwareFlashService.DescribeArtifact(module, firmware).Available,
                "Invalid bundled image advertised as available");

            var blRoot = Path.Combine(root, "bl616");
            Directory.CreateDirectory(blRoot);
            var blFiles = new[]
            {
                ("boot2", "boot2.bin", "0x000000", new byte[] { 1, 2, 3 }),
                ("partition", "partition.bin", "0x00e000", new byte[] { 4, 5 }),
                ("firmware", "firmware.bin", "@partition", new byte[] { 6, 7, 8, 9 })
            };
            foreach (var file in blFiles)
                File.WriteAllBytes(Path.Combine(blRoot, file.Item2), file.Item4);
            var blManifest = Path.Combine(blRoot, "blflash.json");
            void WriteBlManifest(string chip = "bl616", bool corruptHash = false) =>
                File.WriteAllText(blManifest, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, chip, baudRate = 2_000_000,
                    files = blFiles.Select((file, index) => new
                    {
                        kind = file.Item1, path = file.Item2, address = file.Item3,
                        sha256 = corruptHash && index == 2 ? new string('0', 64) :
                            Convert.ToHexString(SHA256.HashData(file.Item4)).ToLowerInvariant()
                    })
                }));
            WriteBlManifest();
            var blFirmware = new BridgeFirmwareDefinition("bl", "bl", "1.0", "",
                ["bl616"], FirmwareFlashMethod.BouffaloUart,
                "bl616/blflash.json", "");
            var blService = new FirmwareFlashService(() => null, () => ["COM8"],
                () => "BLFlashCommand.exe");
            Require(blService.Check(module, blFirmware, "COM8").Ready,
                "Valid BL616 bundle and enumerated bridge were rejected");
            Require(FirmwareFlashService.DescribeArtifact(module, blFirmware).Available,
                "Valid BL616 bundle was not advertised");
            var fakeTool = Environment.ProcessPath!;
            Require(Path.GetFileName(fakeTool).Equals("BridgeManager.Core.Tests.exe", StringComparison.OrdinalIgnoreCase),
                "Launcher regression must run using the test executable, never a vendor tool");
            var launcherService = new FirmwareFlashService(() => null, () => ["COM8", "COM9"], () => fakeTool);
            var launch = launcherService.FlashAsync(module, blFirmware, "COM8", null,
                CancellationToken.None).GetAwaiter().GetResult();
            Require(launch.Success, "BL616 child process/output/config-lifetime regression: " + launch.Message);
            var failedLaunch = launcherService.FlashAsync(module, blFirmware, "COM9", null,
                CancellationToken.None).GetAwaiter().GetResult();
            Require(!failedLaunch.Success && failedLaunch.Message.Contains("23") &&
                failedLaunch.Message.Contains("FAKE_BLFLASH_STDERR_CAPTURED"),
                "BL616 launcher lost the child's exit code or stderr");
            WriteBlManifest(corruptHash: true);
            Require(!blService.Check(module, blFirmware, "COM8").Ready,
                "BL616 image with the wrong hash was accepted");
            WriteBlManifest(chip: "bl618");
            Require(!blService.Check(module, blFirmware, "COM8").Ready,
                "BL616 flasher accepted a different chip target");
            var unsupported = firmware with { FlashMethod = FirmwareFlashMethod.None, ArtifactRelativePath = null };
            Require(!FirmwareFlashService.DescribeArtifact(module, unsupported).Available,
                "Management-only firmware advertised as bundled");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Require(bool value, string error)
    {
        if (!value) throw new InvalidOperationException(error);
    }

    private sealed class TestModule(string root) : BridgeFirmwareModuleBase
    {
        public override string Id => "flash-test";
        public override string DisplayName => "Test";
        public override string BoardFamily => "Test";
        public override string Description => "";
        public override string? PackageRoot => root;
        public override BridgeCapability Capabilities => BridgeCapability.FirmwareFlashing;
        public override bool Matches(DeviceDescriptor descriptor) => false;
    }
}
