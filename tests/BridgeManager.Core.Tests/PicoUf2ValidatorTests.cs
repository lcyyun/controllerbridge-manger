using System.Buffers.Binary;
using BridgeManager.Core.FirmwareModules;

internal static class PicoUf2ValidatorTests
{
    public static void Run()
    {
        var first = Block(0xe48bff59, 0, 2, 0x10000000);
        var second = Block(0xe48bff59, 1, 2, 0x10000100);
        var prefix = Block(0xe48bff57, 0, 2, 0x10ffff00);
        Write(prefix, 8, 0xa000);
        Write(prefix, 288, 0x9957e304);
        prefix.AsSpan(32, 256).Fill(0xef);

        // Reproduces the real 0.1 release's E10 prefix + ARM-S stream topology.
        Accept(Join(prefix, first, second));
        Accept(Join(prefix, second, first)); // Transfer order is not block numbering.
        Accept(Join(first, prefix, second)); // Ignored blocks cannot reset the stream.
        foreach (var family in new uint[] { 0xe48bff57, 0xe48bff58, 0xe48bff59, 0xe48bff5a, 0xe48bff5b })
            Accept(Block(family, 0, 1, 0x103fff00));
        Accept(Join(Block(0xe48bff57, 0, 1, 0x10000000), first, second));
        var optionalMetadata = first.ToArray();
        Write(optionalMetadata, 8, 0xe000); // MD5 + extension flags do not change the write page.
        Accept(Join(optionalMetadata, second));

        var comment = Block(0, 0, 0, uint.MaxValue);
        Write(comment, 8, 1);
        Write(comment, 16, 476);
        Accept(Join(comment, first, second));
        Reject(comment, "metadata alone cannot be reported as flashed firmware");
        Reject(prefix, "ignored prefix alone cannot be reported as flashed firmware");

        var valid = Join(prefix, first, second);
        foreach (var (offset, value) in new (int, uint)[]
        {
            (0, 0), (4, 0), (508, 0),
            (8, 0), (8, 0x2001), (8, 0x3000), (8, 0x22000),
            (16, 0), (16, 252), (16, 260), (16, 476), (16, 477),
            (20, 2), (20, uint.MaxValue), (24, 0), (24, 1), (24, 3), (24, uint.MaxValue),
            (28, 0xe48bff55), (28, 0xe48bff56), (28, 0xdeadbeef),
            (12, 0x0fffff00), (12, 0x10000001), (12, 0x10400000),
            (12, 0xffffff00), (12, 0x20000000)
        })
        {
            var changed = valid.ToArray();
            Write(changed, 512 + offset, value);
            Reject(changed, $"changed active-block field {offset} to 0x{value:x8}");
        }
        Reject([], "empty file");
        Reject(valid[..^1], "partial block");
        Reject(Join(prefix, first), "missing complete 512-byte block");
        Reject(Join(prefix, first, first), "duplicate number replacing a required block");
        Reject(Join(prefix, first, second, second), "appended duplicate block");
        var duplicateAddress = second.ToArray();
        Write(duplicateAddress, 12, 0x10000000);
        Reject(Join(prefix, first, duplicateAddress), "different numbers writing one page");

        var ignoredSecond = second.ToArray();
        Write(ignoredSecond, 8, 0xa000);
        Write(ignoredSecond, 288, 0x9957e304);
        Reject(Join(first, ignoredSecond), "ignored block cannot satisfy an active stream's count");
        var untaggedPrefix = prefix.ToArray();
        Write(untaggedPrefix, 8, 0x2000);
        Reject(Join(untaggedPrefix, first, second), "ignore tag requires extension-present flag");
        var lateTag = prefix.ToArray();
        Write(lateTag, 288, 0);
        Write(lateTag, 292, 0x9957e304);
        Reject(Join(lateTag, first, second), "BootROM does not search later tags for IGNORE_BLOCK");
        Reject(Join(first, Block(0xe48bff5a, 0, 1, 0x10001000), second),
            "interleaving downloadable families resets the BootROM's incomplete transfer");
        Reject(Join(Block(0xe48bff57, 0, 1, 0x10001000), first, second,
            Block(0xe48bff57, 0, 1, 0x10002000)), "repeated family streams are ambiguous");

        // Optional local regression against the immutable old release; synthetic
        // tests above remain self-contained on clean CI clones without dist/.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var release = Path.Combine(directory.FullName, "dist", "BridgeManager-0.3.6-preview.1-win-x64",
                "modules", "pico-unified-bridge", "artifacts", "pico-controller-bridge-0.1.uf2");
            if (File.Exists(release)) { PicoUf2Validator.Validate(release); break; }
            directory = directory.Parent;
        }
    }

    private static byte[] Block(uint family, uint number, uint count, uint address)
    {
        var block = new byte[512];
        Write(block, 0, 0x0a324655);
        Write(block, 4, 0x9e5d5157);
        Write(block, 8, 0x2000);
        Write(block, 12, address);
        Write(block, 16, 256);
        Write(block, 20, number);
        Write(block, 24, count);
        Write(block, 28, family);
        Write(block, 508, 0x0ab16f30);
        return block;
    }

    private static byte[] Join(params byte[][] blocks) => blocks.SelectMany(block => block).ToArray();
    private static void Write(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    private static void Accept(byte[] data) => PicoUf2Validator.Validate(data.AsSpan());
    private static void Reject(byte[] data, string reason)
    {
        try { PicoUf2Validator.Validate(data.AsSpan()); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid Pico UF2 accepted: " + reason);
    }
}
