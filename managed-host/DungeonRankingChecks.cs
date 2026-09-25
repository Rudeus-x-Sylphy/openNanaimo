using System.Buffers.Binary;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenNanaimo.Adapter.Models;
using OpenNanaimo.Adapter.Services;

internal static class DungeonRankingChecks
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "open-nanaimo-dungeon-ranking-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using (File.Create(Path.Combine(root, "game.db"))) { }
        try
        {
            var db = new DatabaseService(root);
            await db.InitializeAsync();
            var characters = new List<CharacterRecord>();
            for (var index = 0; index < 12; index++)
            {
                var accountId = await db.OpenLocalAccountAsync($"rank-check-{index:D2}");
                await db.CreateLocalCharacterAsync(accountId, $"Rank{index:D2}", 1);
                var character = (await db.GetCharacterAsync(accountId))!;
                var sessionId = Guid.NewGuid().ToString("N");
                Check(await db.BeginWorldSessionAsync(accountId, character.Id, sessionId, 1, "127.0.0.1"),
                    $"ranking fixture {index} online");
                var stageScore = 1_000 + index * 100;
                var rewarded = await db.ApplyDungeonRewardAsync(
                    accountId, character.Id, sessionId, 0, 2, 1, 0,
                    score: 500 + index, elapsedMinutes: 20 - index,
                    experienceReward: 0, petExperienceReward: 0, hansReward: 0,
                    completed: true, superBoss: false, clearRating: DungeonRewardPolicy.ClearRatingB,
                    stageRecordScore: stageScore);
                Check(rewarded is not null, $"ranking fixture {index} committed");
                await db.EndWorldSessionAsync(accountId, character.Id, sessionId,
                    new CharacterRuntimeState(character.CurrentHp, character.CurrentMp, 0, 0, 0, 0, 1));
                characters.Add(character);
            }

            var leaderboard = await db.GetDungeonStageLeaderboardAsync(0, 2, 1, 0, 0);
            Check(leaderboard.Count == 10, "CF16 leaderboard is bounded to ten rows");
            Check(leaderboard.Select(record => record.BestScore)
                    .SequenceEqual(Enumerable.Range(2, 10).Reverse().Select(index => checked((uint)(1_000 + index * 100)))),
                "CF16 leaderboard sorts highest score first");
            Check(leaderboard[0].CharacterName == "Rank11" && leaderboard[^1].CharacterName == "Rank02",
                "CF16 leaderboard retained the correct top-ten identities");

            var ratings = await db.GetDungeonBestRatingsAsync(characters[11].Id);
            Check(NetworkAdapterService.ExtractPackedDungeonReadyRoomRank(
                    ratings[2 * 3], dungeon: 1, stage: 0) == 1,
                "ready-room rank extracts B from the selected dungeon archive slot");

            var nativeSelection = NativeDungeonClient.Frame(0xCF6C, new byte[44]);
            nativeSelection[0x22] = 0;
            nativeSelection[0x23] = 2;
            nativeSelection[0x24] = 1;
            nativeSelection[0x25] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(nativeSelection.AsSpan(0x26, 2), 2);
            Check(NetworkAdapterService.TryParseNativeDungeonSelectionFrame(
                    nativeSelection, out var nativeHd, out var nativeEpisode,
                    out var nativeDungeon, out var nativeStage, out var nativeDifficulty)
                && nativeHd == 0 && nativeEpisode == 2 && nativeDungeon == 1
                && nativeStage == 0 && nativeDifficulty == 0,
                "native CF6C selection is decoded in the same logical difficulty domain as persistence");

            Check(NetworkAdapterService.ShouldPersistNativeDungeonSettlement(0xCF87, deathLatched: false)
                && !NetworkAdapterService.ShouldPersistNativeDungeonSettlement(0xCF87, deathLatched: true)
                && !NetworkAdapterService.ShouldPersistNativeDungeonSettlement(0xCF15, deathLatched: false),
                "native personal rank persists only for a non-death CF87 settlement");

            var nativeCf88 = NativeDungeonClient.Frame(0xCF88, new byte[56]);
            BinaryPrimitives.WriteUInt16LittleEndian(nativeCf88.AsSpan(0x08, 2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(nativeCf88.AsSpan(0x0A, 2), 11);
            BinaryPrimitives.WriteUInt16LittleEndian(nativeCf88.AsSpan(0x0C, 2), 11);
            nativeCf88[0x0C + 0x0B] = DungeonRewardPolicy.ClearRatingS;
            BinaryPrimitives.WriteUInt32LittleEndian(nativeCf88.AsSpan(0x0C + 0x1C, 4), 14_478);
            Check(NetworkAdapterService.TryReadNativeDungeonSettlementFrame(
                    nativeCf88, 11, out var nativeSettlementRating, out var nativeSettlementScore)
                && nativeSettlementRating == DungeonRewardPolicy.ClearRatingS
                && nativeSettlementScore == 14_478
                && !NetworkAdapterService.TryReadNativeDungeonSettlementFrame(
                    nativeCf88, 12, out _, out _),
                "native CF88 parser binds the visible S/score to the local member UID");

            var nativeCf71 = NativeDungeonClient.Frame(0xCF71, new byte[0xB0]);
            nativeCf71[0xB4] = 1;
            nativeCf71[0xB5] = 0x7F;
            Check(NetworkAdapterService.PatchNativeReadyRoomRankFrame(nativeCf71, 0)
                && nativeCf71[0xB4] == 0 && nativeCf71[0xB5] == 0
                && HasValidNativeChecksum(nativeCf71),
                "native CF71 egress clears the retained worker's fabricated B for an unrated stage");
            Check(NetworkAdapterService.PatchNativeReadyRoomRankFrame(nativeCf71, 3)
                && nativeCf71[0xB4] == 3 && nativeCf71[0xB5] == 0
                && HasValidNativeChecksum(nativeCf71),
                "native CF71 egress publishes an exact S badge and rewrites checksum");

            var secretCharacter = characters[11];
            var secretSession = Guid.NewGuid().ToString("N");
            Check(await db.BeginWorldSessionAsync(secretCharacter.AccountId, secretCharacter.Id, secretSession, 1, "127.0.0.1"),
                "secret ranking fixture online");
            var secretReward = await db.ApplyDungeonRewardAsync(
                secretCharacter.AccountId, secretCharacter.Id, secretSession, 1, 3, 2, 0,
                score: 700, elapsedMinutes: 9, experienceReward: 0, petExperienceReward: 0, hansReward: 0,
                completed: true, superBoss: true, clearRating: DungeonRewardPolicy.ClearRatingA,
                stageRecordScore: 9_999);
            Check(secretReward is not null, "secret Super-BOSS ranking fixture committed");
            await db.EndWorldSessionAsync(secretCharacter.AccountId, secretCharacter.Id, secretSession,
                new CharacterRuntimeState(secretCharacter.CurrentHp, secretCharacter.CurrentMp, 0, 0, 0, 0, 1));
            var secretLeaderboard = await db.GetDungeonStageLeaderboardAsync(1, 3, 2, 1, 0);
            Check(secretLeaderboard.Count == 1 && secretLeaderboard[0].CharacterName == "Rank11"
                && secretLeaderboard[0].BestScore == 9_999,
                "secret Super-BOSS uses an independent persistent leaderboard");

            var request = new byte[] { 20, 0, 0, 0 };
            var builder = typeof(NetworkAdapterService).GetMethod(
                "BuildDungeonStageRecordsPayload", BindingFlags.Static | BindingFlags.NonPublic)!;
            var payload = (byte[])builder.Invoke(null, [request, leaderboard])!;
            Check(payload.Length == 244 && payload.AsSpan(0, 4).SequenceEqual(request),
                "CF16 payload has the retail 244-byte body and selector echo");
            Check(DecodeGbk(payload.AsSpan(4, 16)) == "Rank11"
                && BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(20, 4)) == 2_100
                && BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(24, 2)) == 1
                && BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(26, 2)) == 1,
                "CF16 first record uses name/score/level/icon at the proven offsets");

            var characterForCf71 = characters[11];
            var blank = DungeonProtocol.BuildRoomMember(
                characterForCf71, checked((ushort)characterForCf71.Id), 0, 0, 0, 0, 1, 0, 0, 0, 0);
            var ranked = DungeonProtocol.BuildRoomMember(
                characterForCf71, checked((ushort)characterForCf71.Id), 0, 0, 0, 0, 1, 0, 0, 0, 0, 3);
            Check(blank[0xB4 - 8] == 0 && ranked[0xB4 - 8] == 3 && ranked[0xB5 - 8] == 0,
                "CF71 +0xB4 publishes blank/S without leaking character level into the rank field");
            Console.WriteLine("DUNGEON_RANKING_CHECKS_PASS ready=0/B/A/S CF16=10x24 persistence=normal+secret");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static bool HasValidNativeChecksum(ReadOnlySpan<byte> frame)
    {
        uint sum = 0;
        for (var index = 4; index < frame.Length; index++)
            sum += frame[index];
        return BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(2, 2))
            == (ushort)(sum ^ 0x0E0E);
    }

    private static string DecodeGbk(ReadOnlySpan<byte> bytes)
    {
        var zero = bytes.IndexOf((byte)0);
        if (zero >= 0) bytes = bytes[..zero];
        return System.Text.Encoding.GetEncoding(936).GetString(bytes);
    }

    private static void Check(bool success, string name)
    {
        if (!success) throw new InvalidDataException("CHECK_FAILED " + name);
        Console.WriteLine("CHECK_PASS " + name);
    }
}
