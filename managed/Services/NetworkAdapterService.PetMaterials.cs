using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    // Private handle allocation policy, not a native constant: PET handles retain
    // 0..55; materials use a disjoint byte-sized range with stable session identities.
    internal const int PetMaterialIdentityBase = 56;

    internal static uint[] GetPetMaterialWireItemCodes(CharacterRecord? character)
        => character?.Items
            .Where(item => item.Quantity > 0 && InventoryClassification.IsPetMaterial(item.ItemCode)
                && ShopCatalog.TryGet(item.ItemCode, out _))
            .OrderBy(item => item.ItemCode)
            .SelectMany(item => Enumerable.Repeat(item.ItemCode, item.Quantity))
            .Take(PetChargeCapacity - GetPetWireItemCodes(character).Length).ToArray() ?? [];

    private static InventoryIdentityMap SessionPetMaterials(ConnectionSession session)
    {
        session.PetMaterialIdentities.Synchronize(GetPetMaterialWireItemCodes(session.Character));
        return session.PetMaterialIdentities;
    }

    private static bool TryResolveSessionPetMaterialIdentity(ConnectionSession session, uint wire,
        out byte ordinal, out uint code)
    {
        ordinal = 0; code = 0;
        return wire >= PetMaterialIdentityBase
            && SessionPetMaterials(session).TryStorage(wire - PetMaterialIdentityBase, out ordinal, out code);
    }

    private static bool TryResolvePetMaterialOrdinal(CharacterRecord character, uint selector, out uint code)
    {
        var items = GetPetMaterialWireItemCodes(character);
        var ordinal = (long)selector - PetMaterialIdentityBase;
        code = ordinal >= 0 && ordinal < items.Length ? items[(int)ordinal] : 0;
        return code != 0;
    }

    private static void RewriteSessionPetMaterialIdentities(Span<byte> frame, ConnectionSession session)
    {
        if (session.Character is null || frame.Length < 12
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6)) != 0xC44C) return;
        var map = SessionPetMaterials(session);
        var ordinal = 0;
        for (var i = 0; i < frame[10]; i++)
        {
            var row = frame.Slice(12 + i * PetInventoryRecordLength, PetInventoryRecordLength);
            if (InventoryClassification.IsPetMaterial(BinaryPrimitives.ReadUInt32LittleEndian(row)))
                BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(8),
                    checked((ushort)(PetMaterialIdentityBase + map.Wire(ordinal++))));
        }
    }

    private async Task<byte[]> DeletePetMaterialAsync(byte[] frame, ConnectionSession session,
        uint code, ushort identity, CancellationToken token)
    {
        await RefreshInventoryCharacterAsync(session, token);
        var valid = TryResolveSessionPetMaterialIdentity(session, identity, out var ordinal, out var ownedCode)
            && ownedCode == code;
        var result = valid && session.Character is not null
            ? await _database.DeletePetMaterialInventoryItemAsync(session.AccountId, session.Character.Id,
                session.SessionId, code, ordinal, token)
            : (Success: false, Quantity: (ushort)0);
        if (result.Success)
        {
            session.PetMaterialIdentities.Remove((uint)(identity - PetMaterialIdentityBase));
            await RefreshInventoryCharacterAsync(session, token);
            AccountStateChanged?.Invoke();
        }
        return BuildNativeFrame(frame, 0xC44E, BuildPetDeleteResultPayload(result.Success, code, identity), session);
    }
}
