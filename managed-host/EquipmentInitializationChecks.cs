using System.Buffers.Binary;
using System.Reflection;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

// Production constructors only: no claim of original-client UI acceptance.
internal static class EquipmentInitializationChecks
{
    internal static void Run(NetworkAdapterService service)
    {
        var type = typeof(NetworkAdapterService);
        var sessionType = type.GetNestedType("ConnectionSession", BindingFlags.NonPublic)!;
        var build = type.GetMethod("BuildLoadNecessityResponse", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var complete = type.GetMethod("CompleteTownEquipmentInitialization", BindingFlags.Static | BindingFlags.NonPublic)!;
        uint[] codes = [10130337, 10110180, 10120352, 10140001, 10150103, 10160017];
        int[] offsets = [0, 8, 12, 16, 20, 24];
        CharacterSkillRecord[] skills = [new() { SkillCode = 52000000, Grade = 3 }];
        int cases = 0;
        foreach (int level in new[] { 1, 199, 200 })
        for (int mask = 0; mask < 64; mask++)
        {
            var character = new CharacterRecord
            {
                Id = 1, AccountId = 1, Gender = 1, Name = "EquipmentInit", TutorialCompleted = true,
                Level = level, Experience = CharacterProgression.ExperienceRequiredForLevel(level),
                MaxHp = CharacterProgression.CalculateMaxHp(level), MaxMp = CharacterProgression.CalculateMaxMp(level),
                CurrentHp = mask % 3 == 0 ? 0 : 100, CurrentMp = mask % 3 == 0 ? 0 : 50,
                AttackModifier = 17, DefenseFlat = 23, SelectedSkill0 = 52000000,
                EquippedPetItemCode = 15000001,
                Items = [new() { ItemCode = 15000001, Quantity = 1 }]
            };
            for (int part = 0; part < codes.Length; part++)
                if ((mask & (1 << part)) != 0)
                    BinaryPrimitives.WriteUInt32LittleEndian(character.Appearance.AsSpan(offsets[part]), codes[part]);
            var appearance = character.Appearance.ToArray();
            var before = NetworkAdapterService.ResolveInventoryVitals(character);
            var defense = CharacterCombatProfile.EffectiveDefense(character);
            var experienceBonus = AvatarEquipmentCatalog.GetExperiencePercent(character.Appearance);
            object session = Activator.CreateInstance(sessionType, true)!;
            sessionType.GetProperty("Character")!.SetValue(session, character);
            byte[] Actor() => NativeDungeonClient.Frame(0xC368,
                NetworkAdapterService.BuildRoomEnterPayload(character, 0, 160, 304));
            byte[] Complete() => (byte[])complete.Invoke(null,
                [NativeDungeonClient.Frame(0xC367, new byte[8]), session, Actor(), skills])!;
            byte[] Initialize()
            {
                var load = (byte[])build.Invoke(service,
                    [NativeDungeonClient.Frame(0xC354, []), session, new byte[60], new byte[60], new byte[23],
                     null, ("", 0, 0)])!;
                Check(Frames(load).Select(f => U16(f, 6)).SequenceEqual(new ushort[] { 0xC355, 0xC476, 0xC44C }),
                    "inventory clear precedes actor; no premature equipped snapshot sampling uninitialized resources");
                return Complete();
            }
            Check(Frames(Complete()).Count == 1, "no snapshot before requested initialization");
            var first = Frames(Initialize());
            Check(first.Select(f => U16(f, 6)).SequenceEqual(new ushort[] { 0xC368, 0xC379 }),
                "actual actor resources precede complete equipped snapshot; no equip ACK/extra actor");
            var box = first[1];
            var queried = NetworkAdapterService.BuildBoxInfoPayloadWithSkills(character, skills);
            Check(box.AsSpan(8).SequenceEqual(queried), "initialization uses the entire standard snapshot");
            Check(box[304] == 3 && BinaryPrimitives.ReadUInt32LittleEndian(box.AsSpan(308)) == 52000000,
                "learned skill grades are available at initialization");
            var rows = NetworkAdapterService.GetAvatarInventoryRows(character);
            Check(box[11] == System.Numerics.BitOperations.PopCount((uint)mask), "all selected effect-bearing slots represented");
            for (int i = 0; i < box[11]; i++)
            {
                var code = BinaryPrimitives.ReadUInt32LittleEndian(box.AsSpan(12 + i * 12));
                var identity = BinaryPrimitives.ReadUInt32LittleEndian(box.AsSpan(16 + i * 12));
                Check(identity < rows.Count && rows[(int)identity].ItemCode == code, "equipped and owned instance identities agree");
            }
            var stale = before with { MaximumHp = 1, MaximumMp = 1 };
            foreach (var resources in new BattleResourceSnapshot?[] { null, stale, before })
            {
                var actor = NetworkAdapterService.BuildRoomEnterPayloadWithResources(character, 0, 160, 304, resources);
                Check(U16(actor, 44) == before.MaximumHp && U16(actor, 46) == before.MaximumMp
                    && U16(actor, 48) == before.CurrentHp && U16(actor, 50) == before.CurrentMp,
                    "first actor ignores stale maxima, preserves absolute current and death");
            }
            Check(Frames(Complete()).Count == 1, "gate consumed once per initialization");
            Check(Frames(Initialize())[1].AsSpan(10).SequenceEqual(box.AsSpan(10)), "reinitialization replaces, never stacks");
            Check(character.Appearance.SequenceEqual(appearance) && NetworkAdapterService.ResolveInventoryVitals(character) == before
                && CharacterCombatProfile.EffectiveDefense(character) == defense
                && AvatarEquipmentCatalog.GetExperiencePercent(character.Appearance) == experienceBonus,
                "initialization preserves loadout and server effect calculations");
            if (level == 199 && mask == 2)
                Check(before.MaximumHp == 42800, "level199 base21400 plus HP100 percent is42800 at first construction");
            cases++;
        }
        Console.WriteLine($"EQUIPMENT_INITIALIZATION_PASS cases={cases} levels=1,199,200 masks=64; host construction only");
    }
    private static List<byte[]> Frames(byte[] bytes)
    {
        var result = new List<byte[]>();
        for (int offset = 0; offset < bytes.Length;)
        {
            int length = U16(bytes, offset + 4);
            Check(length >= 8 && length <= bytes.Length - offset, "bounded frame");
            result.Add(bytes.AsSpan(offset, length).ToArray()); offset += length;
        }
        return result;
    }
    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidDataException("EQUIPMENT_INITIALIZATION_FAILED " + message);
    }
}
