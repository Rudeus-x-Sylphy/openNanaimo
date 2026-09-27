using System.Buffers.Binary;
using OpenNanaimo.Adapter.Models;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    internal static bool TryParseTaskCompletionRequest(
        ReadOnlySpan<byte> payload, out byte taskType, out ushort runtimeState, out uint questId)
    {
        taskType = 0;
        runtimeState = 0;
        questId = 0;
        if (payload.Length != TaskCompletionRequestPayloadLength) return false;
        var taskTypeValue = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(0, 2));
        runtimeState = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(2, 2));
        questId = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(4, 4));
        if (taskTypeValue > byte.MaxValue || runtimeState > byte.MaxValue
            || !QuestCatalog.TryGetQuest(questId, out _)) return false;
        taskType = checked((byte)taskTypeValue);
        return true;
    }

    private byte[] BuildQuestProgressFrames(byte[] request, IReadOnlyList<CharacterTaskRecord> tasks,
        ConnectionSession session, bool newlyCompleted, bool isTaskListRequest = false)
    {
        if (newlyCompleted)
        {
            session.QuestCompletionNoticePending = true;
            _log($"Quest completion notice deferred: character={session.Character?.Id}; progress persisted, no unsolicited C59C/C59D");
        }

        // C59C is request-driven by C59B. Sending it during combat creates the
        // task window and can interrupt dungeon state, so combat events persist only.
        if (!isTaskListRequest) return [];
        var list = BuildNativeFrame(request, 0xC59C, BuildTaskListPayload(tasks), session);

        if (!session.QuestCompletionNoticePending
            || !session.OnlineTracked || session.Character is null
            || session.NativeDungeon is not null || session.NativeForwarding
            || session.DungeonRoomId != 0 || session.AuxiliaryGameSession
            || session.ArenaRoomId != 0 || session.EntertainmentRoomId != 0
            || !(session.TownSceneActive || session.VillageShopCode != 0))
            return list;

        session.QuestCompletionNoticePending = false;
        if (!tasks.Any(task => task.Progress1 != 0)) return list;
        _log($"Quest completion notice delivered outside combat: character={session.Character.Id}");
        return CombineNativeFrames(list, BuildNativeFrame(request, 0xC59D, [], session));
    }

    private async Task EnsureSessionStoryQuestAsync(ConnectionSession session, CancellationToken token)
    {
        if (session.Character?.TutorialCompleted == true && QuestCatalog.MainLineQuestIds.Count > 0)
            await _database.ActivateStoryQuestAsync(session.AccountId, session.Character.Id,
                session.SessionId, QuestCatalog.MainLineQuestIds[0], token);
    }

    private Task<QuestProgressMutationResult> EvaluateSessionQuestsAsync(
        ConnectionSession session, CancellationToken token, bool cleared = false, uint score = 0,
        uint? battlePetCode = null, bool bossDefeated = false)
        => _database.EvaluateQuestObjectivesAsync(session.AccountId, session.Character!.Id,
            session.SessionId, session.PartyId <= 0,
            new QuestRunRestrictions(session.DungeonRunUsedItem, session.DungeonRunCharged,
                session.DungeonRunRevived, cleared, session.QuestClearEpisode,
                session.QuestClearDifficulty, session.QuestClearDungeonBit, score)
            {
                BattlePetCode = battlePetCode,
                BossDefeated = bossDefeated
            }, token);

    private static void ObserveQuestRun(ConnectionSession session, ushort opcode, byte[] frame)
    {
        if (opcode == 0xCF6C && frame.Length >= 0x28)
        {
            session.DungeonRunStarted = false;
            session.QuestClearEpisode = frame[0x23];
            session.QuestClearDungeonBit = frame[0x24] == 2 && frame[0x25] == 1 ? 3 : frame[0x24];
            var difficulty = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(0x26));
            session.QuestClearDifficulty = session.QuestClearDungeonBit == 3 ? difficulty : (difficulty + 1) % 3;
        }
        if (opcode == 0xCF7F && !session.DungeonRunStarted)
        {
            session.DungeonRunStarted = true;
            session.DungeonRunUsedItem = false;
            session.DungeonRunCharged = false;
            session.DungeonRunRevived = false;
            session.DungeonRunStartLevel = session.Character?.Level ?? 0;
        }
        if (opcode == 0xCF93) session.DungeonRunUsedItem = true;
        if (opcode == 0xD00D && frame.Length >= 16 && (frame[15] & 0x40) != 0)
            session.DungeonRunCharged = true;
    }
}

