using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private sealed class LiveExperienceEpoch { internal uint NativeEpoch; internal long ManagedEpoch; }
    private static readonly ConditionalWeakTable<ConnectionSession, LiveExperienceEpoch> LiveExperienceEpochs = new();

    private async Task ApplyNativeLiveExperienceAsync(ConnectionSession session,
        byte[] frame, long epoch, CancellationToken token)
    {
        if (frame.Length != 20 || BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(4)) != 20
            || session.Character is null || session.NativeCheckpoint is null
            || session.NativeDungeon is not { } worker || !session.OnlineTracked
            || BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)) != session.NativeCheckpoint.Get(4)) return;
        var nativeEpoch = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12));
        var score = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(16));
        using var lease = await LockNativeDungeonResourcesAsync(session, token);
        if (session.NativeBattleEpoch != epoch || !ReferenceEquals(session.NativeDungeon, worker)) return;
        var result = await _database.ApplyLiveDungeonExperienceAsync(session.AccountId,
            session.Character.Id, session.SessionId, $"{epoch}:{nativeEpoch}", score, token);
        if (!result.Authorized) return;
        var liveEpoch = LiveExperienceEpochs.GetOrCreateValue(session);
        liveEpoch.NativeEpoch = nativeEpoch; liveEpoch.ManagedEpoch = epoch;
        await RefreshSessionCharacterAsync(session, token);
        await SynchronizeNativeExperienceAsync(session, nativeEpoch, token);
        _log($"NativeDungeon kill EXP: character={session.Character!.Id} epoch={epoch}/{nativeEpoch} score={score} added={result.AddedExperience} total={session.Character.Experience} level={session.Character.Level}");
    }

    private async Task SynchronizeNativeExperienceAsync(ConnectionSession session, uint nativeEpoch, CancellationToken token)
    {
        if (session.NativeDungeon is not { } worker || session.NativeCheckpoint is null || session.Character is null) return;
        var character = session.Character!;
        var next = new NativeDungeonState(session.NativeCheckpoint.Bytes.ToArray());
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(next.Bytes.AsSpan(offset), value);
        next.SetProgression(character.Level, character.Experience);
        Put(16, (uint)character.MaxHp); Put(24, (uint)character.MaxMp);
        var attack = CharacterCombatProgression.NativeAttack(character.Level, character.AttackModifier);
        var defense = CharacterCombatProgression.NativeDefense(character.Level, character.DefenseFlat);
        Put(NativeDungeonState.AttackModifierOffset, attack); Put(NativeDungeonState.DefenseFlatOffset, defense);
        if (session.NativeBattleResources is { } resources)
        {
            var maximums = ResolveInventoryVitals(character);
            // Update maxima now; the worker owns current HP/MP and applies a one-shot
            // refill on a live level crossing. Its CF72 is observed before projection.
            // Never pre-heal from a potentially stale DB/checkpoint or reset Power.
            session.NativeBattleResources = resources with
            { MaximumHp = maximums.MaximumHp, MaximumMp = maximums.MaximumMp };
            session.NativeBattleResources.ApplyTo(next);
        }
        session.NativeCheckpoint = next;
        var payload = new byte[48];
        void Field(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset), value);
        Field(0, NativeDungeonState.ProtocolVersion); Field(4, 48); Field(8, CharacterProgression.CurveVersion);
        Field(12, next.Get(4)); Field(16, checked((uint)character.Level));
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(20), checked((ulong)character.Experience));
        Field(28, (uint)character.MaxHp); Field(32, (uint)character.MaxMp);
        Field(36, attack); Field(40, defense); Field(44, nativeEpoch);
        // Also resynchronize on duplicate receipts: a prior send may have failed
        // after the DB transaction committed. It must never grant EXP twice.
        await worker.SendControlAsync(NativeDungeonClient.Frame(0xF10B, payload), token);
    }
}
