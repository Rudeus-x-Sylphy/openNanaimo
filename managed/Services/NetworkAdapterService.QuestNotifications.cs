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
            || !(QuestCatalog.TryGetQuest(questId, out _)
                || QuestCatalog.StoryChaptersEnabled && QuestCatalog.IsChapterQuest(questId))) return false;
        if (QuestCatalog.IsChapterQuest(questId)
            && (!QuestCatalog.StoryChaptersEnabled || taskTypeValue != 2 || runtimeState != 0)) return false;
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
        // A fixed slot alone is not completion: the fourth objective may still
        // be waiting for the durable monk clear bit.
        if (!tasks.Any(task => task.Progress1 != 0)) return list;
        _log($"Quest completion notice delivered outside combat: character={session.Character.Id}");
        return CombineNativeFrames(list, BuildNativeFrame(request, 0xC59D, [], session));
    }

    private async Task EnsureSessionStoryQuestAsync(ConnectionSession session, CancellationToken token)
    {
        if (session.Character?.TutorialCompleted == true && QuestCatalog.MainLineQuestIds.Count > 0)
            await _database.ActivateStoryQuestAsync(session.AccountId, session.Character.Id,
                session.SessionId, QuestCatalog.MainLineQuestIds[0], token);
        await EnsureSessionChapterTaskAsync(session, token);
    }

    /// <summary>
    /// Keep the fixed type-2 slot in step with the stored chapter progress. The
    /// first row starts after the tutorial when auto-start is enabled; existing
    /// progress is restored without restarting it. The database re-checks that
    /// the quest and objective still match before it writes. Only a monk
    /// receipt earned at this objective is reconciled; rewards require C599.
    /// </summary>
    private async Task EnsureSessionChapterTaskAsync(ConnectionSession session, CancellationToken token)
    {
        if (!QuestCatalog.StoryChaptersEnabled || session.Character is null) return;
        var state = await _database.ReadChapterStoryAsync(session.AccountId, session.Character.Id,
            session.SessionId,
            StoryChaptersAutoStart && session.Character.TutorialCompleted, token);
        if (state is null) return;
        state = await RefreshSessionChapterMonkAsync(session, state, token);
        // The reward is claimed by the player's own C599 click, not silently on
        // login: an automatic grant here made the "领取" button look dead because
        // the items were already delivered before the click.
        await _database.ActivateChapterTaskAsync(session.AccountId, session.Character.Id,
            session.SessionId, state.QuestId, state.ObjectiveIndex, token);
    }

    private async Task<ChapterStoryState> RefreshSessionChapterMonkAsync(
        ConnectionSession session, ChapterStoryState state, CancellationToken token)
    {
        if (session.Character is null || state.ObjectiveIndex != 3
            || state.RewardPending || state.Finished) return state;
        var refreshed = await _database.RefreshChapterMonkAsync(session.AccountId,
            session.Character.Id, session.SessionId, state.QuestId, state.Revision, token);
        if (refreshed is null || refreshed == state) return state;
        await PersistChapterTaskAsync(session, refreshed, token);
        if (refreshed.RewardPending) session.QuestCompletionNoticePending = true;
        _log($"chapter {state.QuestId}: monk policy reconciled revision={refreshed.Revision} "
            + $"rewardPending={refreshed.RewardPending}");
        return refreshed;
    }

    /// <summary>
    /// Claim the chapter reward once a reviewed row exists for the current
    /// quest. The database layer resolves the review against the embedded
    /// package and the live catalogs, grants everything in one transaction and
    /// returns the next chapter; a rejected claim leaves the chapter pending.
    /// </summary>
    private async Task<ChapterClaimResult> ClaimChapterRewardAsync(ConnectionSession session,
        ChapterStoryState state, CancellationToken token)
    {
        var review = StoryChapterRewards.FirstOrDefault(row => row.QuestId == state.QuestId);
        var result = await _database.ClaimChapterRewardsAsync(session.AccountId, session.Character!.Id,
            session.SessionId, state.QuestId, state.Revision, review, token);
        if (result.Status != ChapterClaimStatus.Claimed || result.State is null)
        {
            _log($"chapter {state.QuestId}: reward claim {result.Status} against review {review?.ReviewId ?? "<missing>"}; "
                + "the chapter stays pending");
            return result;
        }
        await PersistChapterTaskAsync(session, result.State, token);
        _log($"chapter {state.QuestId}: reward claimed review={review!.ReviewId} "
            + $"next={result.State.QuestId} revision={result.State.Revision}");
        return result;
    }

    /// <summary>
    /// Candidate chapter progression at observed room events, not page acknowledgements.
    /// Entry is 0->1; verified required settlement records the mask; returning to
    /// the same (town,page) opens exit 1->2. A later town event advances a monk
    /// chapter to objective 3; only its own durable challenge bit unlocks reward.
    /// </summary>
    private async Task<bool> ObserveChapterTownAsync(ConnectionSession session, byte townId, int page,
        CancellationToken token)
    {
        if (!QuestCatalog.StoryChaptersEnabled || session.Character is null) return false;
        // The first village entry happens exactly when the tutorial completes, so
        // the observer may have to start the chapter row itself instead of
        // waiting for the next login to create it.
        var state = await _database.ReadChapterStoryAsync(session.AccountId, session.Character.Id,
            session.SessionId, StoryChaptersAutoStart && session.Character.TutorialCompleted,
            token);
        if (state is null || state.RewardPending || state.Finished) return false;
        if (state.ObjectiveIndex == 3)
            return await RefreshSessionChapterMonkAsync(session, state, token) != state;
        if (state.ObjectiveIndex is not (0 or 1 or 2)) return false;
        var chapter = StoryChapterCatalog.Get(state.QuestId);
        // Pages repeat in every village, so comparing page alone can confirm a
        // different village's chapter. A waiting monk is reconciled separately.
        if (townId != chapter.Village) return false;
        if (state.ObjectiveIndex == 0)
        {
            // The CN client has no NPC interaction for the talk objective, so the
            // episode's dungeon room is what confirms it.
            var roomPage = ChapterStoryPolicy.DungeonRoomPage(chapter.Episode);
            if (roomPage == 0 || page != roomPage)
            {
                _log($"chapter {state.QuestId}: page {page} is not the episode {chapter.Episode} "
                    + $"dungeon room ({roomPage}); the entrance dialogue stays unconfirmed");
                return false;
            }
        }
        else
        {
            // Both the waiting state (objective 1 with every dungeon settled) and
            // the pending exit (objective 2) need the player back in the episode's
            // dungeon room: walking in is what plays the exit dialogue.
            var roomPage = ChapterStoryPolicy.DungeonRoomPage(chapter.Episode);
            // Objective 1 needs the room (mask settled); objective 2 already saw the
            // exit dialogue there, so any later town event confirms the reward.
            if (state.ObjectiveIndex == 1 && (roomPage == 0 || page != roomPage))
            {
                _log($"chapter {state.QuestId}: page {page} is not the episode {chapter.Episode} "
                    + $"dungeon room ({roomPage}); the exit dialogue stays unconfirmed");
                return false;
            }
            if (state.ObjectiveIndex == 1
                && !ChapterStoryPolicy.HasCleared(state.ClearMask, chapter.Episode))
            {
                _log($"chapter {state.QuestId}: objective 1 mask {state.ClearMask} is incomplete; "
                    + "the exit dialogue waits for every dungeon");
                return false;
            }
        }
        var confirmed = await _database.ApplyChapterDialogueAsync(session.AccountId,
            session.Character.Id, session.SessionId, state.QuestId, state.Revision,
            chapter.Episode * 2 - 1, token);
        if (confirmed is null)
        {
            _log($"chapter {state.QuestId}: confirmation rejected at revision {state.Revision}");
            return false;
        }
        confirmed = await RefreshSessionChapterMonkAsync(session, confirmed, token);
        await PersistChapterTaskAsync(session, confirmed, token);
        // The exit confirmation does not move the objective index, so the client
        // only learns the chapter is finished through the completion notice.
        if (confirmed.RewardPending && !state.RewardPending)
            session.QuestCompletionNoticePending = true;
        _log($"chapter {confirmed.QuestId}: town {townId} page {page} confirmed objective "
            + $"{state.ObjectiveIndex}->{confirmed.ObjectiveIndex} rewardPending={confirmed.RewardPending}");
        return true;
    }

    private async Task<bool> ObserveChapterClearAsync(ConnectionSession session, int episode, int dungeonBit,
        CancellationToken token)
    {
        if (!QuestCatalog.StoryChaptersEnabled || session.Character is null) return false;
        // Settlement bits 0..3 exist; only bit 3 (the super boss) settles the
        // chapter, and the policy compares the mask on the next room entry.
        if (dungeonBit is < 0 or > 3) return false;
        var state = await _database.ReadChapterStoryAsync(session.AccountId, session.Character.Id,
            session.SessionId, token: token);
        if (state is null || state.RewardPending || state.Finished || state.ObjectiveIndex != 1) return false;
        var chapter = StoryChapterCatalog.Get(state.QuestId);
        if (chapter.Episode - 1 != episode) return false;
        var cleared = await _database.ApplyChapterSettlementAsync(session.AccountId, session.Character.Id,
            session.SessionId, state.QuestId, state.Revision, episode, dungeonBit, token);
        if (cleared is null)
        {
            _log($"chapter {state.QuestId}: settlement rejected at revision {state.Revision}");
            return false;
        }
        await PersistChapterTaskAsync(session, cleared, token);
        _log($"chapter {cleared.QuestId}: dungeon {dungeonBit} of episode {episode + 1} settled "
            + $"mask={cleared.ClearMask} objective={cleared.ObjectiveIndex}");
        return true;
    }

    private async Task PersistChapterTaskAsync(ConnectionSession session, ChapterStoryState state,
        CancellationToken token)
    {
        if (session.Character is null) return;
        await _database.ActivateChapterTaskAsync(session.AccountId, session.Character.Id,
            session.SessionId, state.QuestId, state.ObjectiveIndex, token);
        // A chapter step changed the fixed slot; the client only learns about it
        // from a C59C, so the room entry that follows hands it a fresh list.
        session.ChapterTaskListDirty = true;
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
