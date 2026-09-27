using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    // C44C's native 803880..8038B4 branch tests only mode == 4 and writes
    // manager+0x121C = 1. It never compares frame+2028 with the current time.
    // Sending a stale nonzero expiration therefore re-locks 7F49F0's local
    // coupon gate (41195F -> 82CB50), preventing a renewal request altogether.
    // Project inactive entitlements as zero; never erase the persisted ledger.
    internal static uint GetActiveInventoryExpansionExpiration(uint expiration, DateTime? currentTime = null)
        => SkillSlotExpansionTime.TryDecode(expiration, out var expires)
            && expires > (currentTime ?? DateTime.Now)
                ? expiration
                : 0;

    private async Task<byte[]?> HandleInventoryExpansionAsync(
        byte[] frame, byte[] payload, string channel, string remote,
        ConnectionSession session, CancellationToken token)
    {
        if (!session.OnlineTracked || session.Character is null)
            return null;

        var requestControl = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(0, 2));
        if (session.LastSkillSlotExpansionRequestControl == requestControl
            && session.LastSkillSlotExpansionRequestPayload is { } cachedRequest
            && payload.AsSpan().SequenceEqual(cachedRequest)
            && session.LastSkillSlotExpansionResultPayload is { } cachedResult
            && DateTime.UtcNow - session.LastSkillSlotExpansionRequestUtc <= TimeSpan.FromSeconds(5))
        {
            _log($"{channel}:{remote} replayed C480 transport frame; returning cached C481 without consuming another ticket");
            var cachedAcknowledgement = BuildNativeFrame(frame, 0xC481, cachedResult, session);
            return cachedResult[0] == 0
                ? BuildInventoryMutationRefresh(frame, cachedAcknowledgement, session, shoppingCoupon: false)
                : cachedAcknowledgement;
        }

        var requestParsed = TryParseInventoryExpansionRequest(
            payload,
            out var requestedType,
            out var requestedIdentity);
        uint itemCode = 0;
        byte inventorySlot = 0;
        byte[] resultPayload;
        if (!requestParsed || requestedType > 6 || requestedIdentity > 83)
        {
            _log($"{channel}:{remote} inventory expansion request invalid: payload={payload.Length} type={requestedType} identity={requestedIdentity}; inventory unchanged");
            resultPayload = BuildSkillSlotExpansionResultPayload(1, requestedType, inventorySlot, itemCode, 0);
        }
        else
        {
            await RefreshInventoryCharacterAsync(session, token);
            if (session.Character is null
                || !TryResolveSessionInventoryIdentity(session, requestedIdentity, out var inventoryIndex, out var requestedCode)
                || !ShopCatalog.TryGet(requestedCode, out var ticket)
                || ticket.Category != 44
                || InventoryExpansionWireAction(ticket.InventoryExpansionType) != requestedType)
            {
                _log($"{channel}:{remote} inventory expansion rejected: no type-{requestedType} ticket in the game-item inventory");
                resultPayload = BuildSkillSlotExpansionResultPayload(1, requestedType, 0, 0, 0);
            }
            else
            {
                itemCode = ticket.ItemCode;
                inventorySlot = checked((byte)requestedIdentity);
                var use = await _database.UseInventoryExpansionAsync(
                    session.AccountId,
                    session.Character.Id,
                    session.SessionId,
                    itemCode,
                    inventoryIndex,
                    DateTime.Now,
                    token);
                if (use.Success)
                {
                    session.GameInventoryIdentities.Remove(inventorySlot);
                    await RefreshInventoryCharacterAsync(session, token);
                    SessionInventory(session);
                    AccountStateChanged?.Invoke();
                }
                _log($"{channel}:{remote} inventory expansion: type={requestedType} item={itemCode} slot={inventorySlot} storageIndex={inventoryIndex} durationDays={ticket.DurationDays} result={(use.Success ? "success" : "failure")} expires={use.Expiration} remaining={use.RemainingQuantity} error={use.Error}");
                resultPayload = BuildSkillSlotExpansionResultPayload(
                    use.Success ? (byte)0 : (byte)1,
                    requestedType,
                    inventorySlot,
                    itemCode,
                    use.Success ? use.Expiration : 0);
            }
        }

        session.LastSkillSlotExpansionRequestControl = requestControl;
        session.LastSkillSlotExpansionRequestPayload = payload.ToArray();
        session.LastSkillSlotExpansionRequestUtc = DateTime.UtcNow;
        session.LastSkillSlotExpansionResultPayload = resultPayload.ToArray();
        var acknowledgement = BuildNativeFrame(frame, 0xC481, resultPayload, session);
        return resultPayload[0] == 0
            ? BuildInventoryMutationRefresh(frame, acknowledgement, session, shoppingCoupon: false)
            : acknowledgement;
    }

    internal static bool TryParseInventoryExpansionRequest(
        ReadOnlySpan<byte> payload,
        out byte expansionType,
        out ushort inventoryIdentity)
    {
        expansionType = 0;
        inventoryIdentity = 0;
        if (payload.Length != InventoryExpansionRequestPayloadLength)
            return false;
        var rawType = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2));
        inventoryIdentity = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        if (rawType > byte.MaxValue)
            return false;
        expansionType = checked((byte)rawType);
        return true;
    }

    private static byte[] BuildSkillSlotExpansionResultPayload(
        byte result,
        byte expansionType,
        byte inventorySlot,
        uint itemCode,
        uint expiration)
    {
        // C481 is consumed as result, reserved, type, slot, item and the
        // YYYYMMDDHH expiration value. This server emits only 0 (consume and
        // activate) or 1 (reject without consumption). Native result 2 also
        // removes a coupon but follows a different branch; do not emit it.
        var payload = new byte[InventoryExpansionResponsePayloadLength];
        payload[0] = result;
        payload[2] = expansionType;
        payload[3] = inventorySlot;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), itemCode);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8, 4), expiration);
        return payload;
    }

}
