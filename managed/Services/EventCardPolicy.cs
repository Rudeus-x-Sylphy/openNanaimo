using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenNanaimo.Adapter.Services;

// Explicit private-server configuration, not a reconstructed original-server reward table.
// No default prizes. A missing/invalid file leaves redemption disabled, without debiting cards.
internal static class EventCardPolicy
{
    internal const string FileName = "event-card-rewards.json";
    internal sealed record Pool(uint Page, string Version, IReadOnlyList<LuckyCardPolicy.Reward> Rewards);
    internal static bool TryParse(ReadOnlySpan<byte> payload, out uint page)
    {
        page = payload.Length == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload) : 0;
        return payload.Length == 4 && page is >= 1 and <= 10;
    }

    internal static IReadOnlyDictionary<uint, Pool> Load(string path)
    {
        if (!File.Exists(path)) return new Dictionary<uint, Pool>();
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 65536) throw new InvalidDataException("Event reward configuration exceeds 64 KiB.");
        using var doc = JsonDocument.Parse(bytes);
        Fields(doc.RootElement, "Version", "Pages");
        if (doc.RootElement.GetProperty("Version").GetInt32() != 1) throw new InvalidDataException("Unknown event reward configuration version.");
        var pools = new Dictionary<uint, Pool>();
        foreach (var row in doc.RootElement.GetProperty("Pages").EnumerateArray())
        {
            Fields(row, "Page", "Rewards");
            var page = row.GetProperty("Page").GetUInt32();
            if (page is < 1 or > 10 || pools.ContainsKey(page)) throw new InvalidDataException("Invalid/duplicate event page.");
            var rewards = new List<LuckyCardPolicy.Reward>(); var seen = new HashSet<uint>(); int upper = 0;
            foreach (var entry in row.GetProperty("Rewards").EnumerateArray())
            {
                Fields(entry, "Code", "Quantity", "Weight");
                uint code = entry.GetProperty("Code").GetUInt32();
                int quantity = entry.GetProperty("Quantity").GetInt32(), weight = entry.GetProperty("Weight").GetInt32();
                // Equipment, PET, game items and furniture only. Other C3FE success branches
                // (notably domain13 and result200) are not silently generalized to card grants.
                if (quantity != 1 || weight is < 1 or > 10000 || !seen.Add(code)
                    || !ShopCatalog.TryGet(code, out _) || CardCatalog.TryGet(code, out _))
                    throw new InvalidDataException("Unsupported event reward (catalog item, quantity one and positive weight required).");
                upper = checked(upper + weight); rewards.Add(new(code, upper));
            }
            if (upper != 10000) throw new InvalidDataException("Event weights must total 10000.");
            // Per-page identity: unrelated page edits cannot invalidate a retained draw.
            var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                page + ":" + string.Join(";", rewards.Select(x => $"{x.Code}:{x.UpperBound}")))));
            pools.Add(page, new(page, version, rewards));
        }
        return pools;
    }

    private static void Fields(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected configuration object.");
        var actual = value.EnumerateObject().Select(x => x.Name).ToArray();
        if (actual.Length != expected.Length || !actual.OrderBy(x => x).SequenceEqual(expected.OrderBy(x => x)))
            throw new InvalidDataException("Missing, duplicate or unknown configuration field.");
    }

    internal static uint Pick(Pool pool, int ticket) => LuckyCardPolicy.Pick(new(0, pool.Version, pool.Rewards), ticket);

    // 863790 case50174 copies frame+12 through a formatting function into a 24-byte
    // local buffer. Use bounded printable ASCII with no format tokens, explicitly NUL
    // terminated. Full length36 is OUR safe construction, not an original-wire length.
    internal static byte[] Result(uint reward, string message)
    {
        if (message.Length > 23 || message.Any(c => c < ' ' || c > '~' || c == '%'))
            throw new ArgumentException("Unsafe C3FE text.", nameof(message));
        var payload = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, reward);
        Encoding.ASCII.GetBytes(message).CopyTo(payload, 4);
        return payload;
    }
}
