using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private sealed record NativeContinuationReload(long Epoch, byte RealStage, byte ShowStage);

    private sealed class NativeContinuationRoom
    {
        public bool AwaitingBattleStart;
        public NativeContinuationReload? Reload;
    }

    private readonly ConditionalWeakTable<ConnectionSession, NativeContinuationRoom> _nativeContinuationRooms = new();

    private void ResetNativeDungeonContinuationRoom(ConnectionSession session)
    {
        var room = _nativeContinuationRooms.GetOrCreateValue(session);
        room.AwaitingBattleStart = false;
        room.Reload = null;
    }

    // Called only for a captured, authorized CF8B/CF8C pair. Do not thaw at
    // CF8C or CF71: late old-battle HP must remain frozen during the reload.
    private void ArmNativeDungeonContinuationReload(ConnectionSession session, ReadOnlySpan<byte> reset)
    {
        var room = _nativeContinuationRooms.GetOrCreateValue(session);
        room.AwaitingBattleStart = false;
        room.Reload = new(session.NativeBattleEpoch, reset[0x28], reset[0x29]);
    }

    private bool IsNativeDungeonContinuationProfileRequest(ConnectionSession session, ReadOnlySpan<byte> frame)
        => session.OnlineTracked && session.NativeDungeon is not null && session.Character is not null
            && !session.NativeDungeonDeathLatched && !session.NativeDungeonSettlementAwaitingAction
            && !session.NativeDungeonTownTransitionAuthorized
            && _nativeContinuationRooms.TryGetValue(session, out var room)
            && room.Reload is { } reload && reload.Epoch == session.NativeBattleEpoch
            && frame.Length == 12 && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) == 12
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) == 0xCFEB
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(8, 2)) == reload.RealStage
            && BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(10, 2)) == reload.ShowStage;

    private async Task HandleNativeDungeonContinuationProfileAsync(
        ConnectionSession session, byte[] request, CancellationToken token)
    {
        var epoch = session.NativeBattleEpoch;
        // Capture the response to this request, not an ambient CFEC. The worker
        // validates C587/CF70 and the pending reset tuple before answering.
        var exchange = await session.NativeDungeon!.ExchangeCapturedAsync(request, null, token);
        foreach (var response in exchange.Frames)
        {
            if (epoch == session.NativeBattleEpoch
                && IsNativeDungeonContinuationProfileRequest(session, request)
                && response.Length == 0x328
                && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(4, 2)) == response.Length
                && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6, 2)) == 0xCFEC
                && response[0x2DA] == request[8] && response[0x2DB] == request[10])
            {
                var cycle = GetNativeRevivalCycle(session);
                lock (cycle.BoundaryGate)
                {
                    if (cycle.BattleStartArmed && !cycle.BattleStarted)
                    {
                        cycle.BattleStartArmed = false;
                        cycle.BattleStarted = true;
                        cycle.FirstHansConsumed = false;
                        cycle.Transition = null;
                        session.NativeBattleResources = session.NativeBattleResources?.ForEpoch(epoch);
                        session.NativeDungeonDeathLatched = false;
                        ResetNativeDungeonContinuationRoom(session);
                        _log($"NativeDungeon continuation combat resources rearmed: character={session.Character!.Id} epoch={epoch} stage={request[8]} show={request[10]} via=CF8B/CF8C->CFEB/CFEC");
                    }
                }
            }
            // Preserve wire order: any D010 before the accepted CFEC is still
            // frozen. Only subsequent HP belongs to the resumed combat phase.
            await HandleNativeWorkerFrameAsync(session, response, epoch, token);
        }
    }

    // A reset stays in the battle controller until its own reload completes.
    // Do not append CF78: it enters the separate room controller prematurely.
    private void ArmNativeDungeonCombatResources(ConnectionSession session)
        => _nativeContinuationRooms.GetOrCreateValue(session).AwaitingBattleStart = true;

    private void ObserveNativeDungeonCombatStart(ConnectionSession session, ReadOnlySpan<byte> frame)
    {
        if (frame.Length != 8 || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2)) != 8
            || BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(6, 2)) != 0xCF80
            || !_nativeContinuationRooms.TryGetValue(session, out var room) || !room.AwaitingBattleStart)
            return;
        ResetNativeDungeonContinuationRoom(session);
        session.NativeBattleResources = session.NativeBattleResources?.ForEpoch(session.NativeBattleEpoch);
        session.NativeDungeonDeathLatched = false;
    }
}
