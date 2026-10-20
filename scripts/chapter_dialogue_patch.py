"""Compile the chapter dialogue window and state transitions.

The 0x400-byte window uses constructor 0x4064FB, kind 9, vtable 0x6E4000,
panel binding 0x4163BF, setup 0xA12CC0 and registry insertion 0x410299.
Each page contains four 51-byte rows. Chapter, phase and segment identities
select the complete speaker name, portrait and text for the active window."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import struct
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import monk_dialogue_compat as monk  # noqa: E402
import story_qt_patch as shared  # noqa: E402

SCHEMA = 'openNanaimo.chapter-dialogue-payload.v2'
CAVE, SPAN = 0x48BDBC, 0x10D00
UPDATE, PAGE = CAVE, CAVE + 0x500
GUARD = CAVE + 0x660
SAVED_ESP, ARMED, LAST = CAVE + 0x668, CAVE + 0x670, CAVE + 0x674
MARK = CAVE + 0x678              # live breadcrumb: last stage the payload reached
OUR_WINDOW = CAVE + 0x67C        # window object we created
OWNER = CAVE + 0x680             # data record the window reads pages from
PROBE = CAVE + 0x600             # diagnostic snapshot; sits in the retired vtable slot
PROBE_SPAN = 0x40
PROBE_OFFSETS = (0x00, 0x04, 0x08, 0x0C, 0x10, 0x24)
GUIDE_FIELD = 0x334              # village + 0x334 = the guide owner the panel binds to
# The move handler exposes the page identifier at this entry site.
PAGE_SLOT = CAVE + 0x3C0         # where the capture stub stores the page
STUB = CAVE + 0x380              # capture trampoline for the town-page handler
LATCH = CAVE + 0x3C4             # reserved latch byte (kept zeroed)
# The C59C handler creates and registers the task window when it does not exist
# yet; that is what pops the panel for an unsolicited list, but it is also how
# the player's own task book is opened, so the create path cannot be removed.
# The packet padding marker distinguishes the pushed list from a manual reply.
TASK_WINDOW_SITE = 0x4D605B
TASK_WINDOW_ORIGINAL = bytes.fromhex('6848020000')
TASK_WINDOW_CREATE_RESUME = 0x4D6060   # after the replaced "push 0x248"
TASK_WINDOW_EPILOGUE = 0x4D60C1        # skip the create block entirely
# The pushed list carries a marker in the unused padding of the fixed type-2
# record (payload+220 = frame+228). A reply to the player's own C59B leaves it
# zero, so the create path can tell "our push" from "the player opened the book"
# without any timing window.
TASK_MARKER_OFFSET = 228
CREATE_STUB = CAVE + 0x400        # free gap between the update block and PAGE
CLAIM_STUB = CAVE + 0x4B0        # free gap after phase slots, before PAGE
TASK_CLAIM_SITE, TASK_CLAIM_EXIT = 0xA5FD76, 0xA5FE1B
TASK_CLAIM_ORIGINAL = bytes.fromhex('6a008b55c8')
TASK_CLAIM_SEND = 0x402E00
PREVIOUS_PAYLOAD_SHA256 = 'C770739DE544400ECAE24BD05B77AAE931AF84B594DC50371E48C6C80CFB9DEA'
SUPPRESS = CAVE + 0x3C5
SUPPRESS_FRAMES = 8                    # ~0.13 s at 60 fps; the pushed list rides the
                                       # same TCP segment as the room entry, so it lands
                                       # within a frame or two of the arming hook
ROOM_PAGE = 31                         # 1村地宫1 = TownPage 31
# Instrumentation: frame counters prove how far apart the arming hook and the
# task-window gate actually run, instead of guessing the delay.
COUNTER = CAVE + 0x3C8
ARM_FRAME = CAVE + 0x3CC
GATE_FRAME = CAVE + 0x3D0
GATE_FLAG = CAVE + 0x3D4
PAGE_HOOK_SITE = 0x4FF4CC        # push 0xc38508 ("TownPage : %d")
PAGE_HOOK_LENGTH = 5
PAGE_HOOK_ORIGINAL = bytes.fromhex('680885c300')
PAGE_HOOK_TARGET = 0x4FF4D1      # first instruction after the replaced push
PAGE_LOG_FORMAT = 0xC38508
PORTRAIT = CAVE + 0x6C0          # portrait loader (own copy of the class's)
PORTRAIT_PATH = CAVE + 0x720     # ASCII 'interface\NPC_<id>_0.eff'
NAME = CAVE + 0x760              # GBK display name, including NUL padding
NAME_BYTES, PANEL_NAME_BYTES = 24, 0x28
TABLE, STRINGS, TEXT = CAVE + 0x780, CAVE + 0x2280, CAVE + 0x2B00
SELECT = CAVE + 0x2980            # lifecycle/segment selection, outside string data
UPDATE_LIMIT, PAGE_LIMIT = 0x380, 0x100
LAST_QUEST, ACTIVE_QUEST, ACTIVE_PHASE = CAVE + 0x498, CAVE + 0x49C, CAVE + 0x4A0
OWNER_PAGES, OWNER_COUNT = 0x18, 0x1C
ENTRY_SIZE, MAX_ENTRIES = 64, 107
ROW_BYTES, ROWS_PER_PAGE = 51, 4
ENTRY_COUNT = CAVE + 0x480      # how many (chapter, phase) rows the table holds
QUEST_SLOT = CAVE + 0x484       # chapter the lookup matched, for the probe
QUEST_COUNT = CAVE + 0x488      # how many chapters the lookup should try
PUSH_FLAG = CAVE + 0x490         # 1 when the last task list arrived as our push
SEG = CAVE + 0x494               # which speech of the current phase is showing
RECORD = CAVE + 0x48C           # the task record the lookup returned
FIRST_QUEST = 73_000_000
HOOK_SITE = 0x527CE0             # sibling call right after the village dialogue hook
HOOK_CHAIN = 0x40ED77            # the native call we tail-jump into
HOOK_ORIGINAL = b'\xE8' + struct.pack('<i', HOOK_CHAIN - HOOK_SITE - 5)
TASK_MANAGER = 0xD7330C          # global the slot handler uses
TASK_LOOKUP = 0x41BAE0           # std::map::find thunk -> record pointer
RECORD_OBJECTIVE = 8             # Progress3; State3 at +4 is not the phase
TAG = b'CDL1'                    # idempotency marker at the cave tail
MARKER = 0x4D000000
STAGE_ENTER, STAGE_FOUND, STAGE_MATCH = 1, 2, 3
STAGE_ALLOC, STAGE_OBJECT, STAGE_INIT = 4, 5, 6
STAGE_PAGE, STAGE_DONE = 7, 0xFF
ROOT = Path(__file__).resolve().parents[1]
TRIGGERS_FILE = ROOT / 'manifest/chapter_dialogue_triggers.json'

# --- the dialogue window class the deployed client already carries -----------
GUIDE_VTABLE = 0x6E4000          # four-village dialogue class (slot 2 = DIALOGUE)
WINDOW_ALLOC = 0xB479CC          # __cdecl allocator
WINDOW_SIZE = 0x400              # 0x144 is the portrait *image*, not the window
WINDOW_CTOR = 0x4064FB           # base window constructor
WINDOW_INIT = 0x41759E           # kind setter, writes the byte at +4
WINDOW_KIND = 9
PANEL_BIND = 0x4163BF            # panel +0x200: bind to the village UI owner
PANEL_SETUP = 0xA12CC0           # panel +0x200: create the frame/buttons
IMAGE_SIZE = 0x144               # portrait image object
IMAGE_CTOR = 0x41BE00
IMAGE_LOAD = 0x404232            # load by path, arguments as the class passes them
REGISTRY = 0x406F82              # window registry singleton getter
REGISTER_INSERT = 0x410299       # registry insert; without it the window never draws
PORTRAIT_INDEX = 0               # index our loader uses for its own path table


def default_texts(root: Path = None) -> dict[int, str]:
    """Chinese chapter speeches keyed by QS id."""
    base = Path(root) if root else ROOT
    data = json.loads((base / 'manifest/story_localization.json').read_text('utf-8'))
    return {row['qs_id']: row['translation_zh'].replace('%s', '收集者').replace('%%', '%')
            for row in data['texts']}


def default_triggers(path: Path = None) -> list[dict]:
    """Trigger table: which (chapter, phase) opens which speech."""
    source = Path(path) if path else TRIGGERS_FILE
    shared.require(source.is_file(), 'Missing chapter dialogue trigger table: ' + str(source))
    data = json.loads(source.read_text('utf-8'))
    shared.require(data.get('schema') == 'openNanaimo.chapter-dialogue-triggers.v1',
                   'Invalid chapter dialogue trigger schema')
    return list(data['triggers'])


def pages(text: str, tail: str = '') -> list[bytes]:
    """Split one speech into pages of four 51-byte GBK rows."""
    lines = []
    for paragraph in text.split('^&'):
        line = ''
        for char in paragraph:
            if len((line + char).encode('gbk')) >= ROW_BYTES:
                lines.append(line)
                line = ''
            line += char
        if line:
            lines.append(line)
    if tail:
        shared.require(len(tail.encode('gbk')) < ROW_BYTES, 'Dialogue tail exceeds a row')
        lines.append(tail)
    return [b''.join(row.encode('gbk').ljust(ROW_BYTES, b'\0')
                     for row in (lines[index:index + ROWS_PER_PAGE] + [''] * ROWS_PER_PAGE)[:ROWS_PER_PAGE])
            for index in range(0, len(lines), ROWS_PER_PAGE)]


def entries(texts: dict[int, str], triggers: list[dict]) -> list[dict]:
    """One row per speech so each one keeps its own speaker and portrait."""
    rows = []
    for trigger in sorted(triggers, key=lambda item: (item['episode'], item['objective'],
                                                      item.get('speech_index', 0))):
        shared.require(trigger['objective'] in (0, 1, 2), 'bad dialogue phase')
        text = texts.get(trigger['qs_id'])
        shared.require(text is not None, 'Missing chapter speech %d' % trigger['qs_id'])
        rendered = pages(text, trigger.get('tail', ''))
        shared.require(bool(rendered), 'Empty chapter speech')
        rows.append(dict(village=trigger['village'], page=trigger['page'],
                         x=trigger['x'], y=trigger['y'], qs_id=trigger['qs_id'],
                         episode=trigger['episode'],
                         objective=0 if trigger['objective'] == 0 else 1,
                         npc=int(trigger['npc_uid']), speech=int(trigger.get('speech_index', 0)),
                         pages=len(rendered), quest=FIRST_QUEST + trigger['episode'] - 1,
                         data=b''.join(rendered)))
        shared.require(len(rows) <= MAX_ENTRIES, 'Chapter trigger table is full')
    shared.require(bool(rows), 'Empty chapter trigger table')
    expected = {}
    for row in rows:
        key = row['quest'], row['objective']
        shared.require(row['speech'] == expected.get(key, 0),
                       'Chapter speech indices must be contiguous from zero')
        expected[key] = row['speech'] + 1
    return rows


def chapter_speaker() -> tuple[int, str]:
    """Speaker of the first chapter's entrance speech, straight from the data."""
    chapters = json.loads((ROOT / 'release/components/story_chapters/chapters.json').read_text('utf-8'))
    rows = chapters if isinstance(chapters, list) else chapters.get('chapters', [])
    chapter = next(c for c in rows if c.get('quest_id') == FIRST_QUEST)
    entrance = next(d for d in chapter['dialogue'] if d['objective_index'] == 0)
    npc = int(entrance['speeches'][0]['npc_uid'])
    return npc, speaker_names()[npc]


def speaker_names() -> dict[int, str]:
    data = json.loads((ROOT / 'manifest/story_localization.json').read_text('utf-8'))
    return {int(npc): name for npc, name in data['speaker_names'].items()}


def encoded_name(name: str) -> bytes:
    raw = name.encode('gbk')
    shared.require(b'\0' not in raw and 0 < len(raw) < NAME_BYTES,
                   'Chapter speaker name exceeds its terminated cave slot')
    return raw.ljust(NAME_BYTES, b'\0')


def portrait_path(npc: int) -> bytes:
    """Portrait resource name; the CN files are not zero padded (NPC_52_0.eff)."""
    return ('interface\\NPC_%d_0.eff' % npc).encode('ascii') + b'\0'


def portrait_routine(code) -> None:
    """Load the portrait the same way the dialogue class does.

    0x739080 does exactly this, but it resolves the path through a table that
    only holds the four village guides (NPC 62..65). We keep our own path so the
    chapter speaker's portrait loads without touching that table.
    """
    code.emit('56')                                      # push esi
    code.emit('57')                                      # push edi
    code.emit('8BF1')                                    # mov esi, ecx (panel)
    code.emit('68'); code.u32(IMAGE_SIZE); code.call(WINDOW_ALLOC)
    code.emit('83C404')
    code.emit('85C0'); code.branch('0F84', 'pt_done')
    code.emit('8BC8'); code.call(IMAGE_CTOR)             # mov ecx,eax; ctor
    code.emit('8986F4000000')                            # mov [esi+0xF4], eax
    code.emit('6A00')                                    # push 0
    code.emit('FFB630010000')                            # push [esi+0x130]
    code.emit('FFB62C010000')                            # push [esi+0x12C]
    code.emit('6A00')                                    # push 0
    code.emit('68'); code.u32(PORTRAIT_PATH)             # push path
    code.emit('8BC8')                                    # mov ecx, eax
    code.call(IMAGE_LOAD)
    code.label('pt_done')
    code.emit('5F')                                      # pop edi
    code.emit('5E')                                      # pop esi
    code.emit('C3')                                      # ret


def name_write(code) -> None:
    """Native A12610/A12EC0 own a 0x28-byte name, before text at +0x228."""
    for offset in range(0, NAME_BYTES, 4):
        code.emit('A1'); code.u32(NAME + offset)
        code.emit('8987'); code.u32(0x200 + offset)


def probe_snapshot(code) -> None:
    """Record the village and guide objects so the room page can be identified.

    The trigger currently rides the client's task record, which only refreshes
    when the task book opens. A page-based trigger needs one stable field; this
    snapshot is how that field gets found: compare the slots in the village with
    the ones in room 31 and keep the one that reads 31.
    """
    code.emit('8B8578FDFFFF')                            # mov eax,[ebp-0x288] (village)
    code.emit('A3'); code.u32(PROBE)                     # mov [PROBE], eax
    code.emit('85C0'); code.branch('0F84', 'probe_done')
    for index, offset in enumerate(PROBE_OFFSETS):
        code.emit('8B88'); code.u32(offset)              # mov ecx,[eax+offset]
        code.emit('890D'); code.u32(PROBE + 4 + index * 4)
    code.emit('8B80'); code.u32(GUIDE_FIELD)             # mov eax,[eax+0x334] (guide owner)
    code.emit('A3'); code.u32(PROBE + 0x20)
    code.emit('85C0'); code.branch('0F84', 'probe_done')
    for index, offset in enumerate(PROBE_OFFSETS):
        code.emit('8B88'); code.u32(offset)
        code.emit('890D'); code.u32(PROBE + 0x24 + index * 4)
    code.label('probe_done')


REGIONS = (
    ('update', UPDATE, UPDATE_LIMIT),
    ('page', PAGE, PAGE_LIMIT),
    ('probe', PROBE, PROBE_SPAN),
    ('owner', OWNER, 0x40),
    ('guard', GUARD, 8),
    ('saved_esp', SAVED_ESP, 8),
    ('armed', ARMED, 12),          # armed + last + mark
    ('window', OUR_WINDOW, 4),
    ('portrait', PORTRAIT, 0x60),
    ('path', PORTRAIT_PATH, 64),
    ('name', NAME, NAME_BYTES),
    ('table', TABLE, ENTRY_SIZE * MAX_ENTRIES),
    ('strings', STRINGS, SELECT - STRINGS),
    ('selector', SELECT, TEXT - SELECT),
    ('entry_slots', ENTRY_COUNT, 24),   # counts, matched quest, record, push flag, speech
    ('phase_slots', LAST_QUEST, 12),
    ('claim_stub', CLAIM_STUB, PAGE - CLAIM_STUB),
    ('create_stub', CREATE_STUB, 0x40),
    ('page_stub', STUB, PAGE_SLOT - STUB),
    ('page_state', PAGE_SLOT, GATE_FLAG + 4 - PAGE_SLOT),
    ('text', TEXT, CAVE + SPAN - len(TAG) - TEXT),
    ('tag', CAVE + SPAN - len(TAG), len(TAG)),
)


def require_regions() -> None:
    """Fail the build if two cave regions overlap.

    Overlapping a state slot with a diagnostic buffer silently corrupts the
    saved stack pointer and crashes the client on the first village frame, so
    the layout is checked before anything is emitted.
    """
    spans = sorted(REGIONS, key=lambda row: row[1])
    for name, start, limit in spans:
        shared.require(limit > 0 and CAVE <= start < start + limit <= CAVE + SPAN,
                       'Invalid cave region: ' + name)
    for (name_a, start_a, limit_a), (name_b, start_b, limit_b) in zip(spans, spans[1:]):
        if start_a + limit_a > start_b:
            raise ValueError('cave regions overlap: %s [%#x..%#x) and %s at %#x'
                             % (name_a, start_a, start_a + limit_a, name_b, start_b))


def claim_stub(code) -> None:
    # C59C can remove the clicked quest before a queued second click. Resolve
    # once, reject null, and preserve the original C599 arguments/UI result.
    code.emit('8B4DC8FF31')                         # ecx=[ebp-0x38]; push [ecx]
    code.emit('8B0D'); code.u32(TASK_MANAGER)
    code.call(TASK_LOOKUP)
    code.emit('85C0'); code.branch('0F84', 'missing')
    code.emit('6A00FF7018')                         # fourth argument=0; runtime=[record+24]
    code.emit('8B4DC8FF31FF701C')                  # quest=[widget]; type=[record+28]
    code.emit('8B0D'); code.u32(0xD72410)
    code.call(TASK_CLAIM_SEND)
    code.emit('8B4DC8C7414C01000000B803000000')     # original pending=1, result=3
    code.branch('E9', 'done')
    code.label('missing')
    code.emit('31C0')                               # no request, no UI state mutation
    code.label('done')
    code.emit(b'\xE9' + struct.pack('<i', TASK_CLAIM_EXIT - (code.base + len(code.data) + 5)))


def create_gate_stub(code) -> None:
    """Skip the task-window create only for the list the adapter pushed."""
    code.emit('A1'); code.u32(COUNTER)                          # mov eax,[counter]
    code.emit('A3'); code.u32(GATE_FRAME)                       # mov [gate_frame], eax
    code.emit('8B4508')                                         # mov eax,[ebp+8] (packet)
    code.emit('0FB680'); code.u32(TASK_MARKER_OFFSET)           # movzx eax, byte [eax+228]
    code.emit('A3'); code.u32(GATE_FLAG)                        # mov [gate_flag], eax
    code.emit('A2'); code.u32(PUSH_FLAG)                        # mov [push_flag], al
    code.emit('85C0'); code.branch('0F84', 'create')            # marker 0 -> player's book
    code.emit(b'\xE9' + struct.pack('<i', TASK_WINDOW_EPILOGUE - (CREATE_STUB + len(code.data) + 5)))
    code.label('create')
    code.emit('68'); code.u32(0x248)                            # push 0x248 (original)
    code.emit(b'\xE9' + struct.pack('<i', TASK_WINDOW_CREATE_RESUME - (CREATE_STUB + len(code.data) + 5)))


def page_capture_stub(code) -> None:
    """Store the move-handler page identifier and preserve its original stack ABI.

    The hook replaces `push 0xc38508` with a call into this stub. We were entered
    with a return address on the stack, so it is dropped before the original push
    is replayed and control jumps back into the handler.
    """
    code.emit('60')                                   # pushad
    code.emit('8B45EC')                               # mov eax,[ebp-0x14]  (the page)
    code.emit('A3'); code.u32(PAGE_SLOT)              # mov [slot], eax
    code.emit('3D'); code.u32(ROOM_PAGE)              # cmp eax, 31
    code.branch('0F85', 'no_arm')
    code.emit('A1'); code.u32(COUNTER)                # mov eax,[counter]
    code.emit('A3'); code.u32(ARM_FRAME)              # mov [arm_frame], eax
    code.emit('C605'); code.u32(SUPPRESS); code.emit('%02X' % SUPPRESS_FRAMES)
    code.label('no_arm')
    code.emit('61')                                   # popad
    code.emit('8B0424')                               # mov eax,[esp]  (our return address)
    code.emit('83C404')                               # add esp, 4     (drop it)
    code.emit('68'); code.u32(PAGE_LOG_FORMAT)        # push 0xc38508  (original instruction)
    code.emit(b'\xE9' + struct.pack('<i', PAGE_HOOK_TARGET - (STUB + len(code.data) + 5)))


def guide_sequence(code) -> None:
    """Create one instance of the deployed dialogue class and register it."""
    code.emit('68'); code.u32(WINDOW_SIZE); code.call(WINDOW_ALLOC)
    code.emit('83C404')
    code.emit('85C0'); code.branch('0F84', 'gd_done')
    code.emit('8BC8'); code.call(WINDOW_CTOR)            # mov ecx,eax; ctor
    code.emit('8BF8')                                    # edi = window
    code.emit('6A'); code.emit('%02X' % WINDOW_KIND)     # push kind
    code.emit('8BCF'); code.call(WINDOW_INIT)            # mov ecx,edi; init
    code.emit('C707'); code.u32(GUIDE_VTABLE)            # install the dialogue vtable
    code.emit('C74744'); code.u32(OWNER)                 # +0x44 owner record
    code.emit('C7474800000000')                          # +0x48 current page = 0
    code.emit('66C747340100')                            # +0x34 word = 1 (draw me)
    code.emit('C7474C01000000')                          # +0x4C = 1 (as the class sets it)
    code.emit('8B4318'); code.emit('A3'); code.u32(OWNER + OWNER_PAGES)
    code.emit('8B431C'); code.emit('A3'); code.u32(OWNER + OWNER_COUNT)
    # Panel: the class binds it to the village UI owner at [owner+0x334].
    # ebp still belongs to the village update, so [ebp-0x288] is that object.
    code.emit('8B8578FDFFFF')                            # mov eax,[ebp-0x288]
    code.emit('85C0'); code.branch('0F84', 'gd_zero')
    code.emit('8B8034030000')                            # mov eax,[eax+0x334]
    code.label('gd_zero')
    code.emit('50')                                      # push owner (0 when absent)
    code.emit('8D8F00020000'); code.call(PANEL_BIND)     # lea ecx,[edi+0x200]
    code.emit('6A006A00')
    code.emit('8D8F00020000'); code.call(PANEL_SETUP)
    code.emit('8D8F00020000'); code.call(PORTRAIT)       # our portrait copy
    code.emit('8BCF'); code.call(PAGE)                   # copy page 0 of the entry
    # The name must be written *after* the panel exists: binding the panel to the
    # village owner fills it with that owner's guide name (尼莫), which is exactly
    # what an earlier build showed. Re-assert it once more after the page copy.
    name_write(code)
    code.emit('57'); code.call(REGISTRY)                 # push win; GetRegistry()
    code.emit('8BC8'); code.call(REGISTER_INSERT)        # insert -> enters the draw list
    code.emit('893D'); code.u32(OUR_WINDOW)              # save the live window
    code.label('gd_done')


def select_speech(code) -> None:
    """Return a table row or zero. Never dereference a retired window pointer."""
    code.call(REGISTRY)
    code.emit('8B008B7008')  # holder -> registry root -> first live window
    code.label('live_loop')
    code.emit('85F6'); code.branch('0F84', 'retired')
    code.emit('813E'); code.u32(GUIDE_VTABLE)
    code.branch('0F84', 'quiet')  # our open window, or another guide using kind 9
    code.emit('8B7618'); code.branch('E9', 'live_loop')
    code.label('retired')
    code.emit('833D'); code.u32(OUR_WINDOW); code.emit('00')
    code.branch('0F84', 'pending')
    code.emit('C705'); code.u32(OUR_WINDOW); code.u32(0)
    code.emit('FF05'); code.u32(SEG)  # consume this retirement exactly once
    code.label('pending')
    code.emit('833D'); code.u32(ACTIVE_QUEST); code.emit('00')
    code.branch('0F85', 'select')
    code.emit('A1'); code.u32(RECORD)
    code.emit('85C0'); code.branch('0F84', 'quiet')
    code.emit('8B5808')  # Progress3, not State3
    code.emit('8B15'); code.u32(QUEST_SLOT)
    code.emit('3B15'); code.u32(LAST_QUEST)
    code.branch('0F84', 'same_quest')
    code.emit('8915'); code.u32(LAST_QUEST)
    code.emit('C605'); code.u32(ARMED); code.emit('00')
    code.label('same_quest')
    code.emit('803D'); code.u32(ARMED); code.emit('00')
    code.branch('0F85', 'armed')
    code.emit('C605'); code.u32(ARMED); code.emit('01')
    code.emit('881D'); code.u32(LAST)
    code.emit('803D'); code.u32(PUSH_FLAG); code.emit('00')
    code.branch('0F84', 'quiet')
    code.emit('80FB01'); code.branch('0F85', 'quiet')
    code.emit('30DB'); code.branch('E9', 'start')
    code.label('armed')
    code.emit('A0'); code.u32(LAST)
    code.emit('38C3'); code.branch('0F84', 'quiet')
    code.emit('881D'); code.u32(LAST)
    # The creation gate is not called when the task book already exists; its
    # marker is only a first-sighting guard, not a per-packet phase signal.
    code.emit('3C00'); code.branch('0F85', 'exit')
    code.emit('80FB01'); code.branch('0F85', 'quiet')
    code.emit('30DB'); code.branch('E9', 'start')
    code.label('exit')
    code.emit('3C01'); code.branch('0F85', 'quiet')
    code.emit('80FB02'); code.branch('0F85', 'quiet')
    code.emit('B301')
    code.label('start')
    code.emit('8915'); code.u32(ACTIVE_QUEST)
    code.emit('0FB6C38905'); code.u32(ACTIVE_PHASE)
    code.emit('C705'); code.u32(SEG); code.u32(0)
    code.label('select')
    code.emit('8B15'); code.u32(ACTIVE_QUEST)
    code.emit('8B1D'); code.u32(ACTIVE_PHASE)
    code.emit('8B0D'); code.u32(SEG)
    code.emit('8B35'); code.u32(ENTRY_COUNT)
    code.emit('BF'); code.u32(TABLE)
    code.label('row')
    code.emit('85F6'); code.branch('0F84', 'finished')
    code.emit('395720'); code.branch('0F85', 'next')
    code.emit('395F24'); code.branch('0F85', 'next')
    code.emit('394F34'); code.branch('0F84', 'hit')
    code.label('next')
    code.emit('83C7404E'); code.branch('E9', 'row')
    code.label('hit'); code.emit('8BC7C3')
    code.label('finished')
    code.emit('C705'); code.u32(ACTIVE_QUEST); code.u32(0)
    code.branch('E9', 'quiet')
    code.label('quiet'); code.emit('33C0C3')


def build(texts: dict[int, str], triggers: list[dict]) -> dict:
    table = entries(texts, triggers)
    npc_uid, npc_name = chapter_speaker()
    data = bytearray(b'\xCC' * SPAN)

    def put(at: int, raw: bytes, limit: int) -> None:
        shared.require(len(raw) <= limit, 'Chapter dialogue region overflow at 0x%X' % at)
        data[at - CAVE:at - CAVE + len(raw)] = raw

    put(GUARD, b'\0' * 8, 8)
    put(OWNER, b'\0' * 0x40, 0x40)
    put(PROBE, b'\0' * PROBE_SPAN, PROBE_SPAN)
    require_regions()
    put(ARMED, b'\0' * 12, 12)   # armed, last objective, live breadcrumb
    put(OUR_WINDOW, b'\0' * 4, 4)
    put(SAVED_ESP, b'\0' * 8, 8)
    put(NAME, encoded_name(npc_name), NAME_BYTES)
    put(PORTRAIT_PATH, portrait_path(npc_uid), 64)

    # One name/portrait pair per speaker, so the panel follows the chapter. The
    # payload copies the selected row's pair into the two slots the window reads.
    display = speaker_names()
    strings = {}
    cursor = STRINGS
    for npc in sorted({row['npc'] for row in table}):
        shared.require(npc in display, 'Missing chapter speaker name: %d' % npc)
        name = encoded_name(display[npc])
        put(cursor, name, NAME_BYTES)
        name_ptr = cursor
        cursor += NAME_BYTES
        put(cursor, portrait_path(npc).ljust(64, b'\0'), 64)
        strings[npc] = (name_ptr, cursor)
        cursor += 64
    shared.require(cursor <= SELECT, 'Chapter dialogue strings overrun the selector')

    cursor = TEXT
    for index, row in enumerate(table):
        put(cursor, row['data'], CAVE + SPAN - len(TAG) - cursor)
        name_ptr, portrait_ptr = strings[row['npc']]
        put(TABLE + index * ENTRY_SIZE,
            struct.pack('<8I', row['village'], row['page'], row['x'] - 24, row['y'] - 48,
                        row['x'] + 24, row['y'] + 48, cursor, row['pages'])
            + struct.pack('<2I', row['quest'], row['objective'])
            + struct.pack('<I', row['qs_id'])
            + struct.pack('<2I', name_ptr, portrait_ptr)
            + struct.pack('<I', row.get('speech', 0))
            + b'\0' * (ENTRY_SIZE - 56), ENTRY_SIZE)
        row['cursor'] = cursor
        cursor += len(row['data'])
    put(ENTRY_COUNT, struct.pack('<I', len(table)), 4)
    put(QUEST_COUNT, struct.pack('<I', max(row['episode'] for row in table)), 4)
    put(QUEST_SLOT, b'\0' * 4, 4)
    put(RECORD, b'\0' * 4, 4)
    put(PUSH_FLAG, b'\0', 1)
    put(SEG, b'\0' * 4, 4)
    put(LAST_QUEST, b'\0' * 12, 12)

    code = monk.Code(UPDATE)
    def mark(stage: int) -> None:
        code.emit('C605'); code.u32(MARK); code.emit('%02X' % stage)
    code.emit('60')                                  # pushad
    code.emit('8925'); code.u32(SAVED_ESP)           # mov [saved], esp
    code.emit('FF05'); code.u32(COUNTER)             # inc dword [counter]
    code.emit('803D'); code.u32(SUPPRESS); code.emit('00')
    code.branch('0F84', 'no_countdown')
    code.emit('FE0D'); code.u32(SUPPRESS)            # dec byte [suppress]
    code.label('no_countdown')
    probe_snapshot(code)
    mark(1)
    code.emit('68'); code.u32(MARKER | STAGE_ENTER)  # breadcrumb on the stack
    code.emit('8B0D'); code.u32(TASK_MANAGER)
    code.emit('85C9'); code.branch('0F84', 'done')   # no task manager yet
    # Walk every installed chapter and keep the *last* match: the client can still
    # hold the previous chapter's record for a frame or two after the slot moves on,
    # and taking the first hit made the new chapter replay the old speaker's lines.
    # ecx must carry the task manager into the call, so the counter lives in esi and
    # ecx is reloaded every round.
    code.emit('C705'); code.u32(RECORD); code.u32(0)  # mov dword [record], 0
    code.emit('BF'); code.u32(FIRST_QUEST)           # mov edi, FIRST_QUEST
    code.emit('8B35'); code.u32(QUEST_COUNT)         # mov esi,[quest_count]
    code.label('lk_loop')
    code.emit('85F6'); code.branch('0F84', 'lk_done')
    code.emit('8B0D'); code.u32(TASK_MANAGER)        # mov ecx,[task_manager]
    code.emit('57')                                  # push edi
    code.call(TASK_LOOKUP)                           # stdcall: the callee pops the id
    code.emit('85C0'); code.branch('0F84', 'lk_next')
    code.emit('893D'); code.u32(QUEST_SLOT)          # mov [quest_slot], edi
    code.emit('A3'); code.u32(RECORD)                # mov [record], eax
    code.label('lk_next')
    code.emit('47')                                  # inc edi
    code.emit('4E')                                  # dec esi
    code.branch('E9', 'lk_loop')
    code.label('lk_done')
    code.emit('68'); code.u32(MARKER | STAGE_FOUND)
    mark(2)
    code.call(SELECT)
    code.emit('85C0'); code.branch('0F84', 'done')
    code.emit('8BF8')                                # edi = selected speech row
    code.emit('8B772C')                              # mov esi,[edi+44] (name ptr)
    for offset in range(0, NAME_BYTES, 4):
        code.emit('8B46'); code.emit('%02X' % offset)     # mov eax,[esi+offset]
        code.emit('A3'); code.u32(NAME + offset)
    code.emit('57')                                  # push edi
    code.emit('8B7730')                              # mov esi,[edi+48] (portrait ptr)
    code.emit('BF'); code.u32(PORTRAIT_PATH)         # mov edi, PORTRAIT_PATH
    code.emit('B910000000')                          # mov ecx,16
    code.emit('FCF3A5')                              # cld; rep movsd
    code.emit('5F')                                  # pop edi
    code.emit('8BDF')                                # mov ebx, edi
    code.label('show')
    mark(3)
    code.emit('68'); code.u32(MARKER | STAGE_MATCH)
    guide_sequence(code)
    code.label('done')
    mark(0x0A)
    code.emit('68'); code.u32(MARKER | STAGE_DONE)
    code.emit('8B25'); code.u32(SAVED_ESP)           # mov esp, [saved]
    code.emit('61')                                  # popad
    code.emit(b'\xE9' + struct.pack('<i', HOOK_CHAIN - (UPDATE + len(code.data) + 5)))
    update_bytes = len(code.data)
    put(UPDATE, code.finish(), UPDATE_LIMIT)

    selector = monk.Code(SELECT)
    select_speech(selector)
    put(SELECT, selector.finish(), TEXT - SELECT)

    page_code = monk.Code(PAGE)
    page_code.emit('5356578BD98B73448B434869C0CC0000000346188BF08DBB28020000B933000000FCF3A5')
    for offset, value in ((0x354, 1), (0x35C, 1), (0x360, 0), (0x350, 0), (0x368, 1)):
        page_code.emit('C783'); page_code.u32(offset); page_code.u32(value)
    page_code.emit('8B43448B5348423B501C0F94C00FB6C0898364030000')
    page_code.emit('6A018D8B00020000'); page_code.call(0x405F7E)
    page_code.emit('5F5E5BC3')
    put(PAGE, page_code.finish(), PAGE_LIMIT)

    portrait = monk.Code(PORTRAIT)
    portrait_routine(portrait)
    put(PORTRAIT, portrait.finish(), 0x60)

    if UPDATE + update_bytes > STUB:
        raise ValueError('update block overruns the page-capture stub at %#x' % STUB)
    stub = monk.Code(STUB)
    page_capture_stub(stub)
    put(STUB, stub.finish(), PAGE_SLOT - STUB)
    put(PAGE_SLOT, b'\0' * 4, 4)
    put(LATCH, b'\0', 1)
    put(SUPPRESS, b'\0', 1)
    for slot in (COUNTER, ARM_FRAME, GATE_FRAME, GATE_FLAG):
        put(slot, b'\0' * 4, 4)
    if UPDATE + update_bytes > CREATE_STUB:
        raise ValueError('update block overruns the create gate stub at %#x' % CREATE_STUB)
    if CREATE_STUB + 0x40 > PAGE:
        raise ValueError('create gate stub overruns PAGE at %#x' % PAGE)
    gate = monk.Code(CREATE_STUB)
    create_gate_stub(gate)
    put(CREATE_STUB, gate.finish(), 0x40)

    claim = monk.Code(CLAIM_STUB)
    claim_stub(claim)
    put(CLAIM_STUB, claim.finish(), PAGE - CLAIM_STUB)

    put(CAVE + SPAN - len(TAG), TAG, len(TAG))
    payload = bytes(data)
    patch = b'\xE8' + struct.pack('<i', UPDATE - HOOK_SITE - 5)
    page_patch = b'\xE8' + struct.pack('<i', STUB - PAGE_HOOK_SITE - 5)
    window_patch = b'\xE9' + struct.pack('<i', CREATE_STUB - TASK_WINDOW_SITE - 5)
    claim_patch = b'\xE9' + struct.pack('<i', CLAIM_STUB - TASK_CLAIM_SITE - 5)
    report = dict(schema=SCHEMA, cave=CAVE, span=SPAN, hook_site=HOOK_SITE,
                  chain_target=HOOK_CHAIN, tag=TAG.decode(),
                  page_hook_site=PAGE_HOOK_SITE, page_slot=PAGE_SLOT, stub=STUB,
                  page_patch=page_patch.hex(), page_hook_original=PAGE_HOOK_ORIGINAL.hex(),
                  task_window_site=TASK_WINDOW_SITE, task_window_patch=window_patch.hex(),
                  task_claim_site=TASK_CLAIM_SITE, task_claim_patch=claim_patch.hex(),
                  task_claim_stub=CLAIM_STUB, task_claim_bytes=len(claim.data),
                  task_manager=TASK_MANAGER, task_lookup=TASK_LOOKUP, guard=GUARD,
                  guide_vtable=GUIDE_VTABLE, window_size=WINDOW_SIZE,
                  window_ctor=WINDOW_CTOR, window_init=WINDOW_INIT, window_kind=WINDOW_KIND,
                  panel_bind=PANEL_BIND, panel_setup=PANEL_SETUP,
                  registry=REGISTRY, register_insert=REGISTER_INSERT,
                  owner=OWNER, owner_pages=OWNER + OWNER_PAGES, owner_count=OWNER + OWNER_COUNT,
                  portrait=PORTRAIT, portrait_path=PORTRAIT_PATH,
                  portrait_file=portrait_path(npc_uid).split(b'\0')[0].decode('ascii'),
                  npc_uid=npc_uid, npc_name=npc_name, name_slot=NAME,
                  name_bytes=NAME_BYTES, native_name_bytes=PANEL_NAME_BYTES,
                  selector=SELECT, selector_bytes=len(selector.data),
                  entries=[{key: row[key] for key in
                            ('episode', 'objective', 'quest', 'qs_id', 'pages', 'cursor', 'npc', 'speech')}
                           for row in table],
                  update_bytes=update_bytes, page_bytes=len(page_code.data),
                  text_bytes=cursor - TEXT,
                  payload_sha256=shared.digest(payload),
                  patch=patch.hex(), activation_allowed=False, runtime_acceptance=False)
    return dict(payload=payload, patch=patch, report=report)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--client', type=Path)
    parser.add_argument('--output', type=Path)
    args = parser.parse_args(argv)
    print(json.dumps(dict(schema=SCHEMA, status='dry-run-only'), indent=2))
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
