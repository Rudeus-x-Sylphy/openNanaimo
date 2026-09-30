namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    // Managed town traffic does not traverse gs_runtime.inc's selector4 fallback.
    // Complete the page-local scene from the first valid town activity, but make
    // the actor visible to the already-present peers as well as to the entrant.
    private async Task CompleteTownPetSceneOnActivityAsync(
        ConnectionSession session,
        CancellationToken token)
    {
        if (!session.TownPetSceneCompletionPending)
            return;

        session.TownPetSceneCompletionPending = false;
        if (session.TownId != 4 || session.TownSceneActive || session.Character is null
            || session.ApartmentOwnerCharacterId != 0 || session.VillageShopCode != 0
            || session.TradeRoomId != 0 || TryGetDungeonTransition(session, out _, out _))
            return;

        session.TownSceneActive = true;
        await QueueTownEntitySnapshotsAsync(session, token);
        var equippedPetItemCode = GetEquippedPetItemCode(session.Character);

        // The entrant must receive its own C47F after C36A snapshots have been
        // queued, and every peer must receive the same appearance/pet state.
        // Previously only the entrant got this frame, causing asymmetric P1/P2
        // pet visibility until a later equipment mutation.
        if (equippedPetItemCode != 0)
        {
            QueueTownBroadcast(
                session,
                0xC47F,
                BuildUserDataChangePayload(session.Character),
                "town actor entered with equipped pet");
        }
        if (equippedPetItemCode != 0
            && _activeWorldSessions.TryGetValue(session.SessionId, out var self))
        {
            session.PendingBroadcasts.Add(new PendingNativeBroadcast(
                self,
                0xC47F,
                BuildUserDataChangePayload(session.Character),
                "restore equipped pet after selector4 page activity"));
        }

        var saved = await _database.SaveCharacterRuntimeStateAsync(
            session.AccountId,
            session.Character.Id,
            session.SessionId,
            CreateRuntimeState(session.Character, session.ChannelId),
            token);
        ActivateNonCombatHealthRecovery(session, HealthRecoveryScene.Town);
        QueueUserAutoHealing(session, session, "synchronize town HP/MP after selector4 CB21");
        _log($"Selector4 page activity completed scene: page={session.TownPage} pet={equippedPetItemCode} saved={saved}; no C36C required");
    }
}
