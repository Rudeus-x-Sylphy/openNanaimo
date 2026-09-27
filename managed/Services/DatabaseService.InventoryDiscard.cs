using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class DatabaseService
{
    // C46B is deletion, never activation: domains 41..48 share this request.
    // The ordinal has already been translated from the appropriate session map;
    // validate it again against the transaction's exact projection before decrement.
    public async Task<(bool Success, ushort Quantity)> DeleteSpecialInventoryItemAsync(
        long accountId, long characterId, string sessionId, uint itemCode,
        int inventoryIndex, CancellationToken cancellationToken = default)
    {
        var result = await ConsumeGameInventoryItemCoreAsync(accountId, characterId,
            sessionId, itemCode, inventoryIndex, false, cancellationToken, specialDelete: true);
        return (result.Success, result.Quantity);
    }

    private static async Task<List<uint>> GetShoppingCouponItemCodesAsync(
        SqliteConnection connection, SqliteTransaction transaction, long characterId,
        CancellationToken cancellationToken)
    {
        var codes = new List<uint>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ItemCode, Quantity FROM CharacterItems WHERE CharacterId = $id AND Quantity > 0 ORDER BY ItemCode";
        command.Parameters.AddWithValue("$id", characterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (codes.Count < 256 && await reader.ReadAsync(cancellationToken))
        {
            var code = checked((uint)reader.GetInt64(0));
            if (!ShopCatalog.TryGet(code, out var item) || !item.IsShoppingCoupon)
                continue;
            var count = Math.Min(reader.GetInt64(1), 256 - codes.Count);
            for (long i = 0; i < count; ++i) codes.Add(code);
        }
        return codes;
    }
}
