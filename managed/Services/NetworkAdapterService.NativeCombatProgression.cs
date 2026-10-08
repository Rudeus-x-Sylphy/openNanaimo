using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private async Task RefreshNativeCombatProgressionAsync(ConnectionSession session, CancellationToken token)
    {
        if (session.Character is not { } character || session.NativeDungeon is not { } worker
            || session.NativeCheckpoint is not { } before) return;
        var attack = CharacterCombatProgression.NativeAttack(character.Level, character.AttackModifier);
        var defense = CharacterCombatProgression.NativeDefense(character.Level, character.DefenseFlat);
        if (before.Get(NativeDungeonState.AttackModifierOffset) == attack
            && before.Get(NativeDungeonState.DefenseFlatOffset) == defense) return;
        var epoch = session.NativeBattleEpoch;
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, before.Get(4));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), attack);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), defense);
        var exchange = await worker.ExchangeCapturedAsync(NativeDungeonClient.Frame(0xF107, payload), null, token);
        if (!ReferenceEquals(session.NativeDungeon, worker) || session.NativeBattleEpoch != epoch) return;
        if (exchange.State.Get(4) != before.Get(4)
            || exchange.State.Get(NativeDungeonState.AttackModifierOffset) != attack
            || exchange.State.Get(NativeDungeonState.DefenseFlatOffset) != defense)
            throw new InvalidDataException("Native combat progression update was not accepted.");
        foreach (var response in exchange.Frames)
            await HandleNativeWorkerFrameAsync(session, response, epoch, token);
        using var resourceCommit = await LockNativeDungeonResourcesAsync(session, token);
        if (!ReferenceEquals(session.NativeDungeon, worker) || session.NativeBattleEpoch != epoch
            || session.NativeCheckpoint is not { } current || current.Get(4) != before.Get(4)) return;
        var updated = new NativeDungeonState(current.Bytes.ToArray());
        BinaryPrimitives.WriteUInt32LittleEndian(updated.Bytes.AsSpan(NativeDungeonState.AttackModifierOffset), attack);
        BinaryPrimitives.WriteUInt32LittleEndian(updated.Bytes.AsSpan(NativeDungeonState.DefenseFlatOffset), defense);
        session.NativeCheckpoint = updated;
    }
}
