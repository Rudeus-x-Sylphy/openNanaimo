using System.Buffers.Binary;
using System.Text;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed class NativeDungeonState
{
    public const int Size = 5120;
    public const int PetLevelOffset = 152;
    public const int PetExperienceOffset = 156;
    public const int AttackModifierOffset = 80;
    public const int DefenseFlatOffset = 84;
    public const int PetCombatLevelOffset = 5116;
    public byte[] Bytes { get; }
    public NativeDungeonState(byte[] bytes)
    {
        if (bytes.Length != Size) throw new InvalidDataException("Invalid native state length.");
        Bytes = bytes;
        if (Get(0) != 1 || Get(1952) > 255) throw new InvalidDataException("Native worker rejected state exchange.");
    }
    public uint Get(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(offset, 4));
    public long GetBalance(int offset) => checked((long)BinaryPrimitives.ReadUInt64LittleEndian(Bytes.AsSpan(offset, 8)));
    private void Put(int offset, long value) => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(offset, 4), checked((uint)value));
    public Dictionary<uint, uint> Items => Enumerable.Range(0, checked((int)Get(1952)))
        .ToDictionary(i => Get(1956 + i * 8), i => Get(1960 + i * 8));
    public static bool IsNativeItem(uint code) => code / 1_000_000 is 14 or 17 or 19 or 21;

    public static NativeDungeonState Create(CharacterRecord c, IReadOnlyList<CharacterCardRecord> cards,
        IReadOnlyList<CharacterSkillRecord> skills)
    {
        if (c.Id > ushort.MaxValue)
            throw new InvalidDataException($"Native dungeon character identity must not exceed uint16: {c.Id}.");
        var data = new byte[Size]; data[0] = 1;
        var s = new NativeDungeonState(data);
        var name = Encoding.GetEncoding(936).GetBytes(c.Name);
        // Native quickbar account keys use 32 bytes including p_ and NUL.
        if (name.Length is < 1 or > 14) throw new InvalidDataException("Native dungeon names require 1..14 GBK bytes.");
        s.Put(4, c.Id); s.Put(8, c.Level); s.Put(12, c.Experience);
        s.Put(16, c.MaxHp); s.Put(20, c.CurrentHp); s.Put(24, c.MaxMp); s.Put(28, c.CurrentMp);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(32, 8), c.Hans);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(40, 8), c.Cash);
        s.Put(48, c.SkillPoints); s.Put(52, c.SelectedSkill0); s.Put(56, c.SelectedSkill1);
        var equippedPetItemCode = c.EquippedPetItemCode != 0
            ? c.EquippedPetItemCode
            : c.PetVariant is >= 1 and <= 3
                ? 15_000_000u + (uint)c.PetVariant
                : 0u;
        s.Put(60, c.RevivalUseCount); s.Put(64, c.QuickSlotExpansionExpires); s.Put(68, equippedPetItemCode);
        s.Put(AttackModifierOffset, c.AttackModifier); s.Put(DefenseFlatOffset, c.DefenseFlat);
        s.Put(PetCombatLevelOffset, (uint)Math.Clamp(c.InitialAttackMode + 1, 1, 3));
        var pet = c.Items.FirstOrDefault(i => i.ItemCode == equippedPetItemCode && i.Quantity > 0);
        var petState = PetProgression.GetState(c, equippedPetItemCode);
        s.Put(72, petState.CurrentStage); s.Put(76, petState.MaximumStage);
        s.Put(PetLevelOffset, petState.Level);
        s.Put(PetExperienceOffset, petState.Experience);
        // Keep the original adapter's zero additive damage and defense policy.
        s.Put(88, name.Length); s.Put(92, c.Gender); name.CopyTo(data, 96);
        ReadOnlySpan<int> equipmentAppearanceOffsets = [0, 4, 8, 12, 20];
        for (int i = 0; i < equipmentAppearanceOffsets.Length; i++)
            s.Put(112 + i * 4, BinaryPrimitives.ReadUInt32LittleEndian(
                c.Appearance.AsSpan(equipmentAppearanceOffsets[i], sizeof(uint))));
        s.Put(132, BinaryPrimitives.ReadUInt32LittleEndian(c.Appearance.AsSpan(24, 4)));
        s.Put(136, long.Parse(DateTime.Now.ToString("yyyyMMddHH")));
        s.Put(140, pet?.PetAccessory0 ?? 0); s.Put(144, pet?.PetAccessory1 ?? 0); s.Put(148, pet?.PetAccessory2 ?? 0);
        foreach (var skill in skills)
            if (skill.SkillCode is >= 52000000 and <= 52000015) s.Put(160 + (int)(skill.SkillCode - 52000000) * 4, skill.Grade);
        foreach (var card in cards)
            if (card.CardCode is >= 13000001 and <= 13000420) s.Put(272 + (int)(card.CardCode - 13000001) * 4, card.Quantity);
        var items = c.Items.Where(i => IsNativeItem(i.ItemCode) && i.Quantity > 0).OrderBy(i => i.ItemCode).ToArray();
        if (items.Length > 255 || items.Sum(i => (int)i.Quantity) > 255)
            throw new InvalidDataException("Native dungeon inventory exceeds its 255 instance handles.");
        s.Put(1952, items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            var item = items[i];
            s.Put(1956 + i * 8, item.ItemCode);
            s.Put(1960 + i * 8, item.Quantity);
        }

        // C430/C47D identities index the complete ordinary game-item list, while
        // the native worker only imports domains 14/17/19/21. Preserve an explicit
        // managed-identity -> native-handle map so skipped cash/special domains do
        // not shift quick-slot ownership and repeated codes retain distinct handles.
        var inventoryInstances = c.Items
            .Where(item => item.Quantity > 0
                && ShopCatalog.TryGet(item.ItemCode, out var catalogItem)
                && catalogItem.Section == InventorySection.GameItem
                && catalogItem.Category is not (42 or 47))
            .OrderBy(item => item.ItemCode)
            .SelectMany(item => Enumerable.Repeat(item.ItemCode, item.Quantity))
            .ToArray();
        var nativeHandlesByInventoryIdentity = new Dictionary<int, int>();
        var nextNativeHandle = 1;
        for (var inventoryIdentity = 0; inventoryIdentity < inventoryInstances.Length; inventoryIdentity++)
        {
            var code = inventoryInstances[inventoryIdentity];
            if (!IsNativeItem(code))
                continue;
            nativeHandlesByInventoryIdentity[inventoryIdentity] = nextNativeHandle;
            s.Put(4000 + nextNativeHandle * 4, code);
            nextNativeHandle++;
        }

        var selectedHandles = new HashSet<int>();
        foreach (var slot in c.QuickSlots)
        {
            if (slot.Slot > 5
                || slot.InventoryIndex >= inventoryInstances.Length
                || inventoryInstances[slot.InventoryIndex] != slot.ItemCode
                || !nativeHandlesByInventoryIdentity.TryGetValue(slot.InventoryIndex, out var nativeHandle))
                throw new InvalidDataException("Quick-slot identity does not match the native dungeon inventory.");
            if (!selectedHandles.Add(nativeHandle))
                throw new InvalidDataException("Multiple quick slots cannot reference the same native item identity.");
            s.Put(224 + slot.Slot * 8, slot.ItemCode);
            s.Put(228 + slot.Slot * 8, nativeHandle);
        }
        return s;
    }
}
