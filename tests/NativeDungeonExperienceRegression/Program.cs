using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class Program
{
    public static async Task Main()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var levelOne = NewCharacter(1, 0);
        var combatBefore = NativeDungeonState.Create(levelOne, [], []);
        var combatAfterBytes = combatBefore.Bytes.ToArray();
        Put(combatAfterBytes, 8, 3);
        Put(combatAfterBytes, 12, 900);
        var combatAfter = new NativeDungeonState(combatAfterBytes);
        var combat = DatabaseService.ResolveNativeDungeonCharacterProgression(
            combatBefore, combatAfter, null,
            storedLevel: 1, storedExperience: 0,
            vitality: 0, intelligence: 0, storedMaxHp: 160, storedMaxMp: 100);
        Check(combat.AppliedExperience == 0
            && combat.Experience == 0
            && combat.Level == 1
            && combat.GainedLevels == 0,
            "intermediate combat checkpoints cannot add character progression");

        var thresholdCharacter = NewCharacter(1, 90);
        var settlementBefore = NativeDungeonState.Create(thresholdCharacter, [], []);
        var settlementAfterBytes = settlementBefore.Bytes.ToArray();
        Put(settlementAfterBytes, 8, 4);
        Put(settlementAfterBytes, 12, 2_000);
        var settlementAfter = new NativeDungeonState(settlementAfterBytes);
        var settlement = new NativeDungeonSettlementRecord(
            0, 1, 0, 0, 0, DungeonRewardPolicy.ClearRatingS, 12_000,
            StageRecordScore: 12_000, CharacterExperienceAward: 20);
        var settled = DatabaseService.ResolveNativeDungeonCharacterProgression(
            settlementBefore, settlementAfter, settlement,
            storedLevel: 1, storedExperience: 90,
            vitality: 0, intelligence: 0, storedMaxHp: 160, storedMaxMp: 100);
        Check(settled.AppliedExperience == 20
            && settled.Experience == 110
            && settled.Level == 2
            && settled.GainedLevels == 1
            && settled.CurrentHp == settled.MaxHp
            && settled.CurrentMp == settled.MaxMp,
            "settlement applies exactly its declared award and derives the level once");

        var stale = DatabaseService.ResolveNativeDungeonCharacterProgression(
            settlementBefore, settlementAfter, null,
            storedLevel: 2, storedExperience: 110,
            vitality: 0, intelligence: 0, storedMaxHp: settled.MaxHp, storedMaxMp: settled.MaxMp);
        Check(stale.AppliedExperience == 0
            && stale.Experience == 110
            && stale.Level == 2
            && stale.GainedLevels == 0,
            "post-settlement stale checkpoints cannot overwrite committed progression");

        var boostedBytes = settlementBefore.Bytes.ToArray();
        Put(boostedBytes, 16, 2500); Put(boostedBytes, 20, 2500);
        Put(boostedBytes, 24, 350); Put(boostedBytes, 28, 350);
        var boosted = DatabaseService.ResolveNativeDungeonCharacterProgression(
            settlementBefore, new NativeDungeonState(boostedBytes), null,
            storedLevel: 1, storedExperience: 90, vitality: 0, intelligence: 0,
            storedMaxHp: 160, storedMaxMp: 100);
        Check(boosted.MaxHp == 1440 && boosted.MaxMp == 100
            && boosted.CurrentHp == 2500 && boosted.CurrentMp == 350,
            "equipped-resource recovery persists full current HP/MP independently of base maxima");
        Put(boostedBytes, 20, 9999); Put(boostedBytes, 28, 9999);
        var capped = DatabaseService.ResolveNativeDungeonCharacterProgression(
            settlementBefore, new NativeDungeonState(boostedBytes), null,
            storedLevel: 1, storedExperience: 90, vitality: 0, intelligence: 0,
            storedMaxHp: 160, storedMaxMp: 100);
        Check(capped.CurrentHp == 2500 && capped.CurrentMp == 350,
            "current resources remain bounded by effective maxima");

        var legacySettlement = settlement with { CharacterExperienceAward = null };
        var legacyAfterBytes = settlementBefore.Bytes.ToArray();
        Put(legacyAfterBytes, 12, 110);
        var legacy = DatabaseService.ResolveNativeDungeonCharacterProgression(
            settlementBefore, new NativeDungeonState(legacyAfterBytes), legacySettlement,
            storedLevel: 1, storedExperience: 90,
            vitality: 0, intelligence: 0, storedMaxHp: 160, storedMaxMp: 100);
        Check(legacy.AppliedExperience == 20 && legacy.Experience == 110 && legacy.Level == 2,
            "legacy settlement journals retain positive-delta recovery compatibility");

        var payload = new byte[4 + 2 * 0x34];
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(0, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(2, 2), checked((ushort)levelOne.Id));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4, 2), checked((ushort)levelOne.Id));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(4 + 0x34, 2), 999);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4 + 0x0C, 4), 20);
        payload[4 + 0x0B] = DungeonRewardPolicy.ClearRatingS;
        var frame = NativeDungeonClient.Frame(0xCF88, payload);
        Check(NetworkAdapterService.TryReadNativeDungeonSettlementFrame(
                frame, checked((ushort)levelOne.Id), out _, out _, out var parsedAward)
            && parsedAward == 20,
            "CF88 settlement parsing retains the per-result character award");
        var malformed = frame.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(8, 2), 1);
        Check(!NetworkAdapterService.TryReadNativeDungeonSettlementFrame(malformed, 1, out _, out _, out _),
            "settlement count must match the full frame length");
        malformed = frame.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(12 + 0x34, 2), 1);
        Check(!NetworkAdapterService.TryReadNativeDungeonSettlementFrame(malformed, 1, out _, out _, out _),
            "duplicate local identity is rejected before selecting its award");
        var untouchedRemote = frame.AsSpan(0x0C + 0x34, 0x34).ToArray();
        var committedCharacter = NewCharacter(2, 110);
        Check(NetworkAdapterService.PatchNativeCharacterProgressionFrame(
                frame, checked((uint)levelOne.Id), thresholdCharacter, committedCharacter)
            && frame[0x0C + 0x04] == 1
            && frame[0x0C + 0x0A] == 2
            && U32(frame, 0x0C + 0x0C) == 20
            && U32(frame, 0x0C + 0x10) == 110
            && U32(frame, 0x0C + 0x14) == 100
            && U32(frame, 0x0C + 0x18) == 300
            && frame.AsSpan(0x0C + 0x34, 0x34).SequenceEqual(untouchedRemote)
            && HasValidChecksum(frame),
            "CF88 client synchronization uses committed progression and preserves other members");

        // Multiplayer settlement retains each real score/rating and only patches local EXP.
        var teamPayload = new byte[4 + 3 * 0x34];
        BinaryPrimitives.WriteUInt16LittleEndian(teamPayload, 3);
        for (var slot = 0; slot < 3; slot++)
        {
            var off = 4 + slot * 0x34;
            BinaryPrimitives.WriteUInt16LittleEndian(teamPayload.AsSpan(off), (ushort)(slot + 1));
            teamPayload[off + 0x0B] = (byte)(3 + slot);
            Put(teamPayload, off + 0x1C, 100_080u + (uint)slot * 200u);
        }
        var teamResult = NativeDungeonClient.Frame(0xCF88, teamPayload);
        var teamBefore = teamResult.ToArray();
        Check(NetworkAdapterService.PatchNativeCharacterProgressionFrame(teamResult, 1, thresholdCharacter, committedCharacter), "local multiplayer EXP patched");
        for (var slot = 0; slot < 3; slot++)
        {
            var off = 12 + slot * 0x34;
            Check(U32(teamResult, off + 0x1C) == 100_080u + (uint)slot * 200u
                && teamResult[off + 0x0B] == 3 + slot, $"slot{slot} real settlement score/rating preserved");
            if (slot > 0)
                Check(teamResult.AsSpan(off, 0x34).SequenceEqual(teamBefore.AsSpan(off, 0x34)), $"remote slot{slot} entire result preserved");
        }

        await NativeDungeonExperienceChecks.RunAsync();
        Console.WriteLine("NATIVE_DUNGEON_EXPERIENCE_REGRESSION_PASS");
    }

    private static CharacterRecord NewCharacter(int level, long experience) => new()
    {
        Id = 1,
        AccountId = 1,
        Name = "ExpSync",
        Level = level,
        Experience = experience,
        Appearance = new byte[36],
        MaxHp = 160,
        CurrentHp = 160,
        MaxMp = 100,
        CurrentMp = 100,
        TutorialCompleted = true
    };

    private static uint U32(byte[] value, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(value.AsSpan(offset, 4));

    private static void Put(byte[] value, int offset, uint number)
        => BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(offset, 4), number);

    private static bool HasValidChecksum(byte[] frame)
    {
        uint sum = 0;
        for (var index = 4; index < frame.Length; index++) sum += frame[index];
        return BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2, 2)) == (ushort)(sum ^ 0x0E0E);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
