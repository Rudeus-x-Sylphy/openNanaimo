using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public static class CardCatalog
{
    private const string ClientDataResourcePrefix = "OpenNanaimo.Adapter.ClientData.";
    private const string NormalResourceName = "OpenNanaimo.Adapter.ClientData.ddakg._D4";
    private const string EventResourceName = "OpenNanaimo.Adapter.ClientData.EDdakgi._D19";
    private const string SpecialResourceName = "OpenNanaimo.Adapter.ClientData.Sddakg._D35";
    private static readonly byte[] EncryptionKey =
    [
        0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF,
        0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0
    ];
    private static readonly Lazy<IReadOnlyList<CardCatalogEntry>> Entries = new(Load);
    private static readonly Lazy<IReadOnlyDictionary<uint, CardCatalogEntry>> EntriesByCode =
        new(() => Entries.Value.ToDictionary(entry => entry.CardCode));
    private static readonly Lazy<IReadOnlyDictionary<uint, CardCatalogEntry[]>> NormalDropsByMonster =
        new(() => Entries.Value
            .Where(entry => entry.Category == 1 && entry.MonsterTargetCode != 0)
            .GroupBy(entry => entry.MonsterTargetCode)
            .ToDictionary(group => group.Key, group => group.OrderBy(entry => entry.CardCode).ToArray()));
    public static IReadOnlyList<CardCatalogEntry> All => Entries.Value;

    public static bool TryGet(uint cardCode, out CardCatalogEntry entry)
        => EntriesByCode.Value.TryGetValue(cardCode, out entry!);

    public static IReadOnlyList<CardCatalogEntry> GetNormalDropsByMonsterTargetCode(uint monsterTargetCode)
        => NormalDropsByMonster.Value.GetValueOrDefault(monsterTargetCode) ?? [];

    public static IReadOnlyList<CardCatalogEntry> GetDungeonDrops(
        byte clientEpisode,
        int monsterResourceCode)
    {
        if (clientEpisode == byte.MaxValue || monsterResourceCode <= 0)
            return [];
        var resourceEpisode = checked((byte)(clientEpisode + 1));
        return GetNormalDropsByMonsterTargetCode(checked((uint)monsterResourceCode))
            .Where(entry => entry.Episode == resourceEpisode)
            .ToArray();
    }

    public static bool TrySelectDungeonDrop(
        byte clientEpisode,
        int monsterResourceCode,
        out CardCatalogEntry entry)
    {
        var candidates = GetDungeonDrops(clientEpisode, monsterResourceCode);
        if (candidates.Count == 0)
        {
            entry = null!;
            return false;
        }

        // The client resource identifies all valid cards for this exact MMO
        // monster code but contains no server drop-rate weights. Select only
        // within that authoritative set; never derive a card from scene UID,
        // dungeon number, or stage number.
        entry = candidates[RandomNumberGenerator.GetInt32(candidates.Count)];
        return true;
    }

    public static IReadOnlyList<CardCatalogEntry> GetDungeonEpisodeDrops(byte clientEpisode)
    {
        if (clientEpisode == byte.MaxValue)
            return [];
        var resourceEpisode = checked((byte)(clientEpisode + 1));
        return Entries.Value
            .Where(entry => entry.Category == 1
                && entry.Episode == resourceEpisode
                && entry.CardCode != 0)
            .OrderBy(entry => entry.CardCode)
            .ToArray();
    }

    public static bool TrySelectDungeonBossDrop(
        byte clientEpisode,
        byte clientDungeon,
        int monsterResourceCode,
        out CardCatalogEntry entry)
    {
        if (TrySelectDungeonDrop(clientEpisode, monsterResourceCode, out entry))
            return true;

        var episodeDrops = GetDungeonEpisodeDrops(clientEpisode);
        if (episodeDrops.Count == 0)
        {
            entry = null!;
            return false;
        }

        // Later ddakg rows explicitly name entries such as "5-1 Boss" in
        // SourceMonsters. Earlier episodes only name the BOSS itself, so use
        // the client-verified episode pool when no exact BOSS marker exists.
        var bossMarker = $"{clientEpisode + 1}-{clientDungeon + 1} Boss";
        var bossDrops = episodeDrops
            .Where(card => card.SourceMonsters.Any(source =>
                string.Equals(source, bossMarker, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var candidates = bossDrops.Length == 0 ? episodeDrops : bossDrops;
        entry = candidates[RandomNumberGenerator.GetInt32(candidates.Count)];
        return true;
    }

    internal static bool TryRollDungeonDrop(
        byte clientEpisode,
        int monsterResourceCode,
        int cardBonusPercent,
        out CardCatalogEntry entry,
        int? rollBasisPoints = null)
    {
        var roll = rollBasisPoints ?? RandomNumberGenerator.GetInt32(10_000);
        if (!DungeonDropPolicy.PassesNormalCardRoll(cardBonusPercent, roll))
        {
            entry = null!;
            return false;
        }

        return TrySelectDungeonDrop(clientEpisode, monsterResourceCode, out entry);
    }

    private static IReadOnlyList<CardCatalogEntry> Load()
    {
        var result = new List<CardCatalogEntry>(310);
        // ddakg._D4 field 5 is the exact item produced by the client's
        // one-card DDAKGI_UNION branch (sub_9BE850 reads record+0xC4).
        LoadFixedRecords(result, NormalResourceName, "PICTURECARD", 3, 17, 200, 1, 0, 1, 20, 12, 5, 6,
            dropRegionField: 13, episodeField: 14, dropTypeField: 8, sourceMonstersField: 15, mapNameField: 16,
            monsterImageField: 11);
        LoadFixedRecords(result, EventResourceName, "EVENTDDAKGI", 2, 12, 100, 2, 0, 1, 20, 8, null, null, @"images\ddakg_images\");
        // Sddakg declares ten fixed-layout VIP cards in its header. Later
        // reward-list records are variable length and are not card-book slots.
        // Sddakg._D35 fields 4 and 5 are the native special-card shop's
        // purchasable flag and Cash price. Field 4 disables the tenth row.
        LoadFixedRecords(result, SpecialResourceName, "SPECIALDDAKGI", 4, 18, 10, 3, 0, 2, 10, 13, null, null,
            specialShopPurchasableField: 4, specialShopPriceField: 5);
        return result;
    }

    private static void LoadFixedRecords(
        List<CardCatalogEntry> result,
        string resourceName,
        string header,
        int headerFields,
        int recordFields,
        int recordCount,
        byte category,
        int codeField,
        int nameField,
        int pageSize,
        int iconPathField,
        int? synthesisItemCodeField,
        int? sellHansPriceField,
        string iconPathPrefix = "",
        int? dropRegionField = null,
        int? episodeField = null,
        int? dropTypeField = null,
        int? sourceMonstersField = null,
        int? mapNameField = null,
        int? monsterImageField = null,
        int? specialShopPurchasableField = null,
        int? specialShopPriceField = null)
    {
        var fields = DecryptFields(resourceName);
        if (fields.Length < headerFields + recordFields * recordCount || fields[0] != header)
            throw new InvalidDataException($"The embedded {header} card catalog is invalid.");

        for (var index = 0; index < recordCount; index++)
        {
            var offset = headerFields + index * recordFields;
            if (!uint.TryParse(fields[offset + codeField], NumberStyles.None, CultureInfo.InvariantCulture, out var cardCode))
                throw new InvalidDataException($"The embedded {header} card at index {index} has an invalid code.");
            uint synthesisItemCode = 0;
            if (synthesisItemCodeField is int synthesisField
                && !uint.TryParse(fields[offset + synthesisField], NumberStyles.None, CultureInfo.InvariantCulture, out synthesisItemCode))
            {
                // The native loader uses atol here. Non-numeric rows become
                // zero and the card-book UI disables one-card synthesis.
                synthesisItemCode = 0;
            }
            uint sellHansPrice = 0;
            if (sellHansPriceField is int sellField
                && !uint.TryParse(fields[offset + sellField], NumberStyles.None, CultureInfo.InvariantCulture, out sellHansPrice))
                throw new InvalidDataException($"The embedded {header} card at index {index} has an invalid sell price.");
            var specialShopPurchasable = specialShopPurchasableField is int purchasableField
                && fields[offset + purchasableField] == "1";
            uint specialShopPrice = 0;
            if (specialShopPriceField is int priceField
                && !uint.TryParse(fields[offset + priceField], NumberStyles.None, CultureInfo.InvariantCulture, out specialShopPrice))
                throw new InvalidDataException($"The embedded {header} card at index {index} has an invalid shop price.");
            result.Add(new CardCatalogEntry
            {
                CardCode = cardCode,
                SynthesisItemCode = synthesisItemCode,
                SellHansPrice = sellHansPrice,
                SpecialShopPrice = specialShopPrice,
                IsSpecialShopPurchasable = specialShopPurchasable,
                Name = fields[offset + nameField],
                Category = category,
                Page = checked((byte)(index / pageSize + 1)),
                Slot = checked((byte)(index % pageSize)),
                IconPath = iconPathPrefix + fields[offset + iconPathField],
                DropRegion = ParseByteField(fields, offset, dropRegionField),
                Episode = ParseByteField(fields, offset, episodeField),
                DropType = ParseByteField(fields, offset, dropTypeField),
                MonsterTargetCode = monsterImageField is int targetField
                    ? ParseMonsterTargetCode(fields[offset + targetField])
                    : 0,
                SourceMonsters = sourceMonstersField is int monstersField
                    ? fields[offset + monstersField].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : [],
                MapName = mapNameField is int mapField ? fields[offset + mapField] : string.Empty
            });
        }
    }

    private static byte ParseByteField(string[] fields, int offset, int? field)
        => field is int index
           && byte.TryParse(fields[offset + index], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : (byte)0;

    private static uint ParseMonsterTargetCode(string imagePath)
    {
        const string marker = "_monster_";
        var markerIndex = imagePath.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return 0;
        var valueStart = markerIndex + marker.Length;
        var valueEnd = imagePath.IndexOf('.', valueStart);
        if (valueEnd < 0)
            valueEnd = imagePath.Length;
        return uint.TryParse(
            imagePath.AsSpan(valueStart, valueEnd - valueStart),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : 0;
    }

    internal static string[] DecryptFields(string resourceName)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (!resourceName.StartsWith(ClientDataResourcePrefix, StringComparison.Ordinal))
            throw new InvalidDataException($"Invalid client catalog name: {resourceName}");
        var fileName = resourceName[ClientDataResourcePrefix.Length..];
        var catalogPath = Path.Combine(AppContext.BaseDirectory, "资源", "数据", fileName);
        if (!File.Exists(catalogPath))
            throw new FileNotFoundException($"Missing client catalog: {catalogPath}", catalogPath);
        using var aes = Aes.Create();
        aes.Key = EncryptionKey;
        aes.IV = new byte[16];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        var cipherText = File.ReadAllBytes(catalogPath);
        var plainText = decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);
        return Encoding.GetEncoding(936).GetString(plainText).Split('#');
    }
}
