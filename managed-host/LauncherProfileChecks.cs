using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
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
            string Profile(int level, int hp, int mp, string dungeonGrade) =>
                $"version=2\nname_hex={nameHex}\ngender=1\ndungeon_grade={dungeonGrade}\nlevel={level}\npet=15009205\npet_age_a=3\npet_age_b=3\ninitial_attack_mode=2\n" +
                "equip_hair=10130337\nequip_body=10100028\nequip_top=10110337\nequip_bottom=10120352\nequip_accessory=10150103\nequip_effect=10160017\n" +
                $"hp_max={hp}\nhp_current={hp - 1}\nmp_max={mp}\nmp_current={mp - 1}\nattack=3456\ndefense=789\ncoin=0\nnana_point=0\n" +
                "card_key_gold=7\ncard_key_mystery=8\nquickbar_expiry=2099123123\nfree_magic_key_expiry=2099123123\nskill_slot_x=52000001\nskill_grade0=5\nskill_grade1=5\n";
            var first = await database.ImportLocalProfileAsync(Profile(25, 1800, 700, "39"), root);
            var second = await database.ImportLocalProfileAsync(Profile(60, 2400, 5000, "auto"), root);
            Check(first.Id == second.Id, "profile reapply changes character identity");
            Check(second.Level == 25 && second.Experience == CharacterProgression.ExperienceRequiredForLevel(25),
                "profile re-registration overwrote existing level/experience progression");
            Check(first.DungeonGrade == 39 && second.DungeonGrade == 39,
                "fixed dungeon grade was not persisted or auto mode did not preserve it");
            Check(second.MaxHp == 2400 && second.CurrentHp == 2399 && second.MaxMp == 5000 && second.CurrentMp == 4999, "launcher HP/MP not applied");
            Check(second.AttackModifier == 3456 && second.DefenseFlat == 789 && second.InitialAttackMode == 2, "launcher combat values not applied");
            Check(second.SelectedSkill1 == 52_000_001u && second.SkillSlotExpansionExpires == 2_099_123_123u,
                "launcher X skill did not receive the matching default slot entitlement");
            Check(second.Hans == 12345 && second.Cash == 67890, "launcher sidecar balances not applied");
            Check(second.Items.Any(x => x.ItemCode == 10160036) && second.Items.Any(x => x.ItemCode == 15009105)
                && second.Items.Any(x => x.ItemCode == 14000001 && x.Quantity == 3), "launcher backpack sidecars not applied");
            var cards = await database.GetCharacterCardsAsync(second.Id);
            var state = NativeDungeonState.Create(second, cards, await database.GetCharacterSkillsAsync(second.Id));
            await database.RestoreNativeDungeonProgressAsync(second.Id, state, CancellationToken.None);
            Check(state.Get(NativeDungeonState.DungeonGradeOffset) == 39,
                "launcher dungeon grade missing from the managed/native state bridge");
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
            var c355 = NetworkAdapterService.BuildLoadNecessityPayload(second, new byte[60], new byte[60], new byte[4], null);
            Check(c355[0x24 - 8] == 39, "C355 does not carry the persisted dungeon grade");
            var cf71 = DungeonProtocol.BuildRoomMember(second, checked((ushort)second.Id), 0, 0, 15009205,
                CharacterProgression.ExperienceRequiredForLevel(second.Level),
                CharacterProgression.ExperienceRequiredForLevel(second.Level + 1), 0, 0, 0, 0);
            Check(cf71[0x49 - 8] == 39, "CF71 does not carry the persisted dungeon grade");
            var fixedAgain = await database.ImportLocalProfileAsync(Profile(60, 2400, 5000, "42"), root);
            Check(fixedAgain.DungeonGrade == 42, "fixed dungeon grade reapply did not replace the prior grade");
            var fixedState = NativeDungeonState.Create(fixedAgain, cards, await database.GetCharacterSkillsAsync(fixedAgain.Id));
            await database.RestoreNativeDungeonProgressAsync(fixedAgain.Id, fixedState, CancellationToken.None);
            Check(fixedState.Get(NativeDungeonState.DungeonGradeOffset) == 42
                && fixedState.Get(NativeDungeonState.DungeonGradeOffset + 4) == 0,
                "fixed dungeon grade did not reset the native frontier state");
            await CheckRejectsAsync(
                () => database.ImportLocalProfileAsync(Profile(60, 2400, 5000, "43"), root),
                "out-of-range dungeon grade was accepted");

            string listenerNameHex = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes("ListenerCheck"));
            string listenerProfile = Profile(25, 1800, 700, "23").Replace(nameHex, listenerNameHex, StringComparison.Ordinal);
            var reservation = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            reservation.Start();
            int profilePort = ((System.Net.IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            await using (var host = new NetworkAdapterService(database, _ => { }, root))
            using (var stop = new CancellationTokenSource())
            {
                var listener = host.RunLocalProfileListenerAsync(profilePort, stop.Token, root);
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, profilePort);
                await using var stream = client.GetStream();
                var bytes = System.Text.Encoding.ASCII.GetBytes(listenerProfile);
                await stream.WriteAsync(BitConverter.GetBytes((uint)bytes.Length));
                await stream.WriteAsync(bytes);
                var ack = new byte[3];
                await stream.ReadExactlyAsync(ack);
                Check(System.Text.Encoding.ASCII.GetString(ack) == "OK\n",
                    "profile listener rejected a fixed dungeon grade");
                stop.Cancel();
                try { await listener; }
                catch (OperationCanceledException) { }
            }
            var listenerNameBytes = Convert.FromHexString(listenerNameHex);
            var listenerUsername = (1000000000UL + BinaryPrimitives.ReadUInt32LittleEndian(
                System.Security.Cryptography.SHA256.HashData(listenerNameBytes))).ToString();
            var listenerAccountId = await database.GetAccountIdByUsernameAsync(listenerUsername);
            var listenerCharacter = listenerAccountId.HasValue
                ? await database.GetCharacterAsync(listenerAccountId.Value)
                : null;
            Check(listenerCharacter?.DungeonGrade == 23,
                "11999 profile registration did not persist the fixed dungeon grade");
            Console.WriteLine("LAUNCHER_PROFILE_CHECKS_PASS level=25-preserved title=39-auto/42-fixed register11999=23 C355=39 CF71=39 native_grade=PASS frontier_reset=PASS hp=2399/2400 mp=4999/5000 attack=3456 defense=789 selina_pet_level=3 accessory_d5=PASS inventory=PASS");
        }
        finally { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task CheckRejectsAsync(Func<Task<CharacterRecord>> action, string message)
    {
        try { await action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
