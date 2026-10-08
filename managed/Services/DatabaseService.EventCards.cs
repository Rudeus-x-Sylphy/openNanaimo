using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenNanaimo.Adapter.Services;

internal readonly record struct EventCardRedeemResult(uint Reward, string Message, string Error)
{
    internal bool Success => Reward != 0;
}

public sealed partial class DatabaseService
{
    private static async Task InitializeEventCardUsesAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS EventCardPendingDraws (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                Page INTEGER NOT NULL CHECK(Page BETWEEN 1 AND 10),
                RewardCode INTEGER NOT NULL, Ticket INTEGER NOT NULL CHECK(Ticket BETWEEN 0 AND 9999),
                PoolVersion TEXT NOT NULL, Codes TEXT NOT NULL DEFAULT '', Hans INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL, PRIMARY KEY(CharacterId,Page)
            );
            CREATE TABLE IF NOT EXISTS EventCardUseReceipts (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                SessionId TEXT NOT NULL, RequestId TEXT NOT NULL, Page INTEGER NOT NULL,
                RewardCode INTEGER NOT NULL, Ticket INTEGER NOT NULL, PoolVersion TEXT NOT NULL,
                Codes TEXT NOT NULL DEFAULT '', Hans INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL, PRIMARY KEY(CharacterId,SessionId,RequestId)
            );
            """;
        await command.ExecuteNonQueryAsync(token);
        // Resolved grant list and direct-currency amount must survive as drawn: the pool
        // version hash covers only Code:UpperBound, so re-reading a changed pool could
        // otherwise rewrite a retained pending draw.
        await EnsureColumnAsync(connection, "EventCardPendingDraws", "Codes", "TEXT NOT NULL DEFAULT ''", token);
        await EnsureColumnAsync(connection, "EventCardPendingDraws", "Hans", "INTEGER NOT NULL DEFAULT 0", token);
        await EnsureColumnAsync(connection, "EventCardUseReceipts", "Codes", "TEXT NOT NULL DEFAULT ''", token);
        await EnsureColumnAsync(connection, "EventCardUseReceipts", "Hans", "INTEGER NOT NULL DEFAULT 0", token);
    }

    internal async Task<EventCardRedeemResult> RedeemEventCardAsync(
        long accountId, long characterId, string sessionId, string requestId, uint page,
        CancellationToken token = default, Func<int, int>? nextTicket = null)
    {
        if (page is < 1 or > 10 || accountId <= 0 || characterId <= 0 || string.IsNullOrEmpty(sessionId)
            || !Guid.TryParseExact(requestId, "N", out _)) return new(0, EventCardPolicy.Fit("请求无效"), "invalid tuple");
        // Replay authorization precedes configuration lookup so a committed retry still
        // gets its original answer if the operator later disables or changes the pool.
        await using var connection = await OpenConnectionAsync(token);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", characterId); command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$session", sessionId); command.Parameters.AddWithValue("$request", requestId);
        command.Parameters.AddWithValue("$page", page); command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.CommandText = """
            SELECT c.PetVariant, c.Gender FROM Characters c JOIN Accounts a ON a.Id=c.AccountId
            WHERE c.Id=$id AND c.AccountId=$account AND c.IsOnline=1 AND a.IsOnline=1
              AND c.ActiveSessionId=$session AND a.ActiveSessionId=$session
            """;
        long petVariant; int characterGender;
        await using (var session = await command.ExecuteReaderAsync(token))
        {
            if (!await session.ReadAsync(token)) return new(0, EventCardPolicy.Fit("会话已失效"), "inactive or foreign session");
            petVariant = session.IsDBNull(0) ? 0L : session.GetInt64(0);
            characterGender = session.IsDBNull(1) ? 0 : session.GetInt32(1);
        }
        command.CommandText = "SELECT Page,RewardCode FROM EventCardUseReceipts WHERE CharacterId=$id AND SessionId=$session AND RequestId=$request";
        await using (var receipt = await command.ExecuteReaderAsync(token))
            if (await receipt.ReadAsync(token))
                return receipt.GetInt64(0) == page ? new(checked((uint)receipt.GetInt64(1)), EventCardPolicy.Fit("已兑换"), "replay")
                    : new(0, EventCardPolicy.Fit("请求无效"), "request identity conflict");
        EventCardPolicy.Pool pool;
        try
        {
            var pools = EventCardPolicy.Load(Path.Combine(Path.GetDirectoryName(DatabasePath)!, EventCardPolicy.FileName));
            if (!pools.TryGetValue(page, out pool!)) return new(0, EventCardPolicy.Fit("本页未配置奖励"), "page has no approved rewards");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or JsonException or InvalidOperationException or FormatException or OverflowException or UnauthorizedAccessException)
        {
            return new(0, EventCardPolicy.Fit("奖励配置不可用"), ex.Message);
        }
        uint first = 50000001 + (page - 1) * 10;
        command.Parameters.AddWithValue("$first", first); command.Parameters.AddWithValue("$last", first + 9);
        command.CommandText = "SELECT COUNT(*) FROM CharacterCards WHERE CharacterId=$id AND CardCode BETWEEN $first AND $last AND Quantity>0";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(token)) != 10) return new(0, EventCardPolicy.Fit("需要本页十种卡片各一张"), "incomplete set");
        uint reward; int ticket; int hans; uint[] codes;
        command.CommandText = "SELECT RewardCode,Ticket,PoolVersion,Codes,Hans FROM EventCardPendingDraws WHERE CharacterId=$id AND Page=$page";
        await using (var pending = await command.ExecuteReaderAsync(token))
        {
            if (await pending.ReadAsync(token))
            {
                if (pending.GetString(2) != pool.Version) return new(0, EventCardPolicy.Fit("奖励待复核"), "pending pool version changed; no reroll");
                reward = checked((uint)pending.GetInt64(0)); ticket = pending.GetInt32(1);
                if (EventCardPolicy.Pick(pool, ticket) != reward) return new(0, EventCardPolicy.Fit("奖励待复核"), "invalid pending reward");
                hans = pending.IsDBNull(4) ? 0 : pending.GetInt32(4);
                codes = ParseEventRewardCodes(pending.IsDBNull(3) ? string.Empty : pending.GetString(3), reward);
            }
            else
            {
                ticket = (nextTicket ?? RandomNumberGenerator.GetInt32)(10000); reward = EventCardPolicy.Pick(pool, ticket);
                hans = EventCardPolicy.HansOf(pool, reward);
                codes = ResolveEventRewardCodes(EventCardPolicy.CodesOf(pool, reward), characterGender);
            }
        }
        command.Parameters.AddWithValue("$reward", reward); command.Parameters.AddWithValue("$ticket", ticket);
        command.Parameters.AddWithValue("$version", pool.Version);
        command.Parameters.AddWithValue("$codes", string.Join(',', codes));
        command.Parameters.AddWithValue("$hans", hans);
        command.CommandText = """
            INSERT INTO EventCardPendingDraws(CharacterId,Page,RewardCode,Ticket,PoolVersion,Codes,Hans,CreatedAt)
            VALUES($id,$page,$reward,$ticket,$version,$codes,$hans,$now) ON CONFLICT(CharacterId,Page) DO NOTHING
            """;
        await command.ExecuteNonQueryAsync(token);
        if (hans == 0)
            foreach (var code in codes)
            {
                var capacity = await CheckCardRewardCapacityAsync(connection, transaction, characterId, code, Convert.ToInt32(petVariant), token);
                // A permanently owned pet copy is settled as a blank draw below: the set is
                // consumed and nothing is granted, so the page is not pinned on it.
                if (capacity is CardRewardCapacity.Full or CardRewardCapacity.Unsupported)
                {
                    await transaction.CommitAsync(token);
                    return new(0, EventCardPolicy.Fit(capacity == CardRewardCapacity.Full ? "物品栏已满" : "已拥有该奖励"), capacity.ToString());
                }
            }
        var gameBefore = await GetGameInventoryItemCodesAsync(connection, transaction, characterId, token);
        var furnitureBefore = await GetInteriorInventoryItemCodesAsync(connection, transaction, characterId, token);
        // Quantity has a CHECK >=1: delete singletons BEFORE decrementing larger stacks.
        command.CommandText = "DELETE FROM CharacterCards WHERE CharacterId=$id AND CardCode BETWEEN $first AND $last AND Quantity=1";
        int consumed = await command.ExecuteNonQueryAsync(token);
        command.CommandText = "UPDATE CharacterCards SET Quantity=Quantity-1,UpdatedAt=$now WHERE CharacterId=$id AND CardCode BETWEEN $first AND $last AND Quantity>1";
        consumed += await command.ExecuteNonQueryAsync(token);
        if (consumed != 10) return new(0, EventCardPolicy.Fit("请重试"), "set balance changed");
        var granted = hans == 0 || await CreditCharacterHansAsync(connection, transaction, characterId, hans, token);
        var blankDraw = hans == 0;
        if (granted && hans == 0)
            foreach (var code in codes)
            {
                var grant = await GrantCardRewardAsync(connection, transaction, characterId, code, token);
                if (grant == CardRewardGrant.Failed) { granted = false; break; }
                if (grant == CardRewardGrant.Granted) blankDraw = false;
            }
        if (!granted
            || !await ReindexGameQuickSlotsAfterGrantAsync(connection, transaction, characterId, gameBefore, token)
            || !await RemapApartmentPlacementsAfterInsertionAsync(connection, transaction, characterId, furnitureBefore, token))
            return new(0, EventCardPolicy.Fit("请重试"), "grant/remap failed; entire transaction rolled back");
        command.CommandText = """
            INSERT INTO EventCardUseReceipts(CharacterId,SessionId,RequestId,Page,RewardCode,Ticket,PoolVersion,Codes,Hans,CreatedAt)
            VALUES($id,$session,$request,$page,$reward,$ticket,$version,$codes,$hans,$now);
            DELETE FROM EventCardPendingDraws WHERE CharacterId=$id AND Page=$page;
            """;
        await command.ExecuteNonQueryAsync(token); await transaction.CommitAsync(token);
        // A set that only granted a permanently owned pet still settles: the cards are
        // consumed and the fixed text says so instead of naming a prize nothing granted.
        return new(reward, hans > 0 ? EventCardPolicy.Fit(hans + "金币")
            : EventCardPolicy.Fit(blankDraw ? "已拥有该奖励" : PrizeName(codes)), string.Empty);
    }

    /** Operator-facing prize name: the granted catalog names, de-duplicated and joined
     *  with '+' only when a group mixes different items (bread + drink). */
    private static string PrizeName(uint[] codes)
    {
        if (codes.Length == 1 && ShopCatalog.TryGet(codes[0], out var single))
            return single.DurationDays > 0 ? single.Name + " " + single.DurationDays + "天" : single.Name;
        var names = new List<string>();
        foreach (var code in codes)
            if (ShopCatalog.TryGet(code, out var item) && !names.Contains(item.Name)) names.Add(item.Name);
        if (names.Count == 0) return "获得奖励";
        if (names.Count > 1) return string.Join('+', names);
        // A 套件 name already implies its pieces; other same-name groups show the count.
        return names[0].Contains("套件") ? names[0] : names[0] + " x " + codes.Length;
    }

    /** Stored grant list; a legacy row without Codes falls back to its single reward code. */
    private static uint[] ParseEventRewardCodes(string text, uint fallback)
    {
        if (string.IsNullOrEmpty(text)) return [fallback];
        var codes = new List<uint>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (uint.TryParse(part, out var code)) codes.Add(code);
        return codes.Count == 0 ? [fallback] : [.. codes];
    }

    /** Equipment codes carry the wearer's gender in digit (code/100000)%10 (0 female, 1 male),
     *  so the opposite-gender twin is ±100000. Everything else is returned unchanged. */
    private static uint ResolveRewardCodeGender(uint code, int characterGender)
    {
        if (!ShopCatalog.TryGet(code, out var item) || item.Section != InventorySection.Clothing) return code;
        var itemGender = (code / 100_000u) % 10u;
        if (itemGender is not (0u or 1u) || itemGender == (uint)characterGender) return code;
        var swapped = characterGender == 1 ? checked(code + 100_000u) : checked(code - 100_000u);
        return ShopCatalog.TryGet(swapped, out var pair) && pair.Section == InventorySection.Clothing
            && !CardCatalog.TryGet(swapped, out _) ? swapped : code;
    }

    private static uint[] ResolveEventRewardCodes(IReadOnlyList<uint> codes, int characterGender)
    {
        var resolved = new uint[codes.Count];
        for (var index = 0; index < codes.Count; index++) resolved[index] = ResolveRewardCodeGender(codes[index], characterGender);
        return resolved;
    }

    /** Direct gold grant for a currency reward entry (Code stays the invisible carrier). */
    private static async Task<bool> CreditCharacterHansAsync(SqliteConnection connection, SqliteTransaction transaction,
        long characterId, int hans, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Characters SET Hans = Hans + $hans, LastSavedAt = $now WHERE Id = $id";
        command.Parameters.AddWithValue("$hans", hans);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", characterId);
        return await command.ExecuteNonQueryAsync(token) == 1;
    }
}
