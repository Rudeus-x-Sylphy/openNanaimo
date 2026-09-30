namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private readonly object _nativeCoupleIdentityGate = new();

    private static bool HasSameNativeCoupleStage(ConnectionSession first, ConnectionSession second)
        => first.NativeDungeon is not null && second.NativeDungeon is not null
            && first.NativeLease is not null && second.NativeLease is not null
            && first.NativeLease.Port == second.NativeLease.Port
            && first.NativeLease.Generation == second.NativeLease.Generation
            && first.NativeDungeonSelectionValid && second.NativeDungeonSelectionValid
            && first.NativeDungeonHdIndex == second.NativeDungeonHdIndex
            && first.NativeDungeonEpisode == second.NativeDungeonEpisode
            && first.NativeDungeonDungeon == second.NativeDungeonDungeon
            && first.NativeDungeonStage == second.NativeDungeonStage
            && first.NativeDungeonLogicalDifficulty == second.NativeDungeonLogicalDifficulty;

    private async Task<bool> HasNativeRoomCoupleEncounterAsync(ConnectionSession viewer, CancellationToken token)
    {
        if (!IsTrackedWorldSession(viewer) || !viewer.NativeDungeonSelectionValid) return false;
        var epoch = viewer.NativeBattleEpoch;
        foreach (var presence in _activeWorldSessions.Values)
        {
            var member = presence.Session;
            if (!IsTrackedWorldSession(member) || member.Character is null
                || !HasSameNativeCoupleStage(viewer, member)) continue;
            var relation = await _database.GetActiveCoupleRelationAsync(member.Character.Id, token);
            if (relation is not null && FindNativeDungeonPartner(member, relation) is { } partner
                && viewer.NativeBattleEpoch == epoch && IsTrackedWorldSession(viewer)
                && HasSameNativeCoupleStage(viewer, member)
                && HasSameNativeCoupleStage(viewer, partner.Session)) return true;
        }
        return false;
    }

    private async Task PatchValidatedNativeCoupleFrameAsync(
        ConnectionSession session, byte[] response, CancellationToken token)
    {
        if (!CoupleProtocol.TryReadDungeonResponseOpcode(response, out var opcode)
            || session.Character is null) return;
        var epoch = session.NativeBattleEpoch;
        if (opcode == 0xCFEC && response.Length == 0x328)
        {
            response[CoupleBenefitPolicy.SpecialMonsterFrameOffset] =
                await HasNativeRoomCoupleEncounterAsync(session, token) ? (byte)1 : (byte)0;
            return;
        }
        if (!CoupleProtocol.IsNativeStartResponse(response)) return;
        var relation = await _database.GetActiveCoupleRelationAsync(session.Character.Id, token);
        var partner = relation is null ? null : FindNativeDungeonPartner(session, relation);
        lock (_nativeCoupleIdentityGate)
        {
            if (!session.NativeCoupleStartRequested || session.NativeCoupleIdentityPublished
                || session.NativeBattleEpoch != epoch) return;
            session.NativeCoupleIdentityPublished = true;
        }
        try
        {
            var identity = BuildNativeFrame(response, 0xC588,
                CoupleBenefitPolicy.BuildPartnerIdentity(partner?.Session.NativeCheckpoint is { } checkpoint
                    ? checked((ushort)checkpoint.Get(4)) : (ushort)0), session);
            await QueueOutboundWriteAsync(session, new OutboundNativeWrite(identity, "NativeDungeon",
                session.ListenerPort, session.RemoteIp ?? "local", true, false, "couple identity"), token);
        }
        catch
        {
            lock (_nativeCoupleIdentityGate)
                if (session.NativeBattleEpoch == epoch) session.NativeCoupleIdentityPublished = false;
            throw;
        }
    }
}
