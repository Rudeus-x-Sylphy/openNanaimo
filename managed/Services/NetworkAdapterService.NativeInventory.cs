using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private static bool SuppressInventoryActorRebuild(ConnectionSession session)
        => session.DungeonRoomId != 0 || session.NativeDungeon is not null
            || session.ApartmentOwnerCharacterId > 0;

    private static bool IsNativeReadyRoomInventory(ConnectionSession session)
        => session.NativeDungeon is not null && session.NativeDungeonSelectionValid
            && !session.NativeCoupleStartRequested && !session.NativeDungeonSettlementAwaitingAction
            && !session.NativeDungeonNextTransitionAuthorized && !session.NativeDungeonTownTransitionAuthorized;

    internal static bool IsNativeInventoryMutation(ushort opcode)
        => opcode is 0xC47D or 0xC44D or 0xC44F or 0xC433 or 0xC43D or 0xC46D
            or 0xC3CF or 0xC475 or 0xC453;

    private async Task<byte[]?> HandleNativeFrameAsync(
        byte[] frame, ushort opcode, string channel, string remote, string? remoteIp,
        ConnectionSession session, CancellationToken token)
    {
        if (channel == "WorldAdapter" && session.OnlineTracked && session.Character is { } noticeCharacter
            && session.CardNoticeCharacterId != noticeCharacter.Id)
        {
            var initialCards = await _database.GetCharacterCardsAsync(noticeCharacter.Id, token);
            session.InventoryAcquisitions.Seed(0xC3E8, CardNoticeCodes(initialCards));
            session.CardNoticeCharacterId = noticeCharacter.Id;
        }
        var synchronize = NativeDungeonEnabled && channel == "WorldAdapter"
            && session.OnlineTracked && session.Character is not null
            && session.NativeDungeon is not null && session.NativeCheckpoint is not null
            && (IsNativeInventoryMutation(opcode) || opcode is 0xC378 or 0xC42F or 0xC44B or 0xC3CB or 0xC3E7);
        if (synchronize)
            await CommitNativeCheckpointAsync(session, null, token);
        var response = await HandleNativeFrameCoreAsync(frame, opcode, channel, remote, remoteIp, session, token);
        if (synchronize && response is not null && IsNativeInventoryMutation(opcode))
            response = CombineNativeFrames(response, await SynchronizeNativeInventoryAsync(session, token));
        return response;
    }

    private async Task<byte[]> SynchronizeNativeInventoryAsync(ConnectionSession session, CancellationToken token)
    {
        if (session.NativeDungeon is not { } worker || session.Character is not { } character
            || session.NativeCheckpoint is not { } previous) return [];
        var epoch = session.NativeBattleEpoch;
        var cards = await _database.GetCharacterCardsAsync(character.Id, token);
        var skills = await _database.GetCharacterSkillsAsync(character.Id, token);
        var state = NativeDungeonState.Create(character, cards, skills);
        await _database.RestoreNativeDungeonProgressAsync(character.Id, state, token);
        var resources = ResolveInventoryVitals(character, session.NativeBattleResources);
        resources.ApplyTo(state);
        CoupleBenefitPolicy.WriteNativeRing(state, previous.Get(CoupleBenefitPolicy.NativeRingOffset));
        CoupleBenefitPolicy.WriteNativePartner(state, checked((ushort)previous.Get(NativeDungeonState.CouplePartnerUidOffset)));
        var exchange = await worker.ExchangeCapturedAsync(null, state, token);
        if (!ReferenceEquals(session.NativeDungeon, worker) || session.NativeBattleEpoch != epoch) return [];
        session.NativeCheckpoint = exchange.State;
        session.NativeBattleResources = resources;
        foreach (var response in exchange.Frames)
            await HandleNativeWorkerFrameAsync(session, response, epoch, token);
        if (!IsNativeReadyRoomInventory(session)) return [];
        // Refresh from the imported worker state through its PET lifecycle.
        // The caller publishes inventory completion before this room snapshot.
        var refresh = await worker.ExchangeCapturedAsync(NativeDungeonClient.Frame(0xF108, []), null, token);
        if (!ReferenceEquals(session.NativeDungeon, worker) || session.NativeBattleEpoch != epoch) return [];
        var frames = new List<byte[]>();
        foreach (var response in refresh.Frames)
        {
            if (response.Length == 0x74 && BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(6)) == 0xCF72)
                frames.Add(BuildNativeFrame(response, 0xCF72, response[8..], session));
            else
                await HandleNativeWorkerFrameAsync(session, response, epoch, token);
        }
        return CombineNativeFrames(frames.ToArray());
    }
}
