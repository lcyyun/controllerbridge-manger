using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BridgeManager.Core.FirmwareModules;

public sealed partial class FirmwareFlashService
{
    private FirmwareFlashPreflight CheckEspressif(string artifact,
        BridgeFirmwareDefinition firmware, string? serialPort)
    {
        if (string.IsNullOrWhiteSpace(serialPort) || !Regex.IsMatch(serialPort, "^COM[1-9][0-9]{0,3}$", RegexOptions.IgnoreCase) ||
            !_serialPorts().Contains(serialPort, StringComparer.OrdinalIgnoreCase))
            return new(false, "请选择 ESP32-S3 下载串口。游戏手柄原生 USB 与板载 USB 转串口接口用途不同。", artifact, serialPort);
        if (_espressifTool() is null)
            return new(false, "缺少经过校验的内置 esptool 4.8.1，请使用完整 Manager 安装包。", artifact, serialPort);
        try
        {
            var bundle = Esp32S3FlashBundle.Read(artifact);
            bundle.ValidateFirmware(firmware);
            return new(true, $"{serialPort} · {bundle.Board} · 三个镜像及 SHA-256 校验通过。" +
                $"开始写入前会检查芯片及 {bundle.FlashSizeMb} MB Flash；请核对所选板型的 {bundle.PsramSizeMb} MB PSRAM。", artifact, serialPort);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or
            InvalidOperationException or UnauthorizedAccessException or ArgumentException or KeyNotFoundException)
        {
            return new(false, $"ESP32-S3 固件包校验失败：{ex.Message}", artifact, serialPort);
        }
    }

    private async Task<FirmwareFlashResult> FlashEspressifAsync(string manifest,
        BridgeFirmwareDefinition firmware, string serialPort, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var bundle = Esp32S3FlashBundle.Read(manifest);
        bundle.ValidateFirmware(firmware);
        var executable = _espressifTool() ?? throw new FileNotFoundException("Pinned esptool is missing.");
        progress?.Report($"正在识别 {serialPort} 的 ESP32-S3 芯片与 Flash 容量...");
        var probe = await RunProcessAsync(CreateEspressifProcess(executable, manifest, serialPort, "flash_id"), cancellationToken);
        var probeText = probe.Output + "\n" + probe.Error;
        if (probe.ExitCode != 0 || !ValidateEspressifProbe(probeText, bundle.FlashSizeMb))
            return new(false, $"ESP32-S3 / {bundle.FlashSizeMb} MB Flash 识别失败，尚未写入。请按住 BOOT 后按 RESET 进入下载模式。\n{probeText.Trim()}");
        // Recheck all files immediately before writing. The module supplies
        // images, never an executable, shell command, address override or erase-all.
        Esp32S3FlashBundle.Read(manifest).ValidateFirmware(firmware);
        progress?.Report($"正在写入 {bundle.Board} 的 bootloader、partition、application...");
        var write = await RunProcessAsync(CreateEspressifProcess(executable, manifest, serialPort, "write_flash"), cancellationToken);
        if (write.ExitCode != 0)
            return new(false, $"ESP32-S3 写入失败（{write.ExitCode}）。重新进入 BOOT 下载模式可重试；未擦除其他分区。\n{write.Output}\n{write.Error}");
        progress?.Report("正在回读校验三个 Flash 镜像...");
        var verify = await RunProcessAsync(CreateEspressifProcess(executable, manifest, serialPort, "verify_flash"), cancellationToken);
        if (verify.ExitCode != 0)
            return new(false, $"ESP32-S3 回读校验失败（{verify.ExitCode}）。重新进入 BOOT 下载模式后重试。\n{verify.Output}\n{verify.Error}");
        progress?.Report("三个镜像写入和回读校验完成，已请求复位。");
        return new(true, "ESP32-S3 固件写入和回读校验完成。请使用原生 USB 数据口连接 Manager / 游戏主机。");
    }

    internal static bool ValidateEspressifProbe(string output, int expectedFlashMb) =>
        Regex.IsMatch(output, @"(?m)^Chip is ESP32-S3(?:\s|$)") &&
        Regex.IsMatch(output, $@"(?m)^Detected flash size:\s*{expectedFlashMb}MB\s*$");

    internal static ProcessStartInfo CreateEspressifProcess(string executable,
        string manifest, string serialPort, string command)
    {
        var bundle = Esp32S3FlashBundle.Read(manifest);
        if (command is not ("flash_id" or "write_flash" or "verify_flash") ||
            !Regex.IsMatch(serialPort, "^COM[1-9][0-9]{0,3}$", RegexOptions.IgnoreCase))
            throw new ArgumentException("Invalid ESP32-S3 command or serial port.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(manifest)!
        };
        foreach (var argument in new[] { "--chip", "esp32s3", "--port", serialPort,
            "--baud", "460800", "--before", "default_reset", "--after",
            command == "verify_flash" ? "hard_reset" : "no_reset", command })
            start.ArgumentList.Add(argument);
        if (command != "flash_id")
        {
            foreach (var argument in new[] { "--flash_mode", "dio", "--flash_freq", "80m",
                "--flash_size", $"{bundle.FlashSizeMb}MB" })
                start.ArgumentList.Add(argument);
            foreach (var file in bundle.Files)
            {
                start.ArgumentList.Add($"0x{file.Address:x}");
                start.ArgumentList.Add(file.Path);
            }
        }
        return start;
    }

    public static string? ResolveEspressifTool(string? applicationDirectory = null)
    {
        var tool = Path.Combine(applicationDirectory ?? AppContext.BaseDirectory,
            "tools", "esptool", "esptool.exe");
        // No PATH or module-defined executable fallback. The distributor pins
        // the same official binary in tools/vendor/esptool/PROVENANCE.json.
        try
        {
            if (!File.Exists(tool)) return null;
            using var input = File.OpenRead(tool);
            return Convert.ToHexString(SHA256.HashData(input)).Equals(
                EspressifToolSha256, StringComparison.OrdinalIgnoreCase) ? tool : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal const string EspressifToolSha256 = "069db0f1e4d8f929f36d0f60243499eb670626c03a686376728998aaaad2f664";
}
