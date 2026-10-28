using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private static byte[] AcceptInventoryStorageVersion(byte[] frame, ushort opcode, ConnectionSession session)
    {
        if (!session.OnlineTracked || session.Character is null
            || opcode is not (0xC378 or 0xC3CB or 0xC42F or 0xC44B or 0xC409 or 0xC451 or 0xC392 or 0xC423)) return frame;
        var length = opcode is 0xC409 or 0xC392 or 0xC423 ? 12 : 8;
        if (frame.Length != length + (opcode == 0xC451 ? 10 : 8)
            || BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(length)) != InventoryCapacity.ProtocolMagic
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(length + 4)) != InventoryCapacity.ProtocolVersion
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(length + 6)) != InventoryCapacity.Slots) return frame;
        if (opcode == 0xC451)
        {
            var page = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(length + 8));
            if (page is < 1 or > InventoryCapacity.Pages) return frame;
            session.InventoryChargePage = page;
        }
        session.Inventory99Enabled = true;
        session.GameInventoryIdentities.Limit = InventoryCapacity.Slots;
        session.PetIdentities.Limit = InventoryCapacity.Slots;
        session.PetMaterialIdentities.Limit = InventoryCapacity.Slots;
        var stripped = frame.AsSpan(0, length).ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(stripped.AsSpan(4), checked((ushort)length));
        return stripped;
    }

    private static uint[] GetSessionPetMaterialCodes(ConnectionSession session)
        => session.Inventory99Enabled
            ? GetFullPetMaterialCodes(session.Character, GetPetWireItems(session.Character, InventoryCapacity.Slots).Length)
            : GetPetMaterialWireItemCodes(session.Character);

    internal static uint[] GetFullPetMaterialCodes(CharacterRecord? character, int petCount)
        => character?.Items.Where(item => item.Quantity > 0
                && InventoryClassification.IsPetMaterial(item.ItemCode) && ShopCatalog.TryGet(item.ItemCode, out _))
            .OrderBy(item => item.ItemCode).SelectMany(item => Enumerable.Repeat(item.ItemCode, item.Quantity))
            .Take(Math.Max(0, InventoryCapacity.Slots - petCount)).ToArray() ?? [];

    private static byte[] BuildExtendedInventoryPayload(ushort opcode, ReadOnlySpan<byte> original,
        ConnectionSession session)
    {
        var character = session.Character!;
        if (opcode == 0xC46A) return BuildCoupleTokenInventoryPayload(character, InventoryCapacity.Slots);
        if (opcode == 0xC3CC)
        {
            var rows = GetAvatarInventoryRows(character, InventoryCapacity.Slots);
            var result = ExtendedList(rows.Count, AvatarInventoryRecordLength, character.AvatarInventoryExpansionExpires, 4);
            for (var i = 0; i < rows.Count; i++)
            {
                var row = result.AsSpan(4 + i * AvatarInventoryRecordLength, AvatarInventoryRecordLength);
                BinaryPrimitives.WriteUInt32LittleEndian(row, rows[i].ItemCode);
                BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(4), rows[i].Equipped);
                BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(6), rows[i].Slot);
                BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(8), rows[i].Expiration);
            }
            return result;
        }
        if (opcode == 0xC430)
        {
            var codes = GetGameInventoryItemCodes(character, InventoryCapacity.Slots);
            var result = ExtendedList(codes.Length, 8, character.GameInventoryExpansionExpires, 6);
            for (var i = 0; i < codes.Length; i++)
            {
                var row = result.AsSpan(4 + i * 8, 8);
                BinaryPrimitives.WriteUInt32LittleEndian(row, codes[i]);
                BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(4), checked((ushort)i));
                row[6] = character.QuickSlots.Any(slot => slot.InventoryIndex == i && slot.ItemCode == codes[i]) ? (byte)1 : (byte)0;
            }
            return result;
        }
        if (opcode is 0xC44C or 0xC452)
        {
            var pets = GetPetWireItems(character, InventoryCapacity.Slots);
            session.PetIdentities.Synchronize(pets);
            if (opcode == 0xC452)
                return BuildExtendedPetChargePayload(character, pets, session);
            var materials = GetFullPetMaterialCodes(character, pets.Length);
            var result = ExtendedList(pets.Length + materials.Length, PetInventoryRecordLength,
                character.PetInventoryExpansionExpires, 4);
            for (var i = 0; i < pets.Length; i++)
                WriteExtendedPetRow(result.AsSpan(4 + i * PetInventoryRecordLength, PetInventoryRecordLength),
                    character, pets[i], session.PetIdentities.Wire(i));
            var map = SessionPetMaterials(session);
            for (var i = 0; i < materials.Length; i++)
            {
                var row = result.AsSpan(4 + (pets.Length + i) * PetInventoryRecordLength, PetInventoryRecordLength);
                BinaryPrimitives.WriteUInt32LittleEndian(row, materials[i]);
                BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(4), PermanentItemExpiration);
                BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(8), checked((ushort)(InventoryCapacity.ExtendedPetMaterialBase + map.Wire(i))));
            }
            var tail = 4 + (pets.Length + materials.Length) * PetInventoryRecordLength;
            var selected = Array.FindIndex(pets, pet => pet.ItemCode == character.EquippedPetItemCode
                && pet.PetInstanceId == character.EquippedPetInstanceId);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(tail + 4), selected >= 0
                ? session.PetIdentities.Wire(selected) : ushort.MaxValue);
            return result;
        }
        if (opcode == 0xC40A && original.Length >= 4 && original[0] != 20 && original[0] != 30)
        {
            if ((original[0] & 128) != 0) return original.ToArray();
            return BuildExtendedInteriorInventory(character, original[0], []);
        }
        return original.ToArray();
    }

    private static byte[] ExtendedList(int count, int recordSize, uint expiry, byte mode)
    {
        var result = new byte[4 + count * recordSize + 8];
        var active = SkillSlotExpansionTime.TryDecode(expiry, out var expires) && expires > DateTime.Now;
        result[1] = (byte)(128 | (active ? mode : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), checked((ushort)count));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4 + count * recordSize), expiry);
        return result;
    }

    private static byte[] BuildExtendedPetChargePayload(CharacterRecord character, CharacterItemRecord[] all,
        ConnectionSession session)
    {
        // The charging dialog owns a fixed 56-object array. Window its list by
        // the ordinary inventory's selected page while retaining full handles.
        var start = (session.InventoryChargePage - 1) * InventoryCapacity.SlotsPerPage;
        var pets = all.Skip(start).Take(PetChargeCapacity).ToArray();
        var result = new byte[PetChargeListResponsePayloadLength];
        BinaryPrimitives.WriteUInt32LittleEndian(result, checked((uint)pets.Length));
        for (var i = 0; i < pets.Length; i++)
            WriteExtendedPetRow(result.AsSpan(4 + i * PetInventoryRecordLength, PetInventoryRecordLength),
                character, pets[i], session.PetIdentities.Wire(start + i));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2024), (uint)Math.Clamp(character.Hans, 0, uint.MaxValue));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(2028), (uint)Math.Clamp(character.Cash, 0, uint.MaxValue));
        return result;
    }

    private static bool IsExtendedEquipment(ConnectionSession session, ReadOnlySpan<byte> payload)
        => session.Inventory99Enabled && payload.Length == InventoryChangeRequestPayloadLength + 16
            && BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(136)) == 0x9949;

    private static bool TryApplySessionQuickSlotDelta(ConnectionSession session, byte[] payload,
        out List<CharacterQuickSlotRecord> slots)
    {
        if (!IsExtendedEquipment(session, payload))
        {
            var valid = TryTranslateInventoryEquipment(session, payload, out var translated);
            return TryApplyInventoryQuickSlotDelta(session.Character!, translated, out slots) && valid;
        }
        slots = session.Character!.QuickSlots.Select(slot => new CharacterQuickSlotRecord
            { Slot = slot.Slot, ItemCode = slot.ItemCode, InventoryIndex = slot.InventoryIndex }).ToList();
        var removed = payload[26]; var added = payload[27];
        if (removed > 6 || added > 6) return false;
        var map = SessionInventory(session);
        var removals = new HashSet<ushort>();
        for (var i = 0; i < removed; i++)
        {
            var wire = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(138 + 2 * i));
            if ((byte)wire != payload[28 + i] || !map.TryStorage(wire, out var index, out _)
                || !removals.Add(index)) return false;
        }
        slots.RemoveAll(slot => removals.Contains(slot.InventoryIndex));
        var additions = new HashSet<ushort>();
        for (var i = 0; i < added; i++)
        {
            var row = payload.AsSpan(36 + 8 * i, 8);
            var code = BinaryPrimitives.ReadUInt32LittleEndian(row);
            var wire = BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(4));
            if (row[6] != 1 || row[7] > 5 || !map.TryStorage(wire, out var index, out var owned)
                || code != owned || !additions.Add(index)) return false;
            var destination = row[7];
            slots.RemoveAll(slot => slot.InventoryIndex == index || slot.Slot == destination);
            slots.Add(new CharacterQuickSlotRecord { Slot = destination, ItemCode = code, InventoryIndex = index });
        }
        return slots.Count <= 6;
    }

    private static ushort SelectedInventoryPetHandle(ConnectionSession session, ReadOnlySpan<byte> payload)
        => IsExtendedEquipment(session, payload)
            ? BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(150)) : payload[85];

    private static void RewriteExtendedEquippedPet(Span<byte> frame, ConnectionSession session)
    {
        if (!session.Inventory99Enabled || frame.Length < 204
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6)) != 0xC379) return;
        var pets = GetPetWireItems(session.Character, InventoryCapacity.Slots);
        session.PetIdentities.Synchronize(pets);
        var index = Array.FindIndex(pets, pet => pet.ItemCode == session.Character!.EquippedPetItemCode
            && pet.PetInstanceId == session.Character.EquippedPetInstanceId);
        if (index >= 0) BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(172), session.PetIdentities.Wire(index));
    }

    private static void WriteExtendedPetRow(Span<byte> row, CharacterRecord character, CharacterItemRecord pet, ushort handle)
    {
        var state = PetProgression.GetState(character, pet.ItemCode, pet.PetInstanceId);
        BinaryPrimitives.WriteUInt32LittleEndian(row, pet.ItemCode);
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(4), GetPetWireExpiration(character, pet.ItemCode, pet.PetInstanceId));
        BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(8), handle);
        row[10] = state.CurrentStage; row[11] = state.MaximumStage;
        row[12] = pet.ItemCode == character.EquippedPetItemCode && pet.PetInstanceId == character.EquippedPetInstanceId ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt16LittleEndian(row.Slice(14), state.Durability);
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(16), state.Experience);
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(20), state.Accessory0);
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(24), state.Accessory1);
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(28), state.Accessory2);
        BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(32), state.Level);
    }

    internal static byte[] BuildExtendedInteriorInventory(CharacterRecord character, byte mode,
        IReadOnlyList<ApartmentPlacementRecord> placements)
    {
        var rows = GetInteriorInventoryRows(character, InventoryCapacity.Slots);
        var expiry = character.InteriorInventoryExpansionExpires;
        var active = SkillSlotExpansionTime.TryDecode(expiry, out var expires) && expires > DateTime.Now;
        var result = ExtendedList(rows.Length, InteriorInventoryRecordLength, active ? expiry : 0, 6);
        result[0] = (byte)(mode | 128); result[1] = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var row = result.AsSpan(4 + i * InteriorInventoryRecordLength, InteriorInventoryRecordLength);
            BinaryPrimitives.WriteUInt32LittleEndian(row, rows[i].Code);
            BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(4), placements.Any(placement => placement.SlotIndex == i
                && placement.ItemCode == rows[i].Code) ? (ushort)1 : (ushort)0);
            BinaryPrimitives.WriteUInt16LittleEndian(row.Slice(6), checked((ushort)i));
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(8), rows[i].Expiration);
        }
        return result;
    }
}
