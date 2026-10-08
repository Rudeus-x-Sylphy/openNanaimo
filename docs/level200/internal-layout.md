# Level-200 internal bridge layout (little-endian)

The managed adapter and native worker use this versioned state contract.
Curve version 3: exact KR T[0..200]; level cap 200, total/bar cap T[200]. Pet EXP remains DWORD.

## F100 / F102 snapshot v4 (card inventory extension)

Payload length **5704**, full frame 5712. Old 5120/5124/5144-byte states are rejected
on the live wire. No implicit journal upgrade. Both peers must ship together.

| Payload offset | Width | Meaning |
|---|---:|---|
| 0 | 4 | success/schema discriminator: 4 (0 = rejected) |
| 4 | 4 | character UID, unchanged |
| 8 | 4 | level 1..200 |
| 12 | 4 | reserved, MUST be zero; no longer character EXP |
| 16..271 | unchanged | resources, identity, pet EXP at 156, skills and quick slots |
| 272..1951 | 420 x 4 | picture cards 13000001..13000420, including gold powder |
| 1952..5119 | unchanged | item count/rows, couple ring, handles, grade and clear masks |
| 5120 | 4 | couple partner UID, unchanged |
| 5124 | 4 | schema version, exactly 4 |
| 5128 | 4 | payload length, exactly 5704 |
| 5132 | 4 | curve version, exactly 3 |
| 5136 | 8 | real total character EXP, unsigned LE, limited to T[200] |
| 5144..5223 | 20 x 4 | SP cards 12000001..12000020 |
| 5224..5623 | 100 x 4 | event cards 50000001..50000100 |
| 5624..5703 | 20 x 4 | special/VIP cards 22000001..22000020 (lucky A-H are 11..18) |

The 560 quantities are byte-bounded DWORDs. Extended albums MUST NOT be read or
written at 272 + index*4 for index >=420; that collides with the item count/array.
Curve version remains **3**. F10B remains v3/48; this is not another EXP migration.
Startup upgrades validated inactive v3 recovery profiles in one SQL transaction,
archives exact old bytes/SHA-256 in NativeCardProfileArchives, and seeds extension
counts from CharacterCards only. Never recover cards from an old polluted worker
sidecar. Current profiles are idempotent. Pending old reward journals are NOT
implicitly upgraded; recover them with the matching old pair before switching.

Old workers reject both the length and discriminator; no low-DWORD compatibility.
Constructor verifies version/length/curve before interpreting authoritative state.

## F10B live authoritative progression v3

Payload length **48**, full frame 56. Old 32-byte payloads are rejected.

| Payload offset | Width | Meaning |
|---|---:|---|
| 0 | 4 | version = 3 |
| 4 | 4 | payload length = 48 |
| 8 | 4 | curve version = 3 |
| 12 | 4 | UID |
| 16 | 4 | level |
| 20 | 8 | real total EXP |
| 28 | 4 | base maximum HP |
| 32 | 4 | base maximum MP |
| 36 | 4 | attack |
| 40 | 4 | defense |
| 44 | 4 | battle epoch |

Both internal formats carry authoritative total EXP. C355, CF71,
C57C and CF88 retain their retail lengths; their character EXP is projected.
