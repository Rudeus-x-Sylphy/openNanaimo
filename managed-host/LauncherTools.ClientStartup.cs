using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json.Nodes;

internal static partial class LauncherTools
{
    const int NativeStartupRva = 0x750C03;
    const int OriginalStartupRva = 0x9BC17B;

    static JsonObject StartupRecipe()
    {
        using var stream = typeof(LauncherTools).Assembly.GetManifestResourceStream("Nanaimo.ClientStartup.gz")
            ?? throw new InvalidOperationException("Client startup resource missing");
        using var decoded = new GZipStream(stream, CompressionMode.Decompress);
        return JsonNode.Parse(decoded)!.AsObject();
    }

    static int StartupEntry(byte[] image)
    {
        VaOffset(image, 0xB50C03, 10);
        int optional = checked(U32(image, 60) + 24);
        Require(U32(image, optional + 28) == 0x400000, "Unsupported client image base");
        return U32(image, optional + 16);
    }

    static void StartupRange(byte[] bytes, int offset, int length)
        => Require(offset >= 0 && length >= 0 && offset <= bytes.Length - length,
                   "Client startup range exceeds file");

    static uint StartupCrc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1)));
        }
        return ~crc;
    }

    static byte[] ExpandStartupBlock(byte[] block)
    {
        Require(block.Length >= 24 && block.AsSpan(0, 4).SequenceEqual("AP32"u8), "Invalid AP32 header");
        int header = U32(block, 4), packed = U32(block, 8), size = U32(block, 16);
        Require(header >= 24 && size is > 0 and <= 0x2000000, "Invalid AP32 size");
        StartupRange(block, header, packed);
        Require(StartupCrc(block.AsSpan(header, packed)) == BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(12)),
                "AP32 packed checksum differs");
        var output = new byte[size];
        int cursor = header, end = checked(header + packed), written = 0, bits = 0, tag = 0;
        int Byte() { Require(cursor < end, "Truncated AP32 stream"); return block[cursor++]; }
        int Bit()
        {
            if (bits == 0) { tag = Byte(); bits = 8; }
            int value = (tag >> 7) & 1; tag <<= 1; bits--; return value;
        }
        int Gamma()
        {
            int value = 1;
            do { Require(value < 0x1000000, "AP32 gamma overflow"); value = (value << 1) | Bit(); }
            while (Bit() != 0);
            return value;
        }
        void Literal(int value) { Require(written < size, "AP32 output overflow"); output[written++] = (byte)value; }
        void Copy(int offset, int length)
        {
            Require(offset > 0 && offset <= written && length <= size - written, "Invalid AP32 match");
            for (int i = 0; i < length; i++) { output[written] = output[written - offset]; written++; }
        }
        int previous = -1, matched = 0;
        Literal(Byte());
        while (true)
        {
            if (Bit() == 0) { Literal(Byte()); matched = 0; continue; }
            if (Bit() == 0)
            {
                int offset = Gamma(), length;
                if (matched == 0 && offset == 2) { offset = previous; length = Gamma(); }
                else
                {
                    offset = checked(((offset - (matched == 0 ? 3 : 2)) << 8) + Byte());
                    length = Gamma() + (offset >= 32000 ? 1 : 0) + (offset >= 1280 ? 1 : 0) + (offset < 128 ? 2 : 0);
                    previous = offset;
                }
                Copy(offset, length); matched = 1;
            }
            else if (Bit() == 0)
            {
                int value = Byte(), offset = value >> 1;
                if (offset == 0) break;
                Copy(offset, 2 + (value & 1)); previous = offset; matched = 1;
            }
            else
            {
                int offset = 0;
                for (int bit = 0; bit < 4; bit++) offset = (offset << 1) | Bit();
                if (offset == 0) Literal(0); else Copy(offset, 1);
                matched = 0;
            }
        }
        Require(written == size && StartupCrc(output) == BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(20)),
                "AP32 expanded checksum differs");
        return output;
    }

    static byte[] PrepareClientStartup(byte[] original, JsonArray operations)
    {
        int entry = StartupEntry(original);
        if (entry == NativeStartupRva)
        {
            Require(original.AsSpan(VaOffset(original, 0xB50C03, 10), 10)
                    .SequenceEqual(Convert.FromHexString("E88B760100E916FEFFFF")), "Native startup instructions differ");
            int optional = U32(original, 60) + 24;
            int imports = U32(original, optional + 104);
            Require(imports != 0 && imports != 0x9BC00D, "Native client import table is incomplete");
            VaOffset(original, 0x400000L + imports, 20);
            operations.Add(Obj(new { operation = "legacy_startup_layout", changed = false, input_identity_checked = false }));
            return original.ToArray();
        }
        Require(entry == OriginalStartupRva, "Unsupported client startup layout");
        var recipe = StartupRecipe();
        Require(N(recipe, "schema") == 1 && original.Length == N(recipe, "input_size") &&
                Hash(original).Equals(S(recipe, "input_sha256"), StringComparison.OrdinalIgnoreCase),
                "Original client identity differs; choose the supported original client");
        int size = checked((int)N(recipe, "output_size"));
        Require(size is > 0 and <= 0x2000000, "Invalid client startup output size");
        var result = new byte[size];
        foreach (var block in Objects(recipe, "blocks"))
        {
            int start = checked((int)N(block, "source")), length = checked((int)N(block, "length"));
            int output = checked((int)N(block, "output")), take = checked((int)N(block, "take"));
            StartupRange(original, start, length); StartupRange(result, output, take);
            byte[] bytes = original.AsSpan(start, length).ToArray();
            if (S(block, "codec") == "ap32") bytes = ExpandStartupBlock(bytes);
            else Require(S(block, "codec") == "copy", "Unknown client startup codec");
            Require(take <= bytes.Length, "Client startup block is truncated");
            bytes.AsSpan(0, take).CopyTo(result.AsSpan(output));
        }
        uint delta = unchecked((uint)N(recipe, "relocation_delta"));
        foreach (var node in A(recipe, "relocations"))
        {
            int offset = node!.GetValue<int>(); StartupRange(result, offset, 4);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(offset));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), unchecked(value + delta));
        }
        int last = 0;
        foreach (var write in Objects(recipe, "writes"))
        {
            int offset = checked((int)N(write, "offset"));
            var bytes = Convert.FromBase64String(S(write, "data")); StartupRange(result, offset, bytes.Length);
            Require(offset >= last && bytes.Length > 0, "Overlapping client startup writes");
            bytes.CopyTo(result, offset); last = checked(offset + bytes.Length);
        }
        Require(Hash(result).Equals(S(recipe, "output_sha256"), StringComparison.OrdinalIgnoreCase) &&
                StartupEntry(result) == NativeStartupRva, "Client startup output identity differs");
        operations.Add(Obj(new { operation = "legacy_startup_layout", changed = true, input_identity_checked = true,
            input_sha256 = Hash(original), output_sha256 = Hash(result) }));
        return result;
    }
}
