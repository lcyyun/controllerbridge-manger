using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Security.Cryptography;
using System.Text.Json;

namespace BridgeManager.Core.FirmwareModules;

public sealed record FirmwareFlashPreflight(
    bool Ready,
    string Message,
    string? ArtifactPath,
    string? Target);

public sealed record FirmwareFlashResult(bool Success, string Message);

public sealed record FirmwareArtifactDetails(
    bool Available, string Message, int FileCount = 0,
    long TotalBytes = 0, string? MainSha256 = null, DateTime? FileTimeUtc = null);

public sealed class FirmwareFlashService
{
    private readonly Func<string?> _sifliTool;
    private readonly Func<string?> _bouffaloTool;
    private readonly Func<string[]> _serialPorts;

    public FirmwareFlashService() : this(() => ResolveSifliTool(),
        SerialPort.GetPortNames, () => ResolveBouffaloTool()) { }

    internal FirmwareFlashService(Func<string?> sifliTool, Func<string[]> serialPorts)
        : this(sifliTool, serialPorts, () => ResolveBouffaloTool()) { }

    internal FirmwareFlashService(Func<string?> sifliTool,
        Func<string[]> serialPorts, Func<string?> bouffaloTool)
    {
        _sifliTool = sifliTool;
        _serialPorts = serialPorts;
        _bouffaloTool = bouffaloTool;
    }

    public static FirmwareArtifactDetails DescribeArtifact(
        IBridgeFirmwareModule module, BridgeFirmwareDefinition firmware)
    {
        var artifact = ResolveArtifact(module, firmware);
        if (artifact is null)
            return new(false, firmware.FlashMethod == FirmwareFlashMethod.None
                ? "未内置烧录镜像 · 当前仅支持设备管理"
                : "固件文件缺失 · 请使用完整应用包");
        try
        {
            ValidateArtifactBundle(firmware.FlashMethod, artifact);
            var files = new[] { artifact };
            var main = artifact;
            if (firmware.FlashMethod == FirmwareFlashMethod.SifliSerial)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(artifact));
                files = document.RootElement.GetProperty("write_flash").GetProperty("files")
                    .EnumerateArray().Select(file => ResolveSifliFile(artifact,
                        file.GetProperty("path").GetString()!)).ToArray();
                main = files.Single(file => Path.GetFileName(file).Equals(
                    "main.bin", StringComparison.OrdinalIgnoreCase));
            }
            else if (firmware.FlashMethod == FirmwareFlashMethod.BouffaloUart)
            {
                var bundle = ReadBouffaloBundle(artifact);
                files = bundle.Files.Select(file => file.Path).ToArray();
                main = bundle.Files.Single(file => file.Kind == "firmware").Path;
            }
            using var input = File.OpenRead(main);
            return new(true, "已内置固件", files.Length,
                files.Sum(file => new FileInfo(file).Length),
                Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(),
                File.GetLastWriteTimeUtc(main));
        }
        catch (Exception ex) when (ex is IOException or JsonException or
            InvalidDataException or KeyNotFoundException or InvalidOperationException or
            UnauthorizedAccessException or ArgumentException)
        {
            return new(false, $"固件不可用：{ex.Message}");
        }
    }

    public FirmwareFlashPreflight Check(
        IBridgeFirmwareModule module,
        BridgeFirmwareDefinition firmware,
        string? serialPort)
    {
        if (firmware.FlashMethod == FirmwareFlashMethod.None)
        {
            return new(false,
                "该固件由模块提供识别与管理信息，请使用对应平台工具手动烧录。",
                null, null);
        }
        var artifact = ResolveArtifact(module, firmware);
        if (artifact is null)
        {
            return new(false,
                "当前应用包缺少固件文件。请使用包含固件的完整应用包。",
                null, null);
        }

        return firmware.FlashMethod switch
        {
            FirmwareFlashMethod.PicoUf2 => CheckPico(artifact),
            FirmwareFlashMethod.SifliSerial => CheckSifli(artifact, serialPort),
            FirmwareFlashMethod.BouffaloUart => CheckBouffalo(artifact, serialPort),
            _ => new(false, "该固件模块没有提供自动烧录方式。", artifact, null)
        };
    }

    public async Task<FirmwareFlashResult> FlashAsync(
        IBridgeFirmwareModule module,
        BridgeFirmwareDefinition firmware,
        string? serialPort,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var check = Check(module, firmware, serialPort);
        if (!check.Ready || check.ArtifactPath is null)
        {
            return new(false, check.Message);
        }

        return firmware.FlashMethod switch
        {
            FirmwareFlashMethod.PicoUf2 => await FlashPicoAsync(
                check.ArtifactPath, check.Target!, progress, cancellationToken),
            FirmwareFlashMethod.SifliSerial => await FlashSifliAsync(
                check.ArtifactPath, serialPort!, progress, cancellationToken),
            FirmwareFlashMethod.BouffaloUart => await FlashBouffaloAsync(
                check.ArtifactPath, serialPort!, progress, cancellationToken),
            _ => new(false, "该固件模块没有提供自动烧录方式。")
        };
    }

    public static string? ResolveArtifact(
        IBridgeFirmwareModule module,
        BridgeFirmwareDefinition firmware)
    {
        if (string.IsNullOrWhiteSpace(firmware.ArtifactRelativePath))
        {
            return null;
        }

        var relative = firmware.ArtifactRelativePath.Replace(
            '/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) || relative.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries).Contains(".."))
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(module.PackageRoot))
        {
            var packageRoot = Path.GetFullPath(module.PackageRoot);
            var candidate = Path.GetFullPath(Path.Combine(packageRoot, relative));
            var prefix = packageRoot.TrimEnd(Path.DirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix,
                       StringComparison.OrdinalIgnoreCase) && File.Exists(candidate)
                ? candidate : null;
        }
        var roots = new List<string>();
        roots.Add(AppContext.BaseDirectory);

        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            roots.Add(cursor.FullName);
            cursor = cursor.Parent;
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.GetFullPath(Path.Combine(root, relative));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static void ValidateArtifactBundle(FirmwareFlashMethod method,
                                              string artifact)
    {
        if (!File.Exists(artifact))
        {
            throw new InvalidDataException("Firmware artifact is missing.");
        }
        switch (method)
        {
            case FirmwareFlashMethod.PicoUf2:
                ValidateUf2(artifact);
                break;
            case FirmwareFlashMethod.SifliSerial:
                using (var document = JsonDocument.Parse(File.ReadAllText(artifact)))
                {
                    var root = document.RootElement;
                    ValidateSifliTarget(root);
                    var ranges = new List<(uint Address, long End)>();
                    var mainCount = 0;
                    foreach (var file in root.GetProperty("write_flash")
                                 .GetProperty("files").EnumerateArray())
                    {
                        var path = ResolveSifliFile(artifact,
                            file.GetProperty("path").GetString() ?? "");
                        if (!File.Exists(path))
                        {
                            throw new InvalidDataException(
                                $"Firmware package is missing {path}.");
                        }
                        var length = new FileInfo(path).Length;
                        var address = uint.Parse(file.GetProperty("address").GetString()![2..],
                            NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        var end = address + length;
                        if (length == 0 || end > 0x13000000L ||
                            ranges.Any(range => address < range.End && end > range.Address))
                            throw new InvalidDataException("固件文件为空、超出 Flash 范围或写入区域重叠。");
                        ranges.Add((address, end));
                        if (Path.GetFileName(path).Equals("main.bin", StringComparison.OrdinalIgnoreCase))
                            mainCount++;
                    }
                    if (mainCount != 1)
                        throw new InvalidDataException("烧录包必须包含一个 main.bin。");
                }
                break;
            case FirmwareFlashMethod.BouffaloUart:
                _ = ReadBouffaloBundle(artifact);
                break;
        }
    }

    private FirmwareFlashPreflight CheckBouffalo(string artifact,
                                                  string? serialPort)
    {
        if (string.IsNullOrWhiteSpace(serialPort))
            return new(false, "请选择已处于下载模式的 BL616 串口。", artifact, null);
        if (!_serialPorts().Contains(serialPort, StringComparer.OrdinalIgnoreCase))
            return new(false, $"Windows 当前没有枚举到 {serialPort}，请刷新串口。",
                artifact, serialPort);
        var tool = _bouffaloTool();
        if (tool is null)
            return new(false,
                "未找到 BLFlashCommand。请安装包含 Bouffalo 烧录工具的完整应用包。",
                artifact, serialPort);
        try
        {
            ValidateArtifactBundle(FirmwareFlashMethod.BouffaloUart, artifact);
        }
        catch (Exception ex) when (ex is IOException or JsonException or
                                   InvalidDataException or InvalidOperationException or
                                   UnauthorizedAccessException or ArgumentException)
        {
            return new(false, $"BL616 固件包校验失败：{ex.Message}", artifact,
                serialPort);
        }
        return new(true,
            $"{serialPort} · BL616 文件及 SHA-256 校验通过 · 请确认设备已进入下载模式 · 烧录工具：{tool}",
            artifact, serialPort);
    }

    private async Task<FirmwareFlashResult> FlashBouffaloAsync(
        string manifestPath,
        string serialPort,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var bundle = ReadBouffaloBundle(manifestPath);
        var executable = _bouffaloTool() ??
            throw new FileNotFoundException("BLFlashCommand was not found.");
        var configPath = Path.Combine(Path.GetTempPath(),
            $"controller-bridge-bl616-{Guid.NewGuid():N}.ini");
        try
        {
            await File.WriteAllTextAsync(configPath, BuildBouffaloConfig(bundle),
                cancellationToken);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(manifestPath)!
            };
            start.ArgumentList.Add("--interface=uart");
            start.ArgumentList.Add($"--baudrate={bundle.BaudRate}");
            start.ArgumentList.Add($"--port={serialPort}");
            start.ArgumentList.Add($"--chipname={bundle.Chip}");
            start.ArgumentList.Add($"--config={configPath}");
            // BLFlashCommand --reset leaves this board in ROM download mode;
            // --warm_reset is the path that starts the freshly written image.
            start.ArgumentList.Add("--warm_reset");
            progress?.Report($"正在通过 {serialPort} 以 {bundle.BaudRate:N0} baud 写入 BL616...");
            var result = await RunProcessAsync(start, cancellationToken);
            var output = $"{result.Output}\n{result.Error}";
            if (result.ExitCode != 0 ||
                !output.Contains("Flash writing succeeded", StringComparison.OrdinalIgnoreCase) &&
                !output.Contains("programming completed", StringComparison.OrdinalIgnoreCase))
                return new(false,
                    $"BLFlashCommand 失败（{result.ExitCode}）：{output.Trim()}");
        }
        finally
        {
            if (File.Exists(configPath)) File.Delete(configPath);
        }
        progress?.Report("BL616 写入完成，已请求热复位并启动新固件。");
        return new(true, "BL616 固件烧录完成，正在等待 USB 重新枚举。");
    }

    private sealed record BouffaloFile(string Kind, string Path, string Address);
    private sealed record BouffaloBundle(string Chip, int BaudRate,
        IReadOnlyList<BouffaloFile> Files);

    private static BouffaloBundle ReadBouffaloBundle(string manifestPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
            !root.GetProperty("chip").GetString()!.Equals("bl616",
                StringComparison.OrdinalIgnoreCase) ||
            root.GetProperty("baudRate").GetInt32() != 2_000_000)
            throw new InvalidDataException("目标必须是 BL616 和 2,000,000 baud。");
        var files = new List<BouffaloFile>();
        foreach (var item in root.GetProperty("files").EnumerateArray())
        {
            var kind = item.GetProperty("kind").GetString()?.ToLowerInvariant() ?? "";
            var relative = item.GetProperty("path").GetString() ?? "";
            var address = item.GetProperty("address").GetString()?.ToLowerInvariant() ?? "";
            var expectedHash = item.GetProperty("sha256").GetString()?.ToLowerInvariant() ?? "";
            var path = ResolvePackageFile(manifestPath, relative);
            if (new FileInfo(path).Length is <= 0 or > 4_194_304)
                throw new InvalidDataException($"{relative} 文件大小无效。");
            using var input = File.OpenRead(path);
            var actualHash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
            if (expectedHash.Length != 64 || actualHash != expectedHash)
                throw new InvalidDataException($"{relative} 的 SHA-256 不匹配。");
            files.Add(new(kind, path, address));
        }
        if (files.Count != 3 || files.Select(file => file.Kind).Distinct().Count() != 3 ||
            files.Count(file => file.Kind == "boot2" && file.Address == "0x000000") != 1 ||
            files.Count(file => file.Kind == "partition" && file.Address == "0x00e000") != 1 ||
            files.Count(file => file.Kind == "firmware" && file.Address == "@partition") != 1)
            throw new InvalidDataException("烧录包必须包含唯一的 boot2、partition 和 firmware。");
        return new("bl616", 2_000_000, files);
    }

    private static string ResolvePackageFile(string manifestPath, string relative)
    {
        relative = relative.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries).Contains(".."))
            throw new InvalidDataException("固件文件路径无效。");
        var root = Path.GetFullPath(Path.GetDirectoryName(manifestPath)!);
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidDataException($"固件文件缺失：{relative}");
        return path;
    }

    private static string BuildBouffaloConfig(BouffaloBundle bundle)
    {
        var boot2 = bundle.Files.Single(file => file.Kind == "boot2");
        var partition = bundle.Files.Single(file => file.Kind == "partition");
        var firmware = bundle.Files.Single(file => file.Kind == "firmware");
        static string P(string path) => path.Replace('\\', '/');
        return $"[cfg]\nerase = 1\nskip_mode = 0x0, 0x0\nboot2_isp_mode = 0\n\n" +
               $"[boot2]\nfiledir = {P(boot2.Path)}\naddress = {boot2.Address}\n\n" +
               $"[partition]\nfiledir = {P(partition.Path)}\naddress = {partition.Address}\n\n" +
               $"[FW]\nfiledir = {P(firmware.Path)}\naddress = {firmware.Address}\n";
    }

    private static FirmwareFlashPreflight CheckPico(string artifact)
    {
        try
        {
            ValidateArtifactBundle(FirmwareFlashMethod.PicoUf2, artifact);
        }
        catch (InvalidDataException ex)
        {
            return new(false, $"UF2 固件无效：{ex.Message}", artifact, null);
        }
        var drives = FindPicoBootDrives();
        if (drives.Count == 0)
        {
            return new(false,
                "未发现 RPI-RP2。按住 BOOTSEL 接入 Pico 2 W 后重新检查。",
                artifact, null);
        }
        if (drives.Count > 1)
        {
            return new(false,
                "发现多个 RPI-RP2 盘。请只保留一块待烧录 Pico。",
                artifact, null);
        }
        return new(true,
            $"已找到 BOOTSEL 磁盘 {drives[0].RootDirectory.FullName}",
            artifact, drives[0].RootDirectory.FullName);
    }

    private FirmwareFlashPreflight CheckSifli(string artifact,
                                                      string? serialPort)
    {
        if (string.IsNullOrWhiteSpace(serialPort))
        {
            return new(false, "请选择 SF32 下载串口。", artifact, null);
        }
        if (!_serialPorts().Contains(serialPort, StringComparer.OrdinalIgnoreCase))
            return new(false, $"Windows 当前没有枚举到 {serialPort}，请刷新下载串口。",
                artifact, serialPort);
        var tool = _sifliTool();
        if (tool is null)
        {
            return new(false,
                "未找到 SF32 烧录工具。请使用包含 tools/sftool 的完整应用包。",
                artifact, serialPort);
        }
        try
        {
            ValidateArtifactBundle(FirmwareFlashMethod.SifliSerial, artifact);
        }
        catch (Exception ex) when (ex is IOException or JsonException or
                                   KeyNotFoundException or InvalidDataException or
                                   InvalidOperationException or UnauthorizedAccessException or
                                   ArgumentException)
        {
            return new(false, $"无法读取 sftool 参数：{ex.Message}", artifact,
                serialPort);
        }
        return new(true, $"{serialPort} · 固件文件检查通过 · 烧录工具：{tool}", artifact,
            serialPort);
    }

    private static async Task<FirmwareFlashResult> FlashPicoAsync(
        string artifact,
        string targetRoot,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var destination = Path.Combine(targetRoot, Path.GetFileName(artifact));
        progress?.Report("正在复制 UF2...");
        await using var source = File.OpenRead(artifact);
        await using var target = File.Create(destination);
        await source.CopyToAsync(target, cancellationToken);
        await target.FlushAsync(cancellationToken);
        progress?.Report("UF2 已写入，等待 Pico 自动重启。");
        return new(true, "Pico UF2 烧录完成。等待设备重新枚举后点击扫描。")
        ;
    }

    private async Task<FirmwareFlashResult> FlashSifliAsync(
        string parameterPath,
        string serialPort,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(parameterPath, cancellationToken));
        var root = document.RootElement;
        var (chip, memory) = ValidateSifliTarget(root);
        var executable = _sifliTool() ??
            throw new FileNotFoundException("sftool was not found.");

        var files = root.GetProperty("write_flash").GetProperty("files")
            .EnumerateArray().ToArray();
        var start = CreateSifliWriteProcess(executable, parameterPath, serialPort);

        progress?.Report($"正在通过 {serialPort} 写入 SF32...");
        var writeResult = await RunProcessAsync(start, cancellationToken);
        if (writeResult.ExitCode != 0)
        {
            return new(false,
                $"sftool 失败（{writeResult.ExitCode}）：" +
                $"{writeResult.Error.Trim()} {writeResult.Output.Trim()}".Trim());
        }

        var main = files.FirstOrDefault(file => Path.GetFileName(
            file.GetProperty("path").GetString() ?? "").Equals(
                "main.bin", StringComparison.OrdinalIgnoreCase));
        if (main.ValueKind == JsonValueKind.Undefined ||
            !main.TryGetProperty("address", out var mainAddress) ||
            mainAddress.ValueKind != JsonValueKind.String)
        {
            return new(false, "烧录参数中缺少 main.bin 回读地址。");
        }

        var mainPath = ResolveSifliFile(parameterPath,
            main.GetProperty("path").GetString() ?? "");
        var readbackPath = Path.Combine(Path.GetTempPath(),
            $"controller-bridge-readback-{Guid.NewGuid():N}.bin");
        try
        {
            progress?.Report("正在回读校验 main.bin...");
            var read = CreateSifliProcess(executable, serialPort, chip, memory,
                "read_flash");
            read.ArgumentList.Add(
                $"{readbackPath}@{mainAddress.GetString()}:256");
            var readResult = await RunProcessAsync(read, cancellationToken);
            if (readResult.ExitCode != 0)
            {
                return new(false,
                    $"sftool 回读失败（{readResult.ExitCode}）：" +
                    $"{readResult.Error.Trim()} {readResult.Output.Trim()}".Trim());
            }
            var expected = await File.ReadAllBytesAsync(mainPath,
                cancellationToken);
            var actual = await File.ReadAllBytesAsync(readbackPath,
                cancellationToken);
            var compareLength = Math.Min(256, expected.Length);
            if (actual.Length < compareLength ||
                !expected.AsSpan(0, compareLength).SequenceEqual(
                    actual.AsSpan(0, compareLength)))
            {
                return new(false, "SF32 回读内容与 main.bin 不一致。");
            }
        }
        finally
        {
            if (File.Exists(readbackPath)) File.Delete(readbackPath);
        }

        progress?.Report("SF32 写入和回读校验完成。");
        return new(true, "SF32 固件烧录并校验完成。重新插拔 USB 设备口后继续。")
        ;
    }

    private static IReadOnlyList<DriveInfo> FindPicoBootDrives()
    {
        var matches = new List<DriveInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && string.Equals(drive.VolumeLabel, "RPI-RP2",
                                                    StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(drive);
                }
            }
            catch (IOException)
            {
            }
        }
        return matches;
    }

    private static void ValidateUf2(string artifact)
    {
        var data = File.ReadAllBytes(artifact);
        if (data.Length == 0 || data.Length % 512 != 0)
        {
            throw new InvalidDataException("文件长度不是完整的 512 字节 UF2 块。");
        }
        for (var offset = 0; offset < data.Length; offset += 512)
        {
            var block = data.AsSpan(offset, 512);
            if (BinaryPrimitives.ReadUInt32LittleEndian(block) != 0x0A324655 ||
                BinaryPrimitives.ReadUInt32LittleEndian(block[4..]) != 0x9E5D5157 ||
                BinaryPrimitives.ReadUInt32LittleEndian(block[508..]) != 0x0AB16F30)
            {
                throw new InvalidDataException($"第 {offset / 512 + 1} 块标记错误。");
            }
        }
        var family = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28));
        if (family is not (0xE48BFF57 or 0xE48BFF59 or 0xE48BFF5A))
        {
            throw new InvalidDataException(
                $"UF2 family 0x{family:X8} 不是 Pico 2 W / RP2350。");
        }
    }

    private static (string Chip, string Memory) ValidateSifliTarget(
        JsonElement root)
    {
        var chip = root.GetProperty("chip").GetString() ??
            throw new InvalidDataException("sftool chip is missing.");
        var memory = root.GetProperty("memory").GetString() ??
            throw new InvalidDataException("sftool memory is missing.");
        if (!chip.Equals("SF32LB52", StringComparison.OrdinalIgnoreCase) ||
            !memory.Equals("NOR", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"unsupported SF32 target: {chip}/{memory}.");
        }
        var files = root.GetProperty("write_flash").GetProperty("files");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0 ||
            files.GetArrayLength() > 16)
            throw new InvalidDataException("烧录文件列表为空或无效。");
        foreach (var file in files.EnumerateArray())
        {
            var addressText = file.GetProperty("address").GetString();
            if (addressText is null || !addressText.StartsWith("0x",
                    StringComparison.OrdinalIgnoreCase) ||
                !uint.TryParse(addressText[2..], NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var address) ||
                address is < 0x12000000 or >= 0x13000000)
            {
                throw new InvalidDataException(
                    $"unsafe SF32 flash address: {addressText}.");
            }
        }
        return (chip, memory);
    }

    internal static ProcessStartInfo CreateSifliWriteProcess(string executable,
        string parameterPath, string serialPort)
    {
        ValidateArtifactBundle(FirmwareFlashMethod.SifliSerial, parameterPath);
        using var document = JsonDocument.Parse(File.ReadAllText(parameterPath));
        var root = document.RootElement;
        var (chip, memory) = ValidateSifliTarget(root);
        var start = CreateSifliProcess(executable, serialPort, chip, memory, "write_flash");
        // sftool validates the complete written images; the later readback also checks boot data.
        start.ArgumentList.Add("--verify");
        foreach (var file in root.GetProperty("write_flash").GetProperty("files").EnumerateArray())
            start.ArgumentList.Add($"{ResolveSifliFile(parameterPath, file.GetProperty("path").GetString()!)}@{file.GetProperty("address").GetString()}");
        return start;
    }

    private static ProcessStartInfo CreateSifliProcess(string executable,
        string serialPort, string chip, string memory, string command)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-p", serialPort, "-c", chip, "-m",
                     memory.ToLowerInvariant(), command
                 })
        {
            start.ArgumentList.Add(argument);
        }
        return start;
    }

    private static async Task<(int ExitCode, string Output, string Error)>
        RunProcessAsync(ProcessStartInfo start,
                        CancellationToken cancellationToken)
    {
        using var process = Process.Start(start) ??
            throw new InvalidOperationException("无法启动 sftool。");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }
        return (process.ExitCode, await outputTask, await errorTask);
    }

    private static string ResolveSifliFile(string parameterPath,
                                           string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) ||
            relativePath.Contains(':') || relativePath.Split('/', '\\').Contains(".."))
            throw new InvalidDataException("sftool 文件路径必须位于固件包内。");
        var root = Path.GetFullPath(Path.GetDirectoryName(parameterPath)!);
        var path = Path.GetFullPath(Path.Combine(root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "sftool parameter references a file outside the firmware package.");
        }
        return path;
    }

    public static string? ResolveSifliTool(string? applicationDirectory = null)
    {
        var bundled = Path.Combine(applicationDirectory ?? AppContext.BaseDirectory,
            "tools", "sftool", "sftool.exe");
        return File.Exists(bundled) ? bundled : FindExecutable("sftool");
    }

    public static string? ResolveBouffaloTool(string? applicationDirectory = null)
    {
        var baseDirectory = applicationDirectory ?? AppContext.BaseDirectory;
        var bundled = Path.Combine(baseDirectory, "tools", "blflash",
            "BLFlashCommand.exe");
        if (File.Exists(bundled)) return bundled;
        var configured = Environment.GetEnvironmentVariable("BRIDGE_MANAGER_BLFLASH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var sdkTool = Path.Combine(documents, "controllerbridge", "bouffalo_sdk",
            "tools", "bflb_tools", "bouffalo_flash_cube", "BLFlashCommand.exe");
        return File.Exists(sdkTool) ? sdkTool : FindExecutable("BLFlashCommand");
    }

    private static string? FindExecutable(string name)
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ??
                          ".EXE;.CMD;.BAT").Split(';');
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator,
                            StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries))
        {
            foreach (var extension in extensions.Prepend(""))
            {
                var candidate = Path.Combine(directory, name + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        if (name.Equals("sftool", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    var toolsRoot = Path.Combine(drive.RootDirectory.FullName,
                        "SDKs", ".sifli-tools", "tools", "sftool");
                    if (!Directory.Exists(toolsRoot)) continue;
                    var candidate = Directory.EnumerateFiles(
                            toolsRoot, "sftool.exe", SearchOption.AllDirectories)
                        .OrderByDescending(path => path,
                            StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    if (candidate is not null) return candidate;
                }
                catch (Exception ex) when (ex is IOException or
                                           UnauthorizedAccessException)
                {
                }
            }
        }
        return null;
    }
}
