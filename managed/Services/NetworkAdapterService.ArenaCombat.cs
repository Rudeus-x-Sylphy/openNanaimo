using System.Buffers.Binary;

namespace OpenNanaimo.Adapter.Services;

public sealed partial class NetworkAdapterService
{
    private byte[]? HandleArenaPvpEvent(byte[] frame, byte[] payload, ConnectionSession victim)
    {
        if (payload.Length != 8 || !victim.OnlineTracked || !victim.AuxiliaryGameSession
            || victim.Character is null || victim.ArenaGameType != 4) return null;
        var kind = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (kind is not (10 or 20 or 30 or 40 or 50 or 60 or 80)) return null;
        byte[] response;
        lock (_arenaRoomGate)
        {
            if (!_arenaRooms.TryGetValue(victim.ArenaRoomId, out var room) || !room.Started
                || room.EndingSessionIds.Count != 0 || room.EliminationWinnerSessionId is not null
                || !room.Members.ContainsKey(victim.SessionId)
                || !room.CurrentHpBySession.TryGetValue(victim.SessionId, out var hp) || hp == 0) return null;
            response = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(response, kind);
            response[7] = victim.ArenaSlotIndex;
            if (kind is 10 or 20 or 30 or 60)
            {
                // The collision reporter is the victim; the final request word identifies the projectile owner.
                var attackerUid = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(6));
                var attacker = room.Members.Values.FirstOrDefault(member => member.Character is not null
                    && GetSceneEntityId(member.Character) == attackerUid);
                if (attacker is null || ReferenceEquals(attacker, victim)
                    || room.CurrentHpBySession.GetValueOrDefault(attacker.SessionId) == 0
                    || (victim.ArenaTeamCode is 1 or 2 && attacker.ArenaTeamCode == victim.ArenaTeamCode)) return null;
                var objectId = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2));
                var hitKey = (victim.SessionId, attacker.SessionId, kind, objectId);
                var now = Environment.TickCount64;
                if (room.PvpHitTicks.TryGetValue(hitKey, out var last) && now - last < 100) return null;
                if (room.PvpHitTicks.Count >= 4096)
                    foreach (var key in room.PvpHitTicks.Where(pair => now - pair.Value >= 100).Select(pair => pair.Key).ToArray())
                        room.PvpHitTicks.Remove(key);
                if (room.PvpHitTicks.Count >= 4096 && !room.PvpHitTicks.ContainsKey(hitKey)) return null;
                room.PvpHitTicks[hitKey] = now;
                // Local combat policy: use the same bounded stat calculation for the first and every subsequent hit.
                var attackCharacter = attacker.Character!;
                var petAttack = PetProgression.GetAttackProfile(PetProgression.GetState(
                    attackCharacter, GetEquippedPetItemCode(attackCharacter))).Default;
                var attack = (int)Math.Min(int.MaxValue,
                    (long)Math.Max(attackCharacter.Attack, petAttack) + attackCharacter.AttackModifier);
                var defense = (int)Math.Min(int.MaxValue, (long)victim.Character.Defense + victim.Character.DefenseFlat);
                var damage = ArenaProtocol.CalculatePvpDamage(attack, defense);
                hp = (ushort)Math.Max(0, hp - damage);
                room.CurrentHpBySession[victim.SessionId] = hp;
                payload.AsSpan(2, 4).CopyTo(response.AsSpan(2));
                response[6] = attacker.ArenaSlotIndex;
                BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(8), hp);
                response[10] = hp == 0 ? (byte)200 : (byte)100;
                if (hp == 0)
                {
                    var living = room.Members.Values.Where(member => room.CurrentHpBySession.GetValueOrDefault(member.SessionId) > 0).ToArray();
                    if (living.All(member => member.SessionId == attacker.SessionId
                        || (attacker.ArenaTeamCode is 1 or 2 && member.ArenaTeamCode == attacker.ArenaTeamCode)))
                        room.EliminationWinnerSessionId = attacker.SessionId;
                }
            }
            QueueArenaBroadcast(victim, 0xD015, response, false, "arena player event");
        }
        return BuildNativeFrame(frame, 0xD015, response, victim);
    }
}
