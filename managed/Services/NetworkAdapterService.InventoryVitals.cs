using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    /// <summary>
    /// CharacterRecord maxima are the persisted GUI/base values. Snapshot maxima
    /// are effective values and must never be used as input to the gem sum.
    /// Inventory changes preserve absolute current resources, not a full-health
    /// flag or a percentage. Raising a maximum does not heal the actor.
    /// </summary>
    internal static BattleResourceSnapshot ResolveInventoryVitals(
        CharacterRecord character,
        BattleResourceSnapshot? current = null)
    {
        ArgumentNullException.ThrowIfNull(character);
        var (hpBonus, mpBonus) = GetSelectedPetResourceBonuses(character);
        var (avatarHp, avatarMp) = AvatarEquipmentCatalog.GetResourceBonuses(character.Appearance, character.Level);
        var maximumHp = (ushort)Math.Clamp((long)character.MaxHp + hpBonus + avatarHp, 1, ushort.MaxValue);
        var maximumMp = (ushort)Math.Clamp((long)character.MaxMp + mpBonus + avatarMp, 0, ushort.MaxValue);
        return (current ?? new BattleResourceSnapshot(0, 0, 0)) with
        {
            MaximumHp = maximumHp,
            MaximumMp = maximumMp,
            CurrentHp = (ushort)Math.Clamp((int?)current?.CurrentHp ?? character.CurrentHp, 0, maximumHp),
            CurrentMp = (ushort)Math.Clamp((int?)current?.CurrentMp ?? character.CurrentMp, 0, maximumMp)
        };
    }

    internal static (int Hp, int Mp) GetSelectedPetResourceBonuses(CharacterRecord character)
    {
        // PetVariant is creation metadata, NOT a second equipment selector.
        // A gem in an unselected pet or loose inventory contributes nothing.
        var petCode = character.EquippedPetItemCode;
        var pet = character.Items.FirstOrDefault(item => item.ItemCode == petCode && item.Quantity > 0);
        if (petCode == 0 || pet is null || !ShopCatalog.TryGet(15, petCode, out _))
            return default;
        long hp = 0, mp = 0;
        foreach (var gemCode in new[] { pet.PetAccessory0, pet.PetAccessory1, pet.PetAccessory2 })
        {
            if (gemCode == 0 || !ShopCatalog.TryGet(17, gemCode, out var gem))
                continue;
            foreach (var effect in gem.PetAccessoryEffects)
            {
                // Match the retained native type2/3 fixed-value policy. Percent
                // fields belong to other effects; do not invent HP/MP percent math.
                if (!effect.Enabled || !float.IsFinite(effect.FixedValue) || effect.FixedValue <= 0)
                    continue;
                var value = (long)Math.Min(ushort.MaxValue, Math.Truncate((double)effect.FixedValue));
                if (effect.Type == 2) hp = Math.Min(ushort.MaxValue, hp + value);
                if (effect.Type == 3) mp = Math.Min(ushort.MaxValue, mp + value);
            }
        }
        return ((int)hp, (int)mp);
    }

    /// <summary>Inventory-only refresh. Do not use for food, revival or settlement.</summary>
    internal static BattleResourceSnapshot? PreserveInventoryVitalsOnRefresh(
        CharacterRecord previous,
        CharacterRecord refreshed,
        BattleResourceSnapshot? liveResources = null)
    {
        if (previous.Id != refreshed.Id || previous.AccountId != refreshed.AccountId)
            return null;
        var before = liveResources ?? ResolveInventoryVitals(previous);
        var after = ResolveInventoryVitals(refreshed, before);
        refreshed.CurrentHp = after.CurrentHp;
        refreshed.CurrentMp = after.CurrentMp;
        return after;
    }

    // Inventory-only mutation/read refresh (not resource-changing transactions).
    // Preserve the live ledger across the database read;
    // read the live snapshot AFTER
    // the await so an intervening recovery tick is not rolled back by DB I/O.
    private async Task RefreshInventoryCharacterAsync(ConnectionSession session, CancellationToken token)
    {
        var previous = session.Character;
        await RefreshSessionCharacterAsync(session, token);
        if (previous is null || session.Character is not { } refreshed)
            return;
        var live = session.NativeBattleResources ?? session.NonCombatResourceSnapshot;
        var preserved = PreserveInventoryVitalsOnRefresh(previous, refreshed, live);
        if (preserved is null)
            return;
        if (session.NativeBattleResources is not null)
            session.NativeBattleResources = preserved;
        else
            session.NonCombatResourceSnapshot = preserved;
    }

    /// <summary>
    /// Actor construction is not recovery. In particular the worker's historical
    /// current==baseMax -> effectiveMax rule must not overwrite a known seed.
    /// Damage, validated pickups, food and revival update the ledger separately.
    /// </summary>
    internal static BattleResourceSnapshot ObserveInventoryActorVitals(
        CharacterRecord character,
        BattleResourceSnapshot resources)
        => ResolveInventoryVitals(character, resources);

    // Call after a resource-changing DB transaction/refresh (town food/revival),
    // never for inventory-only refresh. The old snapshot otherwise masks the
    // freshly persisted current values. The caller still owns transaction success.
    private static void AdoptPersistedInventoryVitals(ConnectionSession session)
    {
        if (session.Character is not { } character)
            return;
        var previous = session.NativeBattleResources ?? session.NonCombatResourceSnapshot;
        var restored = ResolveInventoryVitals(character, previous is null ? null : previous with
        {
            CurrentHp = (ushort)Math.Clamp(character.CurrentHp, 0, ushort.MaxValue),
            CurrentMp = (ushort)Math.Clamp(character.CurrentMp, 0, ushort.MaxValue)
        });
        if (session.NativeBattleResources is not null)
            session.NativeBattleResources = restored;
        else
            session.NonCombatResourceSnapshot = restored;
    }

    // The timer calls this before choosing its snapshot-vs-database branch. This
    // makes recovery cap at effective maxima even before the first dungeon visit.
    private static void EnsureNonCombatInventoryVitals(ConnectionSession session)
    {
        if (session.Character is { } character)
            session.NonCombatResourceSnapshot = ResolveInventoryVitals(character, session.NonCombatResourceSnapshot);
    }

    // Call at FinalizeNativeFramesForSend, BEFORE ComputeNativeChecksum. No new
    // frames are created here. C355 has no closed HP/MP tuple; C379 carries the
    // selected pet/gems but no resource tuple. Neither is treated as recovery.
    private static void NormalizeInventoryVitalsForSend(Span<byte> frame, ConnectionSession session)
    {
        if (session.Character is not { } character || frame.Length < 8)
            return;
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2));
        if (opcode is not (0xC368 or 0xCF71 or 0xCF72 or 0xD8FF))
            return;
        var live = session.NativeBattleResources ?? session.NonCombatResourceSnapshot;
        NormalizeInventoryVitalsFrame(frame, character, live);
    }

    internal static bool NormalizeInventoryVitalsFrame(
        Span<byte> frame, CharacterRecord character, BattleResourceSnapshot? liveResources = null)
    {
        if (frame.Length < 8
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) != frame.Length)
            return false;
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2));
        var uid = character.Id > 0 ? GetSceneEntityId(character) : (ushort)1;
        int resourceOffset;
        switch (opcode)
        {
            case 0xC368 when frame.Length == 60 && frame[8] == 100:
                resourceOffset = 0x34;
                break;
            case 0xCF71 when frame.Length >= 0x52
                && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(0x1A, 2)) == uid:
                resourceOffset = 0x4A;
                break;
            case 0xCF72 when frame.Length >= 0x12
                && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(8, 2)) == uid:
                resourceOffset = 0x0A;
                break;
            case 0xD8FF when frame.Length == 24:
                resourceOffset = 0x10;
                break;
            default:
                return false;
        }
        var resources = ResolveInventoryVitals(character, liveResources);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(resourceOffset, 2), resources.MaximumHp);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(resourceOffset + 2, 2), resources.MaximumMp);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(resourceOffset + 4, 2), resources.CurrentHp);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(resourceOffset + 6, 2), resources.CurrentMp);
        return true;
    }
}
