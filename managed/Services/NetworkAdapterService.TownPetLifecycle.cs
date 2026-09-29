namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    // Managed town traffic does not traverse gs_runtime.inc's selector4 fallback.
    // This is a page-local, request-driven equivalent, not a timer or a C365 push.
    // C367 arms it only outside the retained dungeon-transition bridge; every
    // LeaveTownScene (including an incomplete page) cancels it. C36C can complete
    // the page first, in which case the existing C36C path owns the attachment.
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
        // Read the equipped PET now, not at C367: selection/unequip can change
        // during loading. Preserve the full native C47F visual/UID carrier.
        var pet = GetEquippedPetItemCode(session.Character);
        if (pet != 0 && _activeWorldSessions.TryGetValue(session.SessionId, out var self))
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
        _log($"Selector4 page activity completed scene: page={session.TownPage} pet={pet} saved={saved}; no C36C required");
    }
}
