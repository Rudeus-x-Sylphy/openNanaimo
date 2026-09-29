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
    """Full 25-byte function guard; only three bytes actually differ."""
    return [('dungeon_score_exp_preview', SETTER_VA, SETTER_OLD, SETTER_NEW)]


def patch_experience_preview(data: bytes, patch_site) -> tuple[bytes, dict]:
    """Use the compatibility tool's PE-mapped, old/new-only site engine."""
    return patch_site(data, SETTER_VA, SETTER_OLD, SETTER_NEW,
                      'patch_dungeon_score_exp_preview',
                      'the score-derived EXP setter differs; review this client mapping')
