using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class InventoryVitalsChecks
{
    internal static void Run()
    {
        var hpGem = ShopCatalog.All.First(item => item.Category == 17
            && FixedBonus(item, 2) == 400 && FixedBonus(item, 3) == 0).ItemCode;
        var mpGem = ShopCatalog.All.First(item => item.Category == 17
            && FixedBonus(item, 3) > 0 && FixedBonus(item, 2) == 0);
        var character = MakeCharacter(hpGem);
        var initial = NetworkAdapterService.ResolveInventoryVitals(character);
        Check(initial.MaximumHp == 22622 && initial.CurrentHp == 19000,
            "22222 base plus selected life+400 yields 22622 without healing");
        Check(character.MaxHp == 22222 && character.CurrentHp == 19000,
            "projection never writes effective maxima into the base profile");
        var fullBase = NetworkAdapterService.ResolveInventoryVitals(MakeCharacter(hpGem, 22222));
        Check(fullBase.CurrentHp == 22222 && fullBase.MaximumHp == 22622,
            "current==baseMax is not an implicit full-health flag");
        var dead = NetworkAdapterService.ResolveInventoryVitals(MakeCharacter(hpGem, 0));
        Check(dead.CurrentHp == 0, "inventory projection cannot revive");

        var live = initial with { Epoch = 5, HpAuthority = BattleHpAuthority.LocalDamage, AttackMode = 3 };
        for (var i = 0; i < 12; ++i)
            live = NetworkAdapterService.ResolveInventoryVitals(character, live);
        Check(live.MaximumHp == 22622 && live.CurrentHp == 19000 && live.Epoch == 5
            && live.AttackMode == 3 && live.HpAuthority == BattleHpAuthority.LocalDamage,
            "repeated inventory/actor refreshes do not double gems or reset epoch/power/authority");

        var staleDatabase = MakeCharacter(hpGem, 22222);
        staleDatabase.CurrentMp = 5000;
        var refreshed = NetworkAdapterService.PreserveInventoryVitalsOnRefresh(character, staleDatabase, live)!;
        Check(refreshed.CurrentHp == 19000 && staleDatabase.CurrentHp == 19000
            && refreshed.CurrentMp == 1200 && staleDatabase.CurrentMp == 1200,
            "gift inventory refresh cannot replace injured HP/MP with stale database-full values");
        var otherCharacter = MakeCharacter(hpGem);
        otherCharacter.Id++;
        Check(NetworkAdapterService.PreserveInventoryVitalsOnRefresh(character, otherCharacter, live) is null,
            "inventory snapshot cannot leak across character identities");
        otherCharacter = MakeCharacter(hpGem);
        otherCharacter.AccountId++;
        Check(NetworkAdapterService.PreserveInventoryVitalsOnRefresh(character, otherCharacter, live) is null,
            "inventory snapshot cannot leak across accounts");

        var atEffectiveFull = live with { CurrentHp = 22622, CurrentMp = 5000 };
        var townReturn = NetworkAdapterService.ResolveInventoryVitals(character,
            atEffectiveFull with { SettlementFrozen = true, HpAuthority = BattleHpAuthority.Settlement });
        Check(townReturn.MaximumHp == 22622 && townReturn.CurrentHp == 22622 && townReturn.SettlementFrozen,
            "town return retains effective-full resources above base and settlement authority");
        var belowEffectiveFull = townReturn with { CurrentHp = 22550, CurrentMp = 4995 };
        var recovered = belowEffectiveFull.ApplyNonCombatRecovery(100, 10);
        Check(recovered.CurrentHp == 22622 && recovered.CurrentMp == 5000,
            "town recovery independently caps at effective maxima");
        var nextEpoch = NetworkAdapterService.ResolveInventoryVitals(character, recovered.ForEpoch(6));
        Check(nextEpoch.MaximumHp == 22622 && nextEpoch.CurrentHp == 22622
            && !nextEpoch.SettlementFrozen && nextEpoch.Epoch == 6,
            "consecutive battle keeps effective maxima and current values without double bonus");
        Check(NetworkAdapterService.ObserveInventoryActorVitals(character, live) == live,
            "worker actor construction cannot heal a known snapshot or shrink it to base");

        var unequipped = MakeCharacter(hpGem);
        unequipped.EquippedPetItemCode = 0;
        unequipped.PetVariant = 1;
        var noPet = NetworkAdapterService.ResolveInventoryVitals(unequipped, atEffectiveFull);
        Check(noPet.MaximumHp == 22222 && noPet.CurrentHp == 22222,
            "unequip clamps over-cap current and never falls back to creation PetVariant");
        var reequipped = NetworkAdapterService.ResolveInventoryVitals(character, noPet);
        Check(reequipped.MaximumHp == 22622 && reequipped.CurrentHp == 22222,
            "re-equipping life+400 does not restore the clamped HP");
        var unavailable = MakeCharacter(hpGem);
        unavailable.Items[0].Quantity = 0;
        Check(NetworkAdapterService.ResolveInventoryVitals(unavailable).MaximumHp == 22222,
            "unowned selected pet contributes no bonus");
        var unselected = MakeCharacter(0);
        unselected.Items.Add(new CharacterItemRecord
        {
            ItemCode = 15000002, Quantity = 1, PetAccessory0 = hpGem
        });
        unselected.Items.Add(new CharacterItemRecord { ItemCode = hpGem, Quantity = 9 });
        Check(NetworkAdapterService.ResolveInventoryVitals(unselected).MaximumHp == 22222,
            "unselected pet and loose inventory gems contribute nothing");
        var duplicates = MakeCharacter(hpGem);
        duplicates.Items[0].PetAccessory1 = hpGem;
        duplicates.Items[0].PetAccessory2 = hpGem;
        Check(NetworkAdapterService.ResolveInventoryVitals(duplicates).MaximumHp == 23422,
            "three installed duplicate gems count once each, not once per code");
        var mana = MakeCharacter(hpGem);
        mana.Items[0].PetAccessory1 = mpGem.ItemCode;
        Check(NetworkAdapterService.ResolveInventoryVitals(mana).MaximumMp == 5000 + FixedBonus(mpGem, 3)
            && NetworkAdapterService.ResolveInventoryVitals(mana).CurrentMp == 1200,
            "selected type3 MP effect raises maximum without refilling current MP");
        var overflow = MakeCharacter(hpGem, int.MaxValue);
        overflow.MaxHp = int.MaxValue;
        overflow.MaxMp = int.MaxValue;
        overflow.CurrentMp = int.MaxValue;
        var saturated = NetworkAdapterService.ResolveInventoryVitals(overflow);
        Check(saturated.MaximumHp == ushort.MaxValue && saturated.CurrentHp == ushort.MaxValue
            && saturated.MaximumMp == ushort.MaxValue && saturated.CurrentMp == ushort.MaxValue,
            "base plus bonuses saturate uint16 without integer overflow");

        foreach (var (opcode, length, offset) in new (ushort, int, int)[]
        {
            (0xC368, 60, 0x34), (0xCF71, 0xB8, 0x4A),
            (0xCF72, 0x74, 0x0A), (0xD8FF, 24, 0x10)
        })
        {
            var frame = Frame(opcode, length, character.Id);
            var before = frame.ToArray();
            Check(NetworkAdapterService.NormalizeInventoryVitalsFrame(frame, character, live),
                $"{opcode:X4} normalizes its proven local tuple");
            Check(U16(frame, offset) == 22622 && U16(frame, offset + 2) == 5000
                && U16(frame, offset + 4) == 19000 && U16(frame, offset + 6) == 1200,
                $"{opcode:X4} publishes selected effective maxima and unchanged absolute current");
            Check(frame.AsSpan(0, offset).SequenceEqual(before.AsSpan(0, offset))
                && frame.AsSpan(offset + 8).SequenceEqual(before.AsSpan(offset + 8)),
                $"{opcode:X4} leaves identity, appearance, skills, inventory, rank and transport untouched");
            Check(NetworkAdapterService.NormalizeInventoryVitalsFrame(frame, character, live)
                && U16(frame, offset) == 22622, $"{opcode:X4} send normalizer is idempotent");
            var invalidLength = frame.ToArray();
            BinaryPrimitives.WriteUInt16LittleEndian(invalidLength.AsSpan(4), (ushort)(length - 1));
            Check(!NetworkAdapterService.NormalizeInventoryVitalsFrame(invalidLength, character, live),
                $"{opcode:X4} rejects a mismatched declared frame length");
        }
        foreach (var opcode in new ushort[] { 0xCF71, 0xCF72 })
        {
            var remote = Frame(opcode, 0xB8, character.Id + 1);
            var before = remote.ToArray();
            Check(!NetworkAdapterService.NormalizeInventoryVitalsFrame(remote, character, live)
                && remote.SequenceEqual(before), $"{opcode:X4} remote actor is untouched");
        }
        var refused = Frame(0xC368, 60, character.Id);
        refused[8] = 0;
        Check(!NetworkAdapterService.NormalizeInventoryVitalsFrame(refused, character, live),
            "failed C368 does not receive fabricated resources");
        foreach (var opcode in new ushort[] { 0xC355, 0xC379, 0xC476, 0xCF88 })
        {
            var frame = Frame(opcode, opcode == 0xC355 ? 728 : 324, character.Id);
            var before = frame.ToArray();
            Check(!NetworkAdapterService.NormalizeInventoryVitalsFrame(frame, character, live)
                && frame.SequenceEqual(before), $"{opcode:X4} has no invented HP/MP offsets");
        }
        var box = NetworkAdapterService.BuildBoxInfoPayload(character);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(box.AsSpan(160)) == character.EquippedPetItemCode
            && BinaryPrimitives.ReadUInt32LittleEndian(box.AsSpan(176)) == hpGem,
            "C379 carries the same selected pet and life gem used by effective resources");
        var petList = NetworkAdapterService.BuildPetInventoryPayload(character);
        Check(petList[1] == 0 && petList[3] == 0
            && BinaryPrimitives.ReadUInt32LittleEndian(petList.AsSpan(2020)) == 0,
            "C476-triggered C44C refresh cannot serialize the selected pet code as expansion expiry");
        character.PetInventoryExpansionExpires = 2099123123;
        petList = NetworkAdapterService.BuildPetInventoryPayload(character);
        Check(petList[1] == 4
            && BinaryPrimitives.ReadUInt32LittleEndian(petList.AsSpan(2020)) == 2099123123,
            "C44C expansion mode4 carries the actual expiry independent of selected pet/gems");
        character.EquippedPetItemCode = 0;
        Check(NetworkAdapterService.BuildPetInventoryPayload(character)[3] == byte.MaxValue,
            "C44C unequipped header never accidentally selects owned handle0");
        var bridgeCharacter = MakeCharacter(hpGem, 22550);
        bridgeCharacter.EquippedPetItemCode = 15009205; // three native gem slots
        bridgeCharacter.Items[0].ItemCode = 15009205;
        bridgeCharacter.Items[0].PetAccessory1 = mpGem.ItemCode;
        bridgeCharacter.CurrentMp = 5000 + FixedBonus(mpGem, 3) - 1;
        var bridgeState = NativeDungeonState.Create(bridgeCharacter, [], []);
        var bridgeResources = BattleResourceSnapshot.Capture(bridgeState, 12);
        Check(bridgeResources.MaximumHp == 22622 && bridgeResources.CurrentHp == 22550
            && bridgeResources.MaximumMp == 5000 + FixedBonus(mpGem, 3)
            && bridgeResources.CurrentMp == bridgeCharacter.CurrentMp,
            "Capture resolves effective maxima from native base and selected gems without clipping above-base current");
        for (var i = 0; i < 4; i++)
        {
            var state = BattleResourceSnapshotPolicy.CreateNextDungeonState(
                bridgeCharacter, [], [], bridgeResources, 13 + i);
            Check(state.Get(16) == 22222 && state.Get(24) == 5000
                && state.Get(20) == 22550 && state.Get(28) == bridgeCharacter.CurrentMp
                && state.Get(140) == hpGem && state.Get(144) == mpGem.ItemCode,
                "CreateNext/ApplyTo retains base maxima and effective absolute current across repeated imports");
            bridgeResources = BattleResourceSnapshot.Capture(state, 13 + i);
        }
        var inflatedSnapshot = bridgeResources with
        {
            MaximumHp = ushort.MaxValue, MaximumMp = ushort.MaxValue,
            CurrentHp = ushort.MaxValue, CurrentMp = ushort.MaxValue
        };
        inflatedSnapshot.ApplyTo(bridgeState);
        Check(bridgeState.Get(16) == 22222 && bridgeState.Get(24) == 5000
            && bridgeState.Get(20) == 22622 && bridgeState.Get(28) == bridgeResources.MaximumMp,
            "ApplyTo bounds stale snapshot values by destination effective caps without rewriting base maxima");
        bridgeCharacter.PetVariant = 1;
        bridgeCharacter.EquippedPetItemCode = 0;
        var unequippedState = NativeDungeonState.Create(bridgeCharacter, [], []);
        bridgeResources.ApplyTo(unequippedState);
        Check(unequippedState.Get(68) == 0 && unequippedState.Get(140) == 0
            && unequippedState.Get(144) == 0 && unequippedState.Get(148) == 0
            && unequippedState.Get(20) == 22222 && unequippedState.Get(28) == 5000,
            "Native Create honors explicit unequip despite PetVariant and ApplyTo drops only over-cap resources");
        CheckAvatarEquipment(hpGem);
        Console.WriteLine("INVENTORY_VITALS_CHECKS_PASS base-effective selected-gems gift-refresh no-refill town next-epoch frame-gates");
    }

    private static void CheckAvatarEquipment(uint hpGem)
    {
        var character = MakeCharacter(hpGem);
        character.Level = 25;
        character.Experience = CharacterProgression.ExperienceRequiredForLevel(25);
        character.EquippedPetItemCode = 15009205;
        character.Items[0].ItemCode = 15009205;
        BinaryPrimitives.WriteUInt32LittleEndian(character.Appearance.AsSpan(8), 10110337); // GM top HP+200%, MP+300%
        var bonuses = AvatarEquipmentCatalog.GetResourceBonuses(character.Appearance, 25);
        Check(bonuses == (8000, 1020), "avatar percent basis is level table 4000/340, not GUI maxima");
        var live = NetworkAdapterService.ResolveInventoryVitals(character);
        Check(live.MaximumHp == 30622 && live.MaximumMp == 6020
            && live.CurrentHp == 19000 && live.CurrentMp == 1200,
            "base + avatar + selected gems, absolute current preserved");
        for (int i = 0; i < 4; i++)
        {
            var state = NativeDungeonState.Create(character, [], []);
            live.ApplyTo(state);
            live = BattleResourceSnapshot.Capture(state, i + 1);
            Check(state.Get(16) == 22222 && state.Get(24) == 5000
                && live.MaximumHp == 30622 && live.MaximumMp == 6020 && live.CurrentHp == 19000,
                "avatar resources survive bridge snapshots without double counting");
        }
        var full = live with { CurrentHp = live.MaximumHp, CurrentMp = live.MaximumMp };
        character.Appearance.AsSpan().Clear();
        var naked = NetworkAdapterService.ResolveInventoryVitals(character, full);
        Check(naked.CurrentHp == 22622 && naked.CurrentMp == 5000, "unequip clamps only over-cap current");
        BinaryPrimitives.WriteUInt32LittleEndian(character.Appearance.AsSpan(8), 10110337);
        var restored = NetworkAdapterService.ResolveInventoryVitals(character, naked);
        Check(restored.MaximumHp == 30622 && restored.CurrentHp == 22622 && restored.CurrentMp == 5000,
            "re-equipping never heals or replaces GUI base");
        var giftRefresh = MakeCharacter(hpGem);
        giftRefresh.Level = 25; giftRefresh.EquippedPetItemCode = 15009205;
        giftRefresh.Items[0].ItemCode = 15009205;
        character.Appearance.CopyTo(giftRefresh.Appearance, 0);
        Check(NetworkAdapterService.PreserveInventoryVitalsOnRefresh(character, giftRefresh, restored)!.CurrentHp == 22622,
            "gift/inventory-only refresh preserves avatar-effective current");
        character.Appearance.AsSpan().Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(character.Appearance.AsSpan(8), 10110062);
        Check(AvatarEquipmentCatalog.GetResourceBonuses(character.Appearance, 25) == (930, 155),
            "flat apparel effects use numeric resource columns, not descriptions");
        character.MaxHp = 65000; character.MaxMp = 65000;
        BinaryPrimitives.WriteUInt32LittleEndian(character.Appearance.AsSpan(8), 10110337);
        var saturated = NetworkAdapterService.ResolveInventoryVitals(character);
        Check(saturated.MaximumHp == 65535 && saturated.MaximumMp == 65535, "avatar sums saturate uint16");
        character.Appearance.AsSpan().Clear();
        character.Items.Add(new CharacterItemRecord { ItemCode = 10110337, Quantity = 3 });
        Check(AvatarEquipmentCatalog.GetResourceBonuses(character.Appearance, 25) == (0, 0),
            "owned but unequipped clothing gives no bonus");
    }

    private static int FixedBonus(ShopCatalogItem item, byte type)
        => item.PetAccessoryEffects.Where(effect => effect.Enabled && effect.Type == type)
            .Sum(effect => (int)effect.FixedValue);

    private static CharacterRecord MakeCharacter(uint gem, int hp = 19000) => new()
    {
        Id = 77, AccountId = 11, Name = "Vitals", MaxHp = 22222, MaxMp = 5000,
        CurrentHp = hp, CurrentMp = 1200, EquippedPetItemCode = 15000001,
        Appearance = new byte[36],
        Items = [new CharacterItemRecord { ItemCode = 15000001, Quantity = 1, PetAccessory0 = gem }]
    };

    private static byte[] Frame(ushort opcode, int length, long uid)
    {
        var frame = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), (ushort)length);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), opcode);
        if (opcode == 0xC368) frame[8] = 100;
        if (opcode is 0xCF71 or 0xCF72)
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(opcode == 0xCF71 ? 0x1A : 8), (ushort)uid);
        return frame;
    }
    private static ushort U16(byte[] frame, int offset)
        => BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(offset, 2));
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Inventory vitals: " + message);
    }
}
