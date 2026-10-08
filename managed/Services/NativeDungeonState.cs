using System.Buffers.Binary;
using System.Text;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed class NativeDungeonState
{
    public const int LegacySize = 5120;
    public const int PreviousSize = 5144;
    public const int Size = 5704;
    public const int ProtocolVersion = 4;
    public const int BaseCardCount = 420;
    public const int CardCount = 560; // pictures + SP + event + special/VIP
    public const int ExtendedCardsOffset = PreviousSize;
    public static int CardOffsetAt(int index)
    {
        if ((uint)index >= CardCount) throw new ArgumentOutOfRangeException(nameof(index));
        return index < BaseCardCount ? 272 + index * 4 : ExtendedCardsOffset + (index - BaseCardCount) * 4;
    }
    public static uint CardCodeAt(int index)
    {
        if ((uint)index >= CardCount) throw new ArgumentOutOfRangeException(nameof(index));
        return index < 420 ? 13000001u + (uint)index
            : index < 440 ? 12000001u + (uint)(index - 420)
            : index < 540 ? 50000001u + (uint)(index - 440)
            : 22000001u + (uint)(index - 540);
    }
    public static bool TryGetCardOffset(uint code, out int offset)
    {
        var index = code is >= 13000001 and <= 13000420 ? (int)(code - 13000001)
            : code is >= 12000001 and <= 12000020 ? 420 + (int)(code - 12000001)
            : code is >= 50000001 and <= 50000100 ? 440 + (int)(code - 50000001)
            : code is >= 22000001 and <= 22000020 ? 540 + (int)(code - 22000001) : -1;
        offset = index < 0 ? -1 : CardOffsetAt(index);
        return index >= 0;
    }
    public const int VersionOffset = 5124;
    public const int LengthOffset = 5128;
    public const int CurveVersionOffset = 5132;
    public const int TotalExperienceOffset = 5136;
    public static bool IsSupportedSize(int length) => length == Size;
    public const int CouplePartnerUidOffset = 5120;
    public const int PetLevelOffset = 152;
    public const int PetExperienceOffset = 156;
    public const int AttackModifierOffset = 80;
    public const int DefenseFlatOffset = 84;
    public const int DungeonGradeOffset = 5024;
    public const int DungeonGradeStateLength = 28;
    // The internal state control word keeps clear-mask validity in its low
    // half and the independent meat-skill SP balance in its high half.
    public const int SkillPointsMeatOffset = 5114;
    public const int PetCombatLevelOffset = 5116;
    public byte[] Bytes { get; }
    public NativeDungeonState(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (!IsSupportedSize(bytes.Length)) throw new InvalidDataException("Invalid native state length.");
        Bytes = bytes;
        if (Get(0) != ProtocolVersion || Get(VersionOffset) != ProtocolVersion
            || Get(LengthOffset) != Size || Get(CurveVersionOffset) != CharacterProgression.CurveVersion
            || Get(12) != 0 || Get(1952) > 255)
            throw new InvalidDataException("Native state version/length/curve mismatch; offline migration required.");
        CharacterProgression.Validate(checked((int)Get(8)), TotalExperience64);
        for (var i = 0; i < CardCount; i++)
            if (Get(CardOffsetAt(i)) > byte.MaxValue)
                throw new InvalidDataException("Native card quantity exceeds the album byte limit.");
    }
    public long TotalExperience64 => checked((long)BinaryPrimitives.ReadUInt64LittleEndian(Bytes.AsSpan(TotalExperienceOffset, 8)));
    public void SetProgression(int level, long totalExperience)
    {
        CharacterProgression.Validate(level, totalExperience);
        Put(8, level); Put(12, 0);
        BinaryPrimitives.WriteUInt64LittleEndian(Bytes.AsSpan(TotalExperienceOffset, 8), checked((ulong)totalExperience));
    }
    public uint Get(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(offset, 4));
    public long GetBalance(int offset) => checked((long)BinaryPrimitives.ReadUInt64LittleEndian(Bytes.AsSpan(offset, 8)));
    private void Put(int offset, long value) => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(offset, 4), checked((uint)value));
    public Dictionary<uint, uint> Items => Enumerable.Range(0, checked((int)Get(1952)))
        .ToDictionary(i => Get(1956 + i * 8), i => Get(1960 + i * 8));
    public static bool IsNativeItem(uint code) => code / 1_000_000 is 14 or 17 or 19 or 21;

    // F100 maxima stay base; only current is effective. Resolve against this
    // state's own selected-pet/gem tuple, never a stale session snapshot maximum.
    internal (ushort Hp, ushort Mp) GetEffectiveResourceMaximums()
    {
        var pet = Get(68);
        var appearance = new byte[36];
        ReadOnlySpan<int> offsets = [0, 4, 8, 12, 20];
        for (int i = 0; i < offsets.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(offsets[i], 4), Get(112 + i * 4));
        BinaryPrimitives.WriteUInt32LittleEndian(appearance.AsSpan(24, 4), Get(132));
        var resources = NetworkAdapterService.ResolveInventoryVitals(new CharacterRecord
        {
            Level = checked((int)Math.Min(Get(8), CharacterProgression.MaximumLevel)),
            Appearance = appearance,
            MaxHp = checked((int)Math.Min(Get(16), ushort.MaxValue)),
            MaxMp = checked((int)Math.Min(Get(24), ushort.MaxValue)),
            EquippedPetItemCode = pet,
            Items = pet == 0 ? [] : [new CharacterItemRecord
            {
                ItemCode = pet, Quantity = 1,
                PetAccessory0 = Get(140), PetAccessory1 = Get(144), PetAccessory2 = Get(148)
            }]
        });
        return (resources.MaximumHp, resources.MaximumMp);
    }

    public static NativeDungeonState Create(CharacterRecord c, IReadOnlyList<CharacterCardRecord> cards,
        IReadOnlyList<CharacterSkillRecord> skills)
    {
        if (c.Id > ushort.MaxValue)
            throw new InvalidDataException($"Native dungeon character identity must not exceed uint16: {c.Id}.");
        CharacterProgression.Validate(c.Level, c.Experience, c.CurveVersion);
        var data = new byte[Size]; data[0] = ProtocolVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(VersionOffset), ProtocolVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(LengthOffset), Size);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(CurveVersionOffset), CharacterProgression.CurveVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), checked((uint)c.Level));
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(TotalExperienceOffset), checked((ulong)c.Experience));
        var s = new NativeDungeonState(data);
        var name = Encoding.GetEncoding(936).GetBytes(c.Name);
        // Native quickbar account keys use 32 bytes including p_ and NUL.
        if (name.Length is < 1 or > 14) throw new InvalidDataException("Native dungeon names require 1..14 GBK bytes.");
        s.Put(4, c.Id); s.SetProgression(c.Level, c.Experience);
        s.Put(16, c.MaxHp); s.Put(20, c.CurrentHp); s.Put(24, c.MaxMp); s.Put(28, c.CurrentMp);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(32, 8), c.Hans);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(40, 8), c.Cash);
        s.Put(48, c.SkillPoints); BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(SkillPointsMeatOffset, 2), c.SkillPointsMeat); s.Put(52, c.SelectedSkill0); s.Put(56, c.SelectedSkill1);
        // Zero is an explicit unequip, not a request to restore the creation pet.
        var equippedPetItemCode = c.EquippedPetItemCode;
        s.Put(60, c.RevivalUseCount); s.Put(64, c.QuickSlotExpansionExpires); s.Put(68, equippedPetItemCode);
        s.Put(AttackModifierOffset, CharacterCombatProgression.NativeAttack(c.Level, c.AttackModifier));
        s.Put(DefenseFlatOffset, CharacterCombatProgression.NativeDefense(c.Level, c.DefenseFlat));
        s.Put(PetCombatLevelOffset, (uint)Math.Clamp(c.InitialAttackMode + 1, 1, 3));
        var pet = c.Items.FirstOrDefault(i => i.ItemCode == equippedPetItemCode && i.Quantity > 0);
        var petState = PetProgression.GetState(c, equippedPetItemCode);
        s.Put(72, petState.CurrentStage); s.Put(76, petState.MaximumStage);
        s.Put(PetLevelOffset, petState.Level);
        s.Put(PetExperienceOffset, petState.Experience);
        s.Put(DungeonGradeOffset, CharacterTitleState.GetGrade(c));
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
            if (TryGetCardOffset(card.CardCode, out var cardOffset)) s.Put(cardOffset, card.Quantity);
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
                && catalogItem.IsGameInventoryItem)
            .OrderBy(item => item.ItemCode)
            .SelectMany(item => Enumerable.Repeat(item.ItemCode, item.Quantity))
            .ToArray();
        // The native worker retains 14/17/19/21 in one legacy ledger even though
        // client material inventory moved to C44C. Populate ALL native instances
        // first, then crosswalk only the current C430 ordinals by same-code occurrence.
        var handlesByCode = new Dictionary<uint, Queue<int>>();
        var nextNativeHandle = 1;
        foreach (var item in items)
        {
            var handles = new Queue<int>();
            handlesByCode.Add(item.ItemCode, handles);
            for (int i = 0; i < item.Quantity; i++)
            {
                s.Put(4000 + nextNativeHandle * 4, item.ItemCode);
                handles.Enqueue(nextNativeHandle++);
            }
        }
        var nativeHandlesByInventoryIdentity = new Dictionary<int, int>();
        for (var inventoryIdentity = 0; inventoryIdentity < inventoryInstances.Length; inventoryIdentity++)
            if (handlesByCode.TryGetValue(inventoryInstances[inventoryIdentity], out var handles) && handles.Count > 0)
                nativeHandlesByInventoryIdentity[inventoryIdentity] = handles.Dequeue();

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
