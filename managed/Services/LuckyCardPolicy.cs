using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenNanaimo.Adapter.Services;

internal static class LuckyCardPolicy
{
    // Bundle: explicit multi-code grant (a whole outfit/decor set); Code is the
    // bundle's first member so C3FE's non-zero success value stays meaningful.
    // Hans: > 0 grants that much gold instead of an item.
    internal readonly record struct Reward(uint Code, int UpperBound, int Hans = 0, IReadOnlyList<uint>? Bundle = null);
    internal sealed record Pool(uint Card, string Version, IReadOnlyList<Reward> Rewards);
    private static readonly Lazy<IReadOnlyDictionary<uint, Pool>> Pools = new(Load);
    internal static IReadOnlyDictionary<uint, Pool> All => Pools.Value;
    internal static bool TryGet(uint card, out Pool pool) => Pools.Value.TryGetValue(card, out pool!);
    internal static bool IsUnopened(uint card) => card is >= 22000011u and <= 22000020u && !TryGet(card, out _);

    internal static uint PickOpenCard(int ticket)
    {
        if (ticket is < 0 or >= 10000) throw new ArgumentOutOfRangeException(nameof(ticket));
        var cards = All.Keys.OrderBy(card => card).ToArray();
        if (cards.Length == 0) throw new InvalidDataException("No opened lucky-card pools are configured.");
        return cards[ticket * cards.Length / 10000];
    }

    // 85BA10 -> 851010 -> 8512C0 -> 7BEC40: WORD+8=40, WORD+10=40,
    // DWORD+12=0, DWORD+16=card. Frame+20/+24 are NOT initialized by 8512C0.
    // Ignore only those tail bytes; never reinterpret the key selector as a reward.
    internal static bool TryParse(ReadOnlySpan<byte> payload, out uint card)
    {
        card = 0;
        if (payload.Length != 20 || BinaryPrimitives.ReadUInt16LittleEndian(payload) != 40
            || BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]) != 40
            || BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]) != 0) return false;
        card = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);
        return TryGet(card, out _);
    }

    internal static uint Pick(Pool pool, int ticket)
    {
        if (ticket is < 0 or >= 10000) throw new ArgumentOutOfRangeException(nameof(ticket));
        foreach (var reward in pool.Rewards)
            if (ticket < reward.UpperBound) return reward.Code;
        throw new InvalidDataException("Incomplete lucky-card distribution.");
    }

    // 8599D0 result900 consumes the output code at frame+12 and completes the
    // lottery; 800 is exclusively the timed EXP entitlement. 100 is capacity,
    // not an unsupported-operation code. Zero clears the busy state without lying.
    internal static byte[] Result(uint result, uint reward = 0)
    {
        var p = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(p, result);
        if (result == 900) BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(4), reward);
        return p;
    }

    private static IReadOnlyDictionary<uint, Pool> Load()
    {
        var f = CardCatalog.DecryptFields("OpenNanaimo.Adapter.ClientData.Sddakg._D35");
        if (f.Length < 545 || f[0] != "SPECIALDDAKGI" || f[184] != "10")
            throw new InvalidDataException("Invalid lucky-card resource blocks.");
        var result = new Dictionary<uint, Pool>();
        for (var row = 0; row < 10; row++)
        {
            var start = 185 + row * 36;
            var card = uint.Parse(f[start], CultureInfo.InvariantCulture);
            if (card != 22000011u + row || f[start + 15] != "2")
                throw new InvalidDataException("Invalid lucky-card identity.");
            var rewards = new List<Reward>(); var previous = 0; var ended = false;
            for (var slot = 0; slot < 10; slot++)
            {
                var code = uint.Parse(f[start + 16 + slot * 2], CultureInfo.InvariantCulture);
                var upper = int.Parse(f[start + 17 + slot * 2], CultureInfo.InvariantCulture);
                if (code == 0 && upper == 0) { ended = true; continue; }
                if (ended || code == 0 || upper <= previous || upper > 10000)
                    throw new InvalidDataException("Invalid lucky-card cumulative range.");
                if (!CardCatalog.TryGet(code, out _) && !ShopCatalog.TryGet(code, out _))
                    throw new InvalidDataException($"Uncatalogued lucky-card reward {code}.");
                rewards.Add(new(code, upper)); previous = upper;
            }
            if (row >= 8 && rewards.Count == 0) continue; // I/J explicitly unimplemented.
            if (previous != 10000) throw new InvalidDataException("Incomplete lucky-card range.");
            // Local adapter policy: interpret the authored ascending cutoffs as
            // cumulative ranges. This is not a recovered original-server RNG.
            var version = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join("#", f.Skip(start).Take(36)))));
            result.Add(card, new(card, version, rewards));
        }
        return result;
    }
}

internal sealed class LuckyCardRequestWindow
{
    private readonly Dictionary<(ushort, uint), (DateTime At, string Id)> _requests = [];
    internal string Get(ushort control, uint card, DateTime now)
    {
        // Native sequence numbers wrap. Bound retransmission correlation rather
        // than permanently suppressing a legitimate later draw with the same byte.
        foreach (var key in _requests.Where(x => now - x.Value.At >= TimeSpan.FromSeconds(10)).Select(x => x.Key).ToArray())
            _requests.Remove(key);
        var k = (control, card);
        if (_requests.TryGetValue(k, out var old)) return old.Id;
        if (_requests.Count >= 256) _requests.Remove(_requests.MinBy(x => x.Value.At).Key);
        var id = Guid.NewGuid().ToString("N"); _requests.Add(k, (now, id)); return id;
    }
}
