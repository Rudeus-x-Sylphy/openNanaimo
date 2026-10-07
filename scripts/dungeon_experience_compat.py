"""Disable score-derived provisional EXP while preserving live score and CF88 rewards.

The client refreshes three scoreboard slots, then derives a temporary EXP bonus
for the local slot in 748C90. 401005 -> 74C190 writes global+FD0; 8E1620 adds
that preview to committed global+FCC. D00E/D012/D010 remain live-score inputs.

The guarded setter is thiscall/ret4. Thunk401005 calls it from 748F28 and 748F37
in the score refresh. Zeroing the volatile ECX input preserves the stack, return
value, stored score, committed EXP, CF88 rewards, and the function layout.
"""
from __future__ import annotations

SETTER_VA = 0x0074C190
PATCH_VA = 0x0074C19A
SETTER_OLD = bytes.fromhex('558BEC51894DFC8B45FC8B4D088988D00F00008BE55DC20400')
SETTER_NEW = SETTER_OLD[:10] + bytes.fromhex('31C990') + SETTER_OLD[13:]


def patch_sites():
    """Exact guards for the experience-preview and local actor-level sites."""
    return [('dungeon_score_exp_preview', SETTER_VA, SETTER_OLD, SETTER_NEW),
            ('battle_local_level', LOCAL_LEVEL_VA, LOCAL_LEVEL_OLD, LOCAL_LEVEL_NEW)]


def patch_experience_preview(data: bytes, patch_site) -> tuple[bytes, dict]:
    """Use the compatibility tool's PE-mapped, old/new-only site engine."""
    return patch_site(data, SETTER_VA, SETTER_OLD, SETTER_NEW,
                      'patch_dungeon_score_exp_preview',
                      'the score-derived EXP setter differs; review this client mapping')

# C60D's remote-list loop writes actor+7BAC, but its local fallback only
# fetches the actor pointer. Preserve that loop and room-list updates; cache
# the local pointer, validate its UID, then reuse the existing level setter.
# Reuse the existing code region and preserve actor/PET identity and resources.
LOCAL_LEVEL_VA = 0x00701BB4
LOCAL_LEVEL_OLD = bytes.fromhex('837dfc00752de8955dd1ff8bc8e8f75dd1ff8bc8e81c06d0ff8b4d080fb751083bc2750fe8775dd1ff8bc8e8d95dd1ff8945fc')
LOCAL_LEVEL_NEW = bytes.fromhex('837dfc00752de8955dd1ff8bc8e8f75dd1ff85c0741d8945fc8bc8e81506d0ff8b55080fb752083bc27508ebb3909090909090')

def patch_local_actor_level(data: bytes, patch_site) -> tuple[bytes, dict]:
    return patch_site(data, LOCAL_LEVEL_VA, LOCAL_LEVEL_OLD, LOCAL_LEVEL_NEW,
                      'patch_battle_local_level',
                      'the C60D local actor fallback differs; review this client mapping')
