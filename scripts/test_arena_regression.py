from pathlib import Path
import re
root=Path(__file__).resolve().parents[1]
protocol=(root/'managed/Services/ArenaProtocol.cs').read_text(encoding='utf-8')
service=(root/'managed/Services/NetworkAdapterService.cs').read_text(encoding='utf-8')
combat=(root/'managed/Services/NetworkAdapterService.ArenaCombat.cs').read_text(encoding='utf-8-sig')
checks=[
 ('complete D010 length', 'public const int GameEventResponseLength = 36;' in protocol),
 ('bounded player damage policy', 'CalculatePvpDamage' in protocol),
 ('initialized D010 tail', 'payload.AsSpan(28).Clear();' in protocol),
 ('target HP ledger', 'room.CurrentHpBySession[victim.SessionId] = hp;' in combat),
 ('elimination winner ledger', 'room.EliminationWinnerSessionId = attacker.SessionId;' in combat),
 ('score fallback', 'if (scoreDelta == 0)\n                    scoreDelta = 10;' in service),
 ('target identity validation', 'GetSceneEntityId(member.Character) == attackerUid' in combat),
 ('round reset clears winner', 'room.EliminationWinnerSessionId = null;' in service),
]
for name,ok in checks:
    if not ok: raise SystemExit('ARENA_STATIC_REGRESSION_FAIL '+name)
    print('ARENA_STATIC_REGRESSION_PASS '+name)
print('ARENA_STATIC_REGRESSION_PASS all')
