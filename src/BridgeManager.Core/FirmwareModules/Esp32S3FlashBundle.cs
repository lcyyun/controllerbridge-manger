using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace BridgeManager.Core.FirmwareModules;

internal sealed record Esp32S3FlashFile(string Kind, string Path, uint Address, long Size);

internal sealed record Esp32S3FlashBundle(string Board, int FlashSizeMb, int PsramSizeMb,
    string PsramMode, IReadOnlyList<Esp32S3FlashFile> Files)
{
    public static Esp32S3FlashBundle Read(string manifest)
    {
        try { return ReadManifest(manifest); }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or
            InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            throw new InvalidDataException("ESP32-S3 烧录清单格式无效。", ex);
        }
    }

    private static Esp32S3FlashBundle ReadManifest(string manifest)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var root = document.RootElement;
        var board = root.GetProperty("board").GetString() ?? "";
        var flash = root.GetProperty("flashSizeMb").GetInt32();
        var psram = root.GetProperty("psramSizeMb").GetInt32();
        var mode = root.GetProperty("psramMode").GetString();
        var memory = board switch
        {
            "n4" => (4, 0, "none"), "n8" => (8, 0, "none"),
            "n8r2" => (8, 2, "quad"), "n8r8" => (8, 8, "octal"),
            "n16r8" => (16, 8, "octal"), "esp32supermini" => (4, 2, "quad"),
            _ => throw new InvalidDataException("未知 ESP32-S3 板型。")
        };
        if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
            root.GetProperty("chip").GetString() != "esp32s3" ||
            (flash, psram, mode) != memory ||
            root.GetProperty("flashMode").GetString() != "dio" ||
            root.GetProperty("flashFrequencyMhz").GetInt32() != 80 ||
            root.GetProperty("baudRate").GetInt32() != 460800)
            throw new InvalidDataException("ESP32-S3 板型、Flash/PSRAM 或烧录参数不匹配。");
        var entries = root.GetProperty("files");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != 3)
            throw new InvalidDataException("ESP32-S3 必须包含 bootloader、partition 和 application 三个镜像。");
        var files = new List<Esp32S3FlashFile>();
        foreach (var item in entries.EnumerateArray())
        {
            var kind = item.GetProperty("kind").GetString() ?? "";
            var path = ResolveFile(manifest, item.GetProperty("path").GetString() ?? "");
            var text = item.GetProperty("address").GetString() ?? "";
            if (!text.StartsWith("0x", StringComparison.Ordinal) ||
                !uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address))
                throw new InvalidDataException("ESP32-S3 写入地址必须为十六进制。");
            var length = new FileInfo(path).Length;
            if (length <= 0 || length != item.GetProperty("size").GetInt64() ||
                (ulong)address + (ulong)length > (ulong)flash * 1024 * 1024 ||
                files.Any(file => address < file.Address + file.Size && address + length > file.Address))
                throw new InvalidDataException("ESP32-S3 镜像大小无效、超出 Flash 或写入范围重叠。");
            using var input = File.OpenRead(path);
            var expected = item.GetProperty("sha256").GetString();
            if (expected?.Length != 64 || !Convert.ToHexString(SHA256.HashData(input))
                    .Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{kind} SHA-256 不匹配。");
            files.Add(new(kind, path, address, length));
        }
        if (files.Count(file => file.Kind == "bootloader" && file.Address == 0 && file.Size <= 0x8000) != 1 ||
            files.Count(file => file.Kind == "partition" && file.Address == 0x8000 && file.Size <= 0x1000) != 1 ||
            files.Count(file => file.Kind == "application" && file.Address == 0x10000) != 1)
            throw new InvalidDataException("ESP32-S3 镜像种类或固定写入地址不匹配。");
        Span<byte> header = stackalloc byte[24];
        foreach (var file in files.Where(file => file.Kind != "partition"))
        {
            using var input = File.OpenRead(file.Path);
            input.ReadExactly(header);
            var sizeCode = flash switch { 4 => 2, 8 => 3, 16 => 4, _ => -1 };
            if (header[0] != 0xe9 || header[1] is 0 or > 16 || header[2] != 2 ||
                (header[3] >> 4) != sizeCode || (header[3] & 15) != 15 ||
                BinaryPrimitives.ReadUInt16LittleEndian(header[12..]) != 9)
                throw new InvalidDataException($"{file.Kind} 不是与板型匹配的 ESP32-S3 DIO/80MHz 镜像。");
        }
        ValidatePartition(files, flash);
        return new(board, flash, psram, mode!, files.OrderBy(file => file.Address).ToArray());
    }

    public void ValidateFirmware(BridgeFirmwareDefinition firmware)
    {
        if (firmware.BoardIds.Count != 1 || firmware.BoardIds[0] != "esp32s3-" + Board)
            throw new InvalidDataException("模块板型与 ESP32-S3 镜像板型不一致。");
    }

    private static void ValidatePartition(IReadOnlyList<Esp32S3FlashFile> files, int flashMb)
    {
        var data = File.ReadAllBytes(files.Single(file => file.Kind == "partition").Path);
        var app = files.Single(file => file.Kind == "application");
        var matched = 0;
        var ranges = new List<(uint Start, uint End)>();
        for (var offset = 0; offset + 32 <= data.Length; offset += 32)
        {
            var record = data.AsSpan(offset, 32);
            var magic = BinaryPrimitives.ReadUInt16LittleEndian(record);
            if (magic == 0xffff || magic == 0xebeb) break;
            if (magic != 0x50aa) throw new InvalidDataException("ESP32-S3 分区表格式无效。");
            var start = BinaryPrimitives.ReadUInt32LittleEndian(record[4..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(record[8..]);
            var end = (ulong)start + size;
            if (start < 0x9000 || size == 0 || end > (ulong)flashMb * 1024 * 1024 ||
                ranges.Any(range => start < range.End && end > range.Start))
                throw new InvalidDataException("ESP32-S3 分区超出 Flash 或重叠。");
            ranges.Add((start, (uint)end));
            if (record[2] == 0)
            {
                if (record[3] != 0 || start != app.Address || size < app.Size)
                    throw new InvalidDataException("ESP32-S3 应用与 factory 分区不一致。");
                matched++;
            }
        }
        if (matched != 1) throw new InvalidDataException("ESP32-S3 分区表缺少唯一 factory 应用分区。");
    }

    private static string ResolveFile(string manifest, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
            relative.Contains(':') || parts.Any(part => part is "" or "." or ".." ||
                part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("ESP32-S3 文件路径必须位于固件包内。");
        var root = Path.GetFullPath(Path.GetDirectoryName(manifest)!);
        var current = root;
        foreach (var part in new[] { "" }.Concat(parts))
        {
            if (part.Length > 0) current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ESP32-S3 固件路径不能包含重解析点。");
        }
        if (!File.Exists(current)) throw new InvalidDataException("ESP32-S3 固件文件缺失。");
        return current;
    }
}
