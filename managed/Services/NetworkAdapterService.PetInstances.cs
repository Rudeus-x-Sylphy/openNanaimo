using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

internal sealed class PetIdentityMap
{
    internal int Limit { get; set; } = 56;
    private readonly Dictionary<ushort, (uint Code, long Instance)> _rows = [];
    private readonly HashSet<ushort> _used = [];
    private ushort[] _ordered = [];

    internal void Synchronize(IReadOnlyList<CharacterItemRecord> pets)
    {
        var keys = pets.Select(pet => (pet.ItemCode, pet.PetInstanceId)).ToArray();
        foreach (var row in _rows.Where(row => !keys.Contains(row.Value)).ToArray()) _rows.Remove(row.Key);
        foreach (var key in keys)
        {
            if (_rows.ContainsValue(key)) continue;
            var free = Enumerable.Range(0, Limit).Select(value => (ushort)value).Where(value => !_rows.ContainsKey(value)).ToArray();
            if (free.Length == 0) throw new InvalidDataException("PET inventory capacity exceeded");
            var handle = free.FirstOrDefault(value => !_used.Contains(value), free[0]);
            _rows.Add(handle, key);
            _used.Add(handle);
        }
        _ordered = keys.Select(key => _rows.Single(row => row.Value == key).Key).ToArray();
    }

    internal ushort Wire(int ordinal) => _ordered[ordinal];
    internal void Remove(ushort handle)
    {
        if (handle < Limit) _rows.Remove(handle);
    }
    internal bool TryResolve(uint handle, out ushort ordinal)
    {
        var index = handle < Limit ? Array.IndexOf(_ordered, (ushort)handle) : -1;
        ordinal = index >= 0 ? (ushort)index : (ushort)0;
        return index >= 0;
    }
}

public sealed partial class NetworkAdapterService
{
    internal static CharacterItemRecord[] GetPetWireItems(CharacterRecord? character, int capacity = PetChargeCapacity)
    {
        if (character is null) return [];
        // The client dereferences PET definitions without a missing-code guard.
        // Retain retired ownership in storage, but never publish undefined rows.
        var pets = character.Items.Where(item => item.Quantity > 0 && item.ItemCode / 1000000 == 15
            && ShopCatalog.TryGet(item.ItemCode, out var catalog)
            && (!catalog.HasExpiry
                || ClothingExpirationTime.IsActive(item.ItemExpiration, DateTime.Now)))
            .OrderBy(item => item.ItemCode).ThenBy(item => item.PetInstanceId).ToList();
        if (character.PetVariant is >= 1 and <= 3)
        {
            var code = 15000000u + (uint)character.PetVariant;
            var starter = pets.FirstOrDefault(item => item.ItemCode == code && item.PetInstanceId == 0);
            if (starter is not null) pets.Remove(starter);
            pets.Insert(0, starter ?? new CharacterItemRecord { ItemCode = code, Quantity = 1 });
        }
        var visible = pets.Take(capacity).ToArray();
        var selected = pets.FirstOrDefault(item => item.ItemCode == character.EquippedPetItemCode
            && item.PetInstanceId == character.EquippedPetInstanceId);
        if (selected is not null && !visible.Contains(selected) && visible.Length > 0) visible[^1] = selected;
        return visible;
    }

    private static int GetSelectedPetOrdinal(CharacterRecord? character)
        => Array.FindIndex(GetPetWireItems(character), item => item.ItemCode == character?.EquippedPetItemCode
            && item.PetInstanceId == character.EquippedPetInstanceId);

    private static bool TryResolveSessionPet(ConnectionSession session, uint handle,
        out ushort ordinal, out CharacterItemRecord pet)
    {
        session.PetIdentities.Limit = session.Inventory99Enabled ? InventoryCapacity.Slots : PetChargeCapacity;
        var pets = GetPetWireItems(session.Character, session.PetIdentities.Limit);
        session.PetIdentities.Synchronize(pets);
        pet = null!;
        if (!session.PetIdentities.TryResolve(handle, out ordinal)) return false;
        pet = pets[ordinal];
        return true;
    }

    private static void RewriteSessionPetIdentities(Span<byte> frame, ConnectionSession session)
    {
        if (session.Inventory99Enabled) return;
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2));
        if (session.Character is null || opcode is not (0xC44C or 0xC452 or 0xC379)) return;
        var pets = GetPetWireItems(session.Character);
        session.PetIdentities.Synchronize(pets);
        if (opcode is 0xC44C or 0xC452)
            for (var i = 0; i < pets.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(20 + i * 36, 2), session.PetIdentities.Wire(i));
        var selected = GetSelectedPetOrdinal(session.Character);
        if (opcode == 0xC44C && frame.Length >= 12)
            frame[11] = selected < 0 ? byte.MaxValue : checked((byte)session.PetIdentities.Wire(selected));
        if (opcode == 0xC379 && frame.Length >= 204 && selected >= 0)
            BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(172, 2), session.PetIdentities.Wire(selected));
    }
}
