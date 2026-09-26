using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

internal sealed record ApartmentHouse(long CharacterId, byte Town, byte Page, byte Slot,
    uint Exterior, uint Banner, string Text, string OwnerName, int Gender);
internal sealed record ApartmentExteriorState(bool HasHouse, uint Exterior, uint Banner, string Text,
    IReadOnlyList<ApartmentExteriorItem> Items);

public sealed partial class DatabaseService
{
    private static async Task InitializeApartmentHousingAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS CharacterApartmentHouses (
                CharacterId INTEGER PRIMARY KEY REFERENCES Characters(Id) ON DELETE CASCADE,
                Town INTEGER NOT NULL CHECK(Town BETWEEN 0 AND 4),
                Page INTEGER NOT NULL CHECK(Page BETWEEN 0 AND 255),
                Slot INTEGER NOT NULL CHECK(Slot BETWEEN 0 AND 19),
                Exterior INTEGER NOT NULL DEFAULT 0,
                Banner INTEGER NOT NULL DEFAULT 0,
                BannerText TEXT NOT NULL DEFAULT '',
                PurchasedAt TEXT NOT NULL,
                UNIQUE(Town, Page, Slot));
            CREATE TABLE IF NOT EXISTS CharacterApartmentExteriors (
                CharacterId INTEGER NOT NULL REFERENCES Characters(Id) ON DELETE CASCADE,
                ItemCode INTEGER NOT NULL,
                PRIMARY KEY(CharacterId, ItemCode));
            """;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<bool> AuthorizeApartmentHousingAsync(SqliteConnection connection,
        SqliteTransaction transaction, long accountId, long characterId, string sessionId, CancellationToken token)
    {
        await using var c = connection.CreateCommand(); c.Transaction = transaction;
        c.CommandText = """
            SELECT COUNT(*) FROM Characters c JOIN Accounts a ON c.AccountId=a.Id
            WHERE c.Id=$character AND a.Id=$account AND c.IsOnline=1 AND a.IsOnline=1
              AND c.ActiveSessionId=$session AND a.ActiveSessionId=$session
            """;
        c.Parameters.AddWithValue("$character", characterId); c.Parameters.AddWithValue("$account", accountId);
        c.Parameters.AddWithValue("$session", sessionId);
        return Convert.ToInt64(await c.ExecuteScalarAsync(token)) == 1;
    }

    internal async Task<uint> PurchaseApartmentHouseAsync(long accountId, long characterId, string sessionId,
        byte town, ushort page, ushort slot, CancellationToken token = default)
    {
        if (!ApartmentHousingPolicy.IsHouseSlot(town, page, slot)) return 40;
        await using var connection = await OpenConnectionAsync(token);
        await using var tx = connection.BeginTransaction(deferred: false);
        if (!await AuthorizeApartmentHousingAsync(connection, tx, accountId, characterId, sessionId, token)) return 40;
        await using var check = connection.CreateCommand(); check.Transaction = tx;
        check.CommandText = "SELECT COUNT(*) FROM CharacterApartmentHouses WHERE CharacterId=$id OR (Town=$town AND Page=$page AND Slot=$slot)";
        check.Parameters.AddWithValue("$id", characterId); check.Parameters.AddWithValue("$town", town);
        check.Parameters.AddWithValue("$page", page); check.Parameters.AddWithValue("$slot", slot);
        if (Convert.ToInt64(await check.ExecuteScalarAsync(token)) != 0) return 40;
        await using var debit = connection.CreateCommand(); debit.Transaction = tx;
        debit.CommandText = """
            UPDATE CharacterApartmentProfile SET RecommendationPoints=RecommendationPoints-$price
            WHERE CharacterId=$id AND RecommendationPoints >= $price
            """;
        debit.Parameters.AddWithValue("$id", characterId);
        debit.Parameters.AddWithValue("$price", ApartmentHousingPolicy.PurchaseRecommendationPoints);
        if (await debit.ExecuteNonQueryAsync(token) != 1) return 20;
        await using var insert = connection.CreateCommand(); insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO CharacterApartmentHouses(CharacterId,Town,Page,Slot,PurchasedAt)
            VALUES($id,$town,$page,$slot,$now)
            """;
        insert.Parameters.AddWithValue("$id", characterId); insert.Parameters.AddWithValue("$town", town);
        insert.Parameters.AddWithValue("$page", page); insert.Parameters.AddWithValue("$slot", slot);
        insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync(token); await tx.CommitAsync(token); return 10;
    }

    internal async Task<IReadOnlyList<ApartmentHouse>> GetApartmentHousesAsync(byte town, byte page, CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token);
        await using var c = connection.CreateCommand();
        c.CommandText = """
            SELECT h.CharacterId,h.Town,h.Page,h.Slot,h.Exterior,h.Banner,h.BannerText,c.Name,c.Gender
            FROM CharacterApartmentHouses h JOIN Characters c ON c.Id=h.CharacterId
            WHERE h.Town=$town AND h.Page=$page ORDER BY h.Slot
            """;
        c.Parameters.AddWithValue("$town", town); c.Parameters.AddWithValue("$page", page);
        var rows = new List<ApartmentHouse>(); await using var reader = await c.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) rows.Add(new(reader.GetInt64(0), (byte)reader.GetInt32(1),
            (byte)reader.GetInt32(2), (byte)reader.GetInt32(3), (uint)reader.GetInt64(4), (uint)reader.GetInt64(5),
            reader.GetString(6), reader.GetString(7), reader.GetInt32(8)));
        return rows;
    }

    internal async Task<ApartmentHouse?> GetOwnedApartmentHouseAsync(long characterId, CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token);
        await using var c = connection.CreateCommand();
        c.CommandText = """
            SELECT h.CharacterId,h.Town,h.Page,h.Slot,h.Exterior,h.Banner,h.BannerText,c.Name,c.Gender
            FROM CharacterApartmentHouses h JOIN Characters c ON c.Id=h.CharacterId WHERE h.CharacterId=$id
            """;
        c.Parameters.AddWithValue("$id",characterId);
        await using var r=await c.ExecuteReaderAsync(token);
        return await r.ReadAsync(token) ? new(r.GetInt64(0),(byte)r.GetInt32(1),(byte)r.GetInt32(2),
            (byte)r.GetInt32(3),(uint)r.GetInt64(4),(uint)r.GetInt64(5),r.GetString(6),r.GetString(7),r.GetInt32(8)) : null;
    }

    internal async Task<ApartmentExteriorState> GetApartmentExteriorStateAsync(long characterId, CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token);
        await using var tx = connection.BeginTransaction();
        bool hasHouse = false; uint exterior = 0, banner = 0; string text = "";
        await using (var c = connection.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "SELECT Exterior,Banner,BannerText FROM CharacterApartmentHouses WHERE CharacterId=$id";
            c.Parameters.AddWithValue("$id", characterId);
            await using var r = await c.ExecuteReaderAsync(token);
            if (await r.ReadAsync(token)) { hasHouse = true; exterior=(uint)r.GetInt64(0); banner=(uint)r.GetInt64(1); text=r.GetString(2); }
        }
        var items = new List<ApartmentExteriorItem>();
        await using (var c = connection.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = "SELECT ItemCode FROM CharacterApartmentExteriors WHERE CharacterId=$id ORDER BY ItemCode";
            c.Parameters.AddWithValue("$id", characterId);
            await using var r = await c.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token))
                if (ApartmentHousingPolicy.Find((uint)r.GetInt64(0)) is { } item) items.Add(item);
        }
        await tx.CommitAsync(token);
        return new(hasHouse, exterior, banner, text, items.OrderBy(i=>i.Index).ToArray());
    }

    internal async Task<byte> PurchaseApartmentExteriorAsync(long accountId, long characterId, string sessionId,
        uint code, CancellationToken token = default)
    {
        if (ApartmentHousingPolicy.Find(code) is not { } item) return 20;
        await using var connection = await OpenConnectionAsync(token);
        await using var tx = connection.BeginTransaction(deferred: false);
        if (!await AuthorizeApartmentHousingAsync(connection, tx, accountId, characterId, sessionId, token)) return 20;
        await using var c = connection.CreateCommand(); c.Transaction = tx;
        c.CommandText = "SELECT COUNT(*) FROM CharacterApartmentExteriors WHERE CharacterId=$id AND ItemCode=$code";
        c.Parameters.AddWithValue("$id", characterId); c.Parameters.AddWithValue("$code", code);
        if (Convert.ToInt64(await c.ExecuteScalarAsync(token)) != 0) return 60;
        c.CommandText = "UPDATE Characters SET Hans=Hans-$price,LastSavedAt=$now WHERE Id=$id AND Hans >= $price";
        c.Parameters.AddWithValue("$price", item.Hans); c.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));
        if (await c.ExecuteNonQueryAsync(token) != 1) return 40;
        c.CommandText = "INSERT INTO CharacterApartmentExteriors(CharacterId,ItemCode) VALUES($id,$code)";
        await c.ExecuteNonQueryAsync(token); await tx.CommitAsync(token); return 10;
    }

    internal async Task<bool> SaveApartmentExteriorAsync(long accountId, long characterId, string sessionId,
        byte[] changes, string text, CancellationToken token = default)
    {
        if (changes.Length != 8 || changes.Where((value,index)=>index%2==0).Any(value=>value>1)) return false;
        await using var connection = await OpenConnectionAsync(token);
        await using var tx = connection.BeginTransaction(deferred: false);
        if (!await AuthorizeApartmentHousingAsync(connection, tx, accountId, characterId, sessionId, token)) return false;
        uint exterior, banner;
        await using var c = connection.CreateCommand(); c.Transaction = tx;
        c.CommandText = "SELECT Exterior,Banner FROM CharacterApartmentHouses WHERE CharacterId=$id";
        c.Parameters.AddWithValue("$id", characterId);
        await using(var r = await c.ExecuteReaderAsync(token))
        {
            if (!await r.ReadAsync(token)) return false;
            exterior=(uint)r.GetInt64(0); banner=(uint)r.GetInt64(1);
        }
        for (var pair = 0; pair < 4; pair++)
        {
            if(changes[pair*2]==0) continue;
            var index=changes[pair*2+1];
            var item = ApartmentHousingPolicy.Exteriors.FirstOrDefault(i=>i.Index==index);
            if(item.Code==0 || (pair < 2) != (index<128)) return false;
            c.CommandText="SELECT COUNT(*) FROM CharacterApartmentExteriors WHERE CharacterId=$id AND ItemCode=$code";
            c.Parameters.AddWithValue("$code",item.Code);
            var owned=Convert.ToInt64(await c.ExecuteScalarAsync(token))==1; c.Parameters.RemoveAt("$code");
            if(!owned) return false;
        }
        if(changes[2]==1 && ApartmentHousingPolicy.Find(exterior)?.Index==changes[3]) exterior=0;
        if(changes[6]==1 && ApartmentHousingPolicy.Find(banner)?.Index==changes[7]) banner=0;
        if(changes[0]==1) exterior=ApartmentHousingPolicy.Exteriors.Single(i=>i.Index==changes[1]).Code;
        if(changes[4]==1) banner=ApartmentHousingPolicy.Exteriors.Single(i=>i.Index==changes[5]).Code;
        c.CommandText="UPDATE CharacterApartmentHouses SET Exterior=$exterior,Banner=$banner,BannerText=$text WHERE CharacterId=$id";
        c.Parameters.AddWithValue("$exterior",exterior); c.Parameters.AddWithValue("$banner",banner); c.Parameters.AddWithValue("$text",text);
        await c.ExecuteNonQueryAsync(token); await tx.CommitAsync(token); return true;
    }

    internal async Task<CharacterRecord?> FindApartmentOwnerAsync(string identity, CancellationToken token = default)
    {
        await using var connection = await OpenConnectionAsync(token);
        await using var c = connection.CreateCommand();
        c.CommandText="SELECT c.Id FROM Characters c JOIN Accounts a ON a.Id=c.AccountId WHERE c.Name=$name OR a.Username=$name ORDER BY c.Name=$name DESC LIMIT 1";
        c.Parameters.AddWithValue("$name",identity);
        var id=Convert.ToInt64(await c.ExecuteScalarAsync(token) ?? 0L);
        return id>0 ? await GetCharacterByIdAsync(id,token) : null;
    }
}
