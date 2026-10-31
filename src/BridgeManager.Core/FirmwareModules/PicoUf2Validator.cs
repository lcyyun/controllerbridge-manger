using System.Buffers.Binary;

namespace BridgeManager.Core.FirmwareModules;

/// <summary>
/// Structural validation for Pico 2 W flash UF2s, without opening a device.
/// This does not authenticate firmware or predict BootROM partition permissions.
/// </summary>
// Format: github.com/raspberrypi/pico-sdk/blob/master/src/common/boot_uf2_headers/include/boot/uf2.h
// Write semantics: github.com/raspberrypi/pico-bootrom-rp2350/blob/master/src/nsboot/usb_virtual_disk.c
// E10 prefix: github.com/raspberrypi/picotool/blob/master/elf2uf2/elf2uf2.cpp (gen_abs_block)
public static class PicoUf2Validator
{
    private const uint NotMainFlash = 0x00000001;
    private const uint FileContainer = 0x00001000;
    private const uint FamilyPresent = 0x00002000;
    private const uint ExtensionPresent = 0x00008000;
    private const uint KnownFlags = 0x0000f001;
    private const uint IgnoreBlock = 0x9957e304;
    private const uint FlashStart = 0x10000000;
    private const uint FlashEnd = FlashStart + 4 * 1024 * 1024;

    public static void Validate(string artifact) => Validate(File.ReadAllBytes(artifact).AsSpan());

    public static void Validate(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || data.Length % 512 != 0)
            throw new InvalidDataException("UF2 文件长度不是完整的 512 字节块。");

        var streams = new Dictionary<uint, FamilyStream>();
        uint? currentFamily = null;
        for (var offset = 0; offset < data.Length; offset += 512)
        {
            var block = data.Slice(offset, 512);
            var index = offset / 512 + 1;
            if (Read(block, 0) != 0x0a324655 || Read(block, 4) != 0x9e5d5157 ||
                Read(block, 508) != 0x0ab16f30)
                throw Invalid(index, "标记错误");

            var flags = Read(block, 8);
            var payload = Read(block, 16);
            if ((flags & ~KnownFlags) != 0 || (flags & FileContainer) != 0)
                throw Invalid(index, "不支持的 UF2 标志或文件容器");
            if (payload > 476 || ((flags & 0x4000) != 0 && payload > 452))
                throw Invalid(index, "数据长度超出 UF2 块");

            // The UF2 specification permits arbitrary comment/source blocks.
            // BootROM ignores their address, family and numbering entirely.
            if ((flags & NotMainFlash) != 0) continue;

            var family = Read(block, 28);
            if ((flags & FamilyPresent) == 0 || family is < 0xe48bff57 or > 0xe48bff5b)
                throw Invalid(index, "缺少 RP2350 family 标记或 family 不兼容");
            // RP2350 BootROM only writes full 256-byte pages (not generic UF2's 476).
            if (payload != 256) throw Invalid(index, "RP2350 数据页必须为 256 字节");

            // picotool's RP2350-E10 prefix is ABSOLUTE/number=0/count=2 at
            // 0x10ffff00. It is intentionally not a complete downloadable stream.
            // BootROM recognizes this tag only as the FIRST tag at data + 256.
            if ((flags & ExtensionPresent) != 0 && Read(block, 32 + 256) == IgnoreBlock)
                continue;

            var address = Read(block, 12);
            if ((address & 255) != 0 || address < FlashStart ||
                (ulong)address + payload > FlashEnd)
                throw Invalid(index, "目标地址不在 Pico 2 W 的 4 MB Flash 内或未按页对齐");

            var number = Read(block, 20);
            var count = Read(block, 24);
            if (count == 0 || count > (FlashEnd - FlashStart) / 256 ||
                count > data.Length / 512 || number >= count)
                throw Invalid(index, "块编号或总块数无效");

            if (currentFamily != family)
            {
                // BootROM resets its transfer state on a family change. Complete
                // concatenated streams are supported; interleaved streams are not.
                if (currentFamily is { } previous) streams[previous].RequireComplete();
                if (streams.ContainsKey(family)) throw Invalid(index, "family 数据流交错或重复");
                streams.Add(family, new FamilyStream(family, count));
                currentFamily = family;
            }
            var stream = streams[family];
            if (stream.Count != count || !stream.Numbers.Add(number))
                throw Invalid(index, "同一 family 的块总数不一致或块编号重复");
            // Pages are aligned and exactly 256 bytes, so duplicate addresses
            // detect all overlap within this logical stream. Different families
            // may be relocated to distinct BootROM partitions; do not conflate them.
            if (!stream.Addresses.Add(address)) throw Invalid(index, "写入地址重复");
        }
        if (streams.Count == 0) throw new InvalidDataException("UF2 没有可写入的 RP2350 Flash 数据页。");
        foreach (var stream in streams.Values) stream.RequireComplete();
    }

    private static uint Read(ReadOnlySpan<byte> block, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(block[offset..]);

    private static InvalidDataException Invalid(int block, string reason) =>
        new($"UF2 第 {block} 块：{reason}。");

    private sealed class FamilyStream(uint family, uint count)
    {
        public uint Count { get; } = count;
        public HashSet<uint> Numbers { get; } = [];
        public HashSet<uint> Addresses { get; } = [];

        public void RequireComplete()
        {
            // Every number is already unique and < Count, so cardinality proves
            // that all indices 0..Count-1 are present without allocating by Count.
            if (Numbers.Count != Count)
                throw new InvalidDataException($"UF2 family 0x{family:X8} 缺块：{Numbers.Count}/{Count}。");
        }
    }
}
