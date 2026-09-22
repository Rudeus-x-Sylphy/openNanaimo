using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Services;

internal static class LauncherProfileChecks
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "open-nanaimo-launcher-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (File.Create(Path.Combine(root, "game.db"))) { }
            var database = new DatabaseService(root);
            await database.InitializeAsync();
            string nameHex = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("LauncherCheck"));
            string suffix = "p_" + nameHex;
            await File.WriteAllTextAsync(Path.Combine(root, "nanaimo_inventory_state_v1.dat"),
                "version=1\ncoin=0:12345\nnana=0:67890\nequip0=10130337\nequip1=10100028\nequip2=10110337\nequip3=10120352\nequip4=10150103\neffect=10160017\nselected_pet=15009205\nowned_equipment=10160036\nowned_pet=15009205\nowned_pet=15009105\n");
            await File.WriteAllTextAsync(Path.Combine(root, $"adapter_pet_items_{suffix}.dat"), "version=2\n");
            await File.WriteAllTextAsync(Path.Combine(root, $"card_synthesis_rewards_{suffix}.dat"), "version=1\n14000001=3\n");
            await File.WriteAllTextAsync(Path.Combine(root, $"card_inventory_{suffix}.dat"), "version=1\n13000001=2\n");
            var apartment = new byte[4076];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(apartment, 0x31545041);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(apartment.AsSpan(4), 1);
            await File.WriteAllBytesAsync(Path.Combine(root, "nanaimo_apartment_state_v1.dat"), apartment);
            string Profile(int level, int hp, int mp) =>
                $"version=2\nname_hex={nameHex}\ngender=1\nlevel={level}\npet=15009205\npet_age_a=3\npet_age_b=3\ninitial_attack_mode=2\n" +
                "equip_hair=10130337\nequip_body=10100028\nequip_top=10110337\nequip_bottom=10120352\nequip_accessory=10150103\nequip_effect=10160017\n" +
                $"hp_max={hp}\nhp_current={hp - 1}\nmp_max={mp}\nmp_current={mp - 1}\nattack=3456\ndefense=789\ncoin=0\nnana_point=0\n" +
                "card_key_gold=7\ncard_key_mystery=8\nquickbar_expiry=2099123123\nfree_magic_key_expiry=2099123123\nskill_slot_x=52000001\nskill_grade0=5\nskill_grade1=5\n";
            var first = await database.ImportLocalProfileAsync(Profile(25, 1800, 700), root);
            var second = await database.ImportLocalProfileAsync(Profile(60, 2400, 5000), root);
            Check(first.Id == second.Id, "profile reapply changes character identity");
            Check(second.Level == 25 && second.Experience == CharacterProgression.ExperienceRequiredForLevel(25),
                "profile re-registration overwrote existing level/experience progression");
            Check(second.MaxHp == 2400 && second.CurrentHp == 2399 && second.MaxMp == 5000 && second.CurrentMp == 4999, "launcher HP/MP not applied");
            Check(second.AttackModifier == 3456 && second.DefenseFlat == 789 && second.InitialAttackMode == 2, "launcher combat values not applied");
            Check(second.SelectedSkill1 == 52_000_001u && second.SkillSlotExpansionExpires == 2_099_123_123u,
                "launcher X skill did not receive the matching default slot entitlement");
            Check(second.Hans == 12345 && second.Cash == 67890, "launcher sidecar balances not applied");
            Check(second.Items.Any(x => x.ItemCode == 10160036) && second.Items.Any(x => x.ItemCode == 15009105)
                && second.Items.Any(x => x.ItemCode == 14000001 && x.Quantity == 3), "launcher backpack sidecars not applied");
            var cards = await database.GetCharacterCardsAsync(second.Id);
            var state = NativeDungeonState.Create(second, cards, await database.GetCharacterSkillsAsync(second.Id));
            Check(state.Get(NativeDungeonState.AttackModifierOffset) == 3456 && state.Get(NativeDungeonState.DefenseFlatOffset) == 789, "combat values missing from native bridge");
            Check(state.Get(NativeDungeonState.PetCombatLevelOffset) == 3, "Selina combat level missing from native bridge");
            var cf72Payload = new byte[108];
            BinaryPrimitives.WriteUInt16LittleEndian(cf72Payload.AsSpan(0, 2), checked((ushort)second.Id));
            var cf72 = NativeDungeonClient.Frame(0xCF72, cf72Payload);
            Check(NetworkAdapterService.PatchNativePetActorFrame(cf72, second)
                && cf72[0x66] == 3 && cf72[0x67] == 3,
                "native CF72 forwarding overwrote the independent PET combat level");
            Check(BinaryPrimitives.ReadUInt32LittleEndian(second.Appearance.AsSpan(16, 4)) == 0
                && BinaryPrimitives.ReadUInt32LittleEndian(second.Appearance.AsSpan(20, 4)) == 10150103u
                && state.Get(128) == 10150103u,
                "reserved D4 was mistaken for the D5 accessory in the native dungeon snapshot");
            Check(state.Items.TryGetValue(14000001, out var quantity) && quantity == 3 && state.Get(272) == 2, "backpack/cards missing from native bridge");
            Console.WriteLine("LAUNCHER_PROFILE_CHECKS_PASS level=25-preserved hp=2399/2400 mp=4999/5000 attack=3456 defense=789 selina_pet_level=3 accessory_d5=PASS inventory=PASS");
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
