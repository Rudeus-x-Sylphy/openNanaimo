"""Checks for the event-driven chapter dialogue payload builder."""
from __future__ import annotations

import struct
from pathlib import Path
import sys
import unittest
import unicorn as uc
from unicorn import x86_const as x

sys.path.insert(0, str(Path(__file__).resolve().parent))
import chapter_dialogue_patch as chapter  # noqa: E402

TEXTS = {
    3001: '你是受尼莫委托，前去击退村里出现的怪物^&的吧？骑着宠物进入地宫后，'
          '^&卡片册的魔力会让宠物变成飞行推进器，^&这你应该知道了吧？我也有件事想拜托你。',
    3002: '哇，真了不起！出现在普拉塔诺斯天空的^&怪物都被你打败了！',
}
TRIGGERS = [
    dict(village=0, page=27, x=293, y=189, qs_id=3001, episode=1, objective=0, npc_uid=52),
    dict(village=0, page=27, x=120, y=430, qs_id=3002, episode=1, objective=1, npc_uid=52),
]


def call_targets(blob, base):
    """Every direct call site in a generated block, as (offset, target)."""
    rows = []
    for offset in range(len(blob) - 4):
        if blob[offset] != 0xE8:
            continue
        target = base + offset + 5 + int.from_bytes(blob[offset + 1:offset + 5], 'little', signed=True)
        rows.append((offset, target))
    return rows


class PageTests(unittest.TestCase):
    def test_rows_are_fifty_one_bytes_and_four_per_page(self):
        rows = chapter.pages(TEXTS[3001])
        self.assertTrue(rows)
        for page in rows:
            self.assertEqual(len(page), chapter.ROW_BYTES * chapter.ROWS_PER_PAGE)
            for index in range(chapter.ROWS_PER_PAGE):
                row = page[index * chapter.ROW_BYTES:(index + 1) * chapter.ROW_BYTES]
                self.assertTrue(row.rstrip(b'\0').decode('gbk'))

    def test_short_text_is_one_page(self):
        self.assertEqual(len(chapter.pages('你好')), 1)

    def test_tail_line_is_appended(self):
        rows = chapter.pages('你好', '现在开始挑战吗？')
        cells = b''.join(rows).split(b'\0')
        self.assertTrue(any(cell == '现在开始挑战吗？'.encode('gbk') for cell in cells))


class EntryTests(unittest.TestCase):
    def test_one_entry_per_phase(self):
        rows = chapter.entries(TEXTS, TRIGGERS)
        self.assertEqual([row['objective'] for row in rows], [0, 1])
        self.assertEqual([row['quest'] for row in rows], [73_000_000, 73_000_000])
        self.assertTrue(all(row['pages'] >= 1 for row in rows))

    def test_only_dialogue_phases_are_accepted(self):
        with self.assertRaises(ValueError):
            chapter.entries(TEXTS, [dict(TRIGGERS[0], objective=3)])

    def test_missing_text_is_refused(self):
        with self.assertRaises(ValueError):
            chapter.entries(TEXTS, [dict(TRIGGERS[0], qs_id=9999)])

    def test_table_capacity(self):
        # Rows are grouped by (episode, phase), so the overflow needs distinct
        # chapters: MAX_ENTRIES + 1 separate entrance phases.
        triggers = [dict(TRIGGERS[0], episode=index + 1, speech_index=0)
                    for index in range(chapter.MAX_ENTRIES + 1)]
        with self.assertRaises(ValueError):
            chapter.entries(TEXTS, triggers)


class LayoutTests(unittest.TestCase):
    """The cave layout is hand-mapped; an overlap corrupts the saved stack."""

    def test_regions_do_not_overlap(self):
        chapter.require_regions()

    def test_probe_does_not_touch_any_state_slot(self):
        probe_start = chapter.PROBE
        probe_end = chapter.PROBE + chapter.PROBE_SPAN
        for name, start, limit in chapter.REGIONS:
            if name == 'probe':
                continue
            self.assertFalse(probe_start < start + limit and start < probe_end,
                             'probe overlaps %s at %#x' % (name, start))

    def test_saved_stack_pointer_stays_inside_its_own_slot(self):
        for name, start, limit in chapter.REGIONS:
            if name == 'saved_esp':
                self.assertLessEqual(start + limit, chapter.ARMED)


class BuildTests(unittest.TestCase):
    def setUp(self):
        self.result = chapter.build(TEXTS, TRIGGERS)
        self.report = self.result['report']
        self.code = self.result['payload'][chapter.UPDATE - chapter.CAVE:chapter.TABLE - chapter.CAVE]

    def test_payload_has_cave_size(self):
        self.assertEqual(len(self.result['payload']), chapter.SPAN)

    def test_report_records_the_state_channel(self):
        self.assertEqual(self.report['schema'], chapter.SCHEMA)
        self.assertEqual(self.report['task_manager'], 0xD7330C)
        self.assertEqual(self.report['task_lookup'], 0x41BAE0)
        self.assertEqual(self.report['chain_target'], chapter.HOOK_CHAIN)
        self.assertEqual(self.report['hook_site'], chapter.HOOK_SITE)
        self.assertFalse(self.report['activation_allowed'])
        self.assertFalse(self.report['runtime_acceptance'])

    def test_patch_is_a_relative_call_to_the_update_entry(self):
        patch = self.result['patch']
        self.assertEqual(patch[0], 0xE8)
        offset = int.from_bytes(patch[1:5], 'little', signed=True)
        self.assertEqual(chapter.HOOK_SITE + 5 + offset, chapter.UPDATE)

    def test_replaced_call_points_at_the_native_chain(self):
        original = chapter.HOOK_ORIGINAL
        self.assertEqual(original[0], 0xE8)
        offset = int.from_bytes(original[1:5], 'little', signed=True)
        self.assertEqual(chapter.HOOK_SITE + 5 + offset, chapter.HOOK_CHAIN)

    def test_payload_tail_calls_into_the_native_chain(self):
        body = self.code[:self.report['update_bytes']]
        self.assertEqual(body[0], 0x60)                     # pushad, never pushfd
        self.assertNotIn(b'\x61\x9d', body)
        self.assertIn(b'\x8b\x25' + chapter.SAVED_ESP.to_bytes(4, 'little'), body)
        self.assertEqual(body[-5], 0xE9)
        offset = int.from_bytes(body[-4:], 'little', signed=True)
        self.assertEqual(chapter.UPDATE + len(body) + offset, chapter.HOOK_CHAIN)

    def test_breadcrumbs_are_pushed_on_the_stack(self):
        for stage in (chapter.STAGE_ENTER, chapter.STAGE_FOUND, chapter.STAGE_MATCH,
                      chapter.STAGE_DONE):
            needle = b'\x68' + (chapter.MARKER | stage).to_bytes(4, 'little')
            self.assertIn(needle, self.code)

    def test_guard_starts_zeroed(self):
        start = chapter.ARMED - chapter.CAVE
        self.assertEqual(self.result['payload'][start:start + 12], b'\0' * 12)

    def test_table_keeps_the_monk_layout_and_adds_the_state(self):
        row = self.report['entries'][0]
        start = chapter.TABLE - chapter.CAVE
        fields = struct.unpack('<10I', self.result['payload'][start:start + 40])
        self.assertEqual(fields[0:2], (0, 27))
        self.assertEqual(fields[2:6], (293 - 24, 189 - 48, 293 + 24, 189 + 48))
        self.assertEqual((fields[6], fields[7]), (row['cursor'], row['pages']))
        # +32 carries the quest id now: the payload looks the chapter up by it.
        self.assertEqual((fields[8], fields[9]), (73_000_000, 0))

    def test_lookup_and_guard_appear_in_the_code(self):
        self.assertIn(struct.pack('<I', chapter.TASK_MANAGER), self.code)
        selector = self.result['payload'][chapter.SELECT - chapter.CAVE:chapter.TEXT - chapter.CAVE]
        for address in (chapter.SAVED_ESP, chapter.ARMED, chapter.LAST, chapter.MARK):
            self.assertIn(struct.pack('<I', address), self.code + selector)
        self.assertIn(struct.pack('<I', chapter.FIRST_QUEST), self.code)

    def test_payload_is_deterministic(self):
        again = chapter.build(TEXTS, TRIGGERS)
        self.assertEqual(again['payload'], self.result['payload'])

    def test_claim_jump_and_managed_upgrade_contract(self):
        import generate_managed_compatibility_recipe as managed
        rows = managed.generate()['sites']
        claim = next(row for row in rows if row['operation'] == 'chapter_task_claim_lookup_guard')
        self.assertEqual(claim['va'], chapter.TASK_CLAIM_SITE)
        self.assertEqual(claim['known'], [chapter.TASK_CLAIM_ORIGINAL.hex()])
        patch = bytes.fromhex(claim['target'])
        self.assertEqual(chapter.TASK_CLAIM_SITE + 5 + struct.unpack('<i', patch[1:])[0],
                         chapter.CLAIM_STUB)
        cave = next(row for row in rows if row['operation'] == 'chapter_dialogue_cave')
        self.assertIn(chapter.PREVIOUS_PAYLOAD_SHA256, cave['known_hashes'])
        self.assertEqual(bytes.fromhex(cave['target']),
                         chapter.build(chapter.default_texts(), chapter.default_triggers())['payload'])

    def test_python_compatibility_repeats_and_rejects_unknown_click_site(self):
        from unittest import mock
        import prepare_client_compatibility as compat
        bundle = chapter.build(chapter.default_texts(), chapter.default_triggers())
        image = bytearray(b'\xCC' * 0xA00000)
        for site, original in ((chapter.HOOK_SITE, chapter.HOOK_ORIGINAL),
                               (chapter.PAGE_HOOK_SITE, chapter.PAGE_HOOK_ORIGINAL),
                               (chapter.TASK_WINDOW_SITE, chapter.TASK_WINDOW_ORIGINAL),
                               (chapter.TASK_CLAIM_SITE, chapter.TASK_CLAIM_ORIGINAL)):
            image[site - 0x400000:site - 0x400000 + len(original)] = original
        with mock.patch.object(compat, '_va_offset', side_effect=lambda data, va, size: va - 0x400000):
            target, _ = compat.patch_chapter_dialogue(bytes(image))
            self.assertEqual(compat.patch_chapter_dialogue(target)[0], target)
            offset = chapter.TASK_CLAIM_SITE - 0x400000
            self.assertEqual(target[offset:offset + 5], bytes.fromhex(bundle['report']['task_claim_patch']))
            image[offset:offset + 5] = b'\x90' * 5
            with self.assertRaises(compat.CompatibilityError):
                compat.patch_chapter_dialogue(bytes(image))


class TriggerTests(unittest.TestCase):
    """A fresh session's first record usually arrives already at objective 1."""

    def setUp(self):
        self.result = chapter.build(TEXTS, TRIGGERS)
        self.code = self.result['payload'][chapter.SELECT - chapter.CAVE:
                                           chapter.SELECT - chapter.CAVE + self.result['report']['selector_bytes']]

    def test_first_record_at_objective_one_fires_the_entrance_speech(self):
        armed = self.code.index(b'\xC6\x05' + struct.pack('<I', chapter.ARMED) + b'\x01')
        tail = self.code[armed:armed + 64]
        self.assertIn(b'\x80\xFB\x01', tail)                       # cmp bl,1
        self.assertIn(b'\xE9', tail)                              # jmp select

    def test_first_record_still_stores_the_baseline(self):
        armed = self.code.index(b'\xC6\x05' + struct.pack('<I', chapter.ARMED) + b'\x01')
        self.assertIn(b'\x88\x1D' + struct.pack('<I', chapter.LAST), self.code[armed:armed + 16])


class WindowTests(unittest.TestCase):
    """The window must be an instance of the dialogue class the client carries."""

    def setUp(self):
        self.result = chapter.build(TEXTS, TRIGGERS)
        self.report = self.result['report']
        self.code = self.result['payload'][chapter.UPDATE - chapter.CAVE:chapter.TABLE - chapter.CAVE]
        self.payload = self.result['payload']

    def test_window_is_allocated_with_the_class_size(self):
        self.assertEqual(self.report['window_size'], 0x400)
        self.assertIn(b'\x68' + struct.pack('<I', 0x400), self.code)
        # 0x144 belongs to the portrait image, never to the window
        window_alloc = b'\x68' + struct.pack('<I', 0x400) + b'\xE8'
        self.assertIn(window_alloc, self.code)

    def test_construction_uses_the_installed_class(self):
        self.assertEqual(self.report['guide_vtable'], 0x6E4000)
        self.assertIn(b'\xC7\x07' + struct.pack('<I', 0x6E4000), self.code)
        targets = [target for _, target in call_targets(self.code, chapter.UPDATE)]
        self.assertIn(chapter.WINDOW_CTOR, targets)
        self.assertIn(chapter.WINDOW_INIT, targets)
        self.assertIn(b'\x6A\x09', self.code)               # kind 9

    def test_panel_and_portrait_are_initialised_before_the_page_copy(self):
        calls = call_targets(self.code, chapter.UPDATE)
        for address in (chapter.PANEL_BIND, chapter.PANEL_SETUP, chapter.PORTRAIT):
            self.assertIn(address, [target for _, target in calls])
        bind = next(offset for offset, target in calls if target == chapter.PANEL_BIND)
        page = next(offset for offset, target in calls if target == chapter.PAGE)
        self.assertLess(bind, page)

    def test_window_is_registered_in_the_engine_registry(self):
        targets = [target for _, target in call_targets(self.code, chapter.UPDATE)]
        self.assertIn(chapter.REGISTRY, targets)
        self.assertIn(chapter.REGISTER_INSERT, targets)
        calls = call_targets(self.code, chapter.UPDATE)
        registry = next(offset for offset, target in calls if target == chapter.REGISTRY)
        insert = next(offset for offset, target in calls if target == chapter.REGISTER_INSERT)
        self.assertLess(registry, insert)

    def test_owner_record_points_at_the_entry_pages(self):
        owner = chapter.OWNER - chapter.CAVE
        self.assertEqual(self.payload[owner:owner + 4], b'\0' * 4)
        # the code copies entry +0x18/+0x1C into owner +0x18/+0x1C
        self.assertIn(b'\x8B\x43\x18\xA3' + struct.pack('<I', chapter.OWNER + 0x18), self.code)
        self.assertIn(b'\x8B\x43\x1C\xA3' + struct.pack('<I', chapter.OWNER + 0x1C), self.code)

    def test_portrait_path_is_the_unpadded_npc_resource(self):
        self.assertEqual(self.report['portrait_file'], 'interface\\NPC_52_0.eff')
        start = chapter.PORTRAIT_PATH - chapter.CAVE
        raw = self.payload[start:start + 64].split(b'\0')[0]
        self.assertEqual(raw, b'interface\\NPC_52_0.eff')

    def test_display_name_is_written_into_the_window(self):
        start = chapter.NAME - chapter.CAVE
        self.assertEqual(self.payload[start:start + 6], '伊露卡'.encode('gbk'))
        self.assertIn(b'\x89\x87\x00\x02\x00\x00', self.code)   # mov [edi+0x200], eax
        self.assertIn(b'\x89\x87\x04\x02\x00\x00', self.code)   # mov [edi+0x204], eax

    def test_name_is_written_after_the_panel_is_populated(self):        # Binding the panel to the village owner fills the name with that owner's
        # guide NPC (尼莫); our name must be written last so it wins.
        calls = call_targets(self.code, chapter.UPDATE)
        setup = next(offset for offset, target in calls if target == chapter.PANEL_SETUP)
        page = next(offset for offset, target in calls if target == chapter.PAGE)
        name_marker = self.code.index(b'\xA1' + struct.pack('<I', chapter.NAME))
        self.assertGreater(name_marker, setup)
        self.assertGreater(name_marker, page)

    def test_no_draw_hook_patch_is_emitted(self):
        # The class's own draw path paints the window once it is registered.
        self.assertNotIn('draw_patch', self.report)


class ClaimExecutionTests(unittest.TestCase):
    # Real click-branch bytes with mocked lookup/send, not full-client acceptance.
    SITE, EXIT, SEND = 0xA5FD76, 0xA5FE1B, 0x402E00
    ORIGINAL = bytes.fromhex(
        '6a008b55c88b02508b0d0c33d700e857bd9bff8b4818518b55c88b0250'
        '8b4dc88b11528b0d0c33d700e83cbd9bff8b401c508b0d1024d700'
        'e84d309aff8b4dc8c7414c01000000b803000000eb57')

    def setUp(self):
        self.m = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        self.m.mem_map(0x400000, 0xA00000)
        self.m.mem_map(0x2000000, 0x10000)
        self.widget, self.record = 0x2000000, 0x2001000
        self.stack, self.frame = 0x200F000, 0x200E000
        self.quest, self.present, self.sent, self.lookups = chapter.FIRST_QUEST, True, [], 0
        self.put(self.widget, self.quest)
        self.put(self.record + 0x18, 0)
        self.put(self.record + 0x1C, 2)
        self.put(self.frame - 0x38, self.widget)
        self.put(chapter.TASK_MANAGER, 0x2002000)
        self.put(0xD72410, 0x2003000)
        self.m.mem_write(self.SITE, self.ORIGINAL)
        self.m.mem_write(chapter.TASK_LOOKUP, b'\xC2\x04\x00')
        self.m.mem_write(self.SEND, b'\xC2\x10\x00')
        self.m.hook_add(uc.UC_HOOK_CODE, self.stub)

    def put(self, address, value):
        self.m.mem_write(address, struct.pack('<I', value))

    def get(self, address):
        return struct.unpack('<I', self.m.mem_read(address, 4))[0]

    def stub(self, m, address, size, user):
        sp = m.reg_read(x.UC_X86_REG_ESP)
        if address == chapter.TASK_LOOKUP:
            self.lookups += 1
            self.assertEqual(self.get(sp + 4), self.quest)
            m.reg_write(x.UC_X86_REG_EAX, self.record if self.present else 0)
        elif address == self.SEND:
            self.sent.append(tuple(self.get(sp + 4 + i * 4) for i in range(4)))
            m.reg_write(x.UC_X86_REG_EAX, 1)

    def click(self):
        self.m.reg_write(x.UC_X86_REG_EBP, self.frame)
        self.m.reg_write(x.UC_X86_REG_ESP, self.stack)
        for reg in (x.UC_X86_REG_EBX, x.UC_X86_REG_ESI, x.UC_X86_REG_EDI):
            self.m.reg_write(reg, 0x12345678)
        self.m.emu_start(self.SITE, self.EXIT, count=1000)
        self.assertEqual(self.m.reg_read(x.UC_X86_REG_EIP), self.EXIT)
        self.assertEqual(self.m.reg_read(x.UC_X86_REG_ESP), self.stack)
        for reg in (x.UC_X86_REG_EBX, x.UC_X86_REG_ESI, x.UC_X86_REG_EDI):
            self.assertEqual(self.m.reg_read(reg), 0x12345678)
        return self.m.reg_read(x.UC_X86_REG_EAX)

    def install_guard(self):
        bundle = chapter.build(TEXTS, TRIGGERS)
        self.m.mem_write(chapter.CAVE, bundle['payload'])
        self.m.mem_write(self.SITE, bytes.fromhex(bundle['report']['task_claim_patch']))

    def test_original_double_click_sends_twice_while_waiting(self):
        self.assertEqual(self.click(), 3)
        self.assertEqual(self.click(), 3)
        self.assertEqual(self.sent, [(2, self.quest, 0, 0)] * 2)

    def test_original_stale_click_reads_null_after_task_refresh(self):
        self.click()
        self.present = False
        with self.assertRaises(uc.UcError) as caught:
            self.click()
        self.assertEqual(caught.exception.errno, uc.UC_ERR_READ_UNMAPPED)
        self.assertEqual(self.m.reg_read(x.UC_X86_REG_EIP), 0xA5FD89)

    def test_guarded_live_task_preserves_retry_semantics(self):
        self.install_guard()
        self.assertEqual(self.click(), 3)
        self.assertEqual(self.click(), 3)
        self.assertEqual(self.sent, [(2, self.quest, 0, 0)] * 2)
        self.assertEqual(self.lookups, 2)
        self.assertEqual(self.get(self.widget + 0x4C), 1)

    def test_guarded_stale_click_does_not_dereference_removed_task(self):
        self.install_guard()
        self.click()
        self.present = False
        self.assertEqual(self.click(), 0)
        self.assertEqual(len(self.sent), 1)
        self.assertEqual(self.lookups, 2)

    def test_guarded_missing_task_without_pending_request_is_ignored(self):
        self.install_guard()
        self.present = False
        self.assertEqual(self.click(), 0)
        self.assertEqual(self.sent, [])
        self.assertEqual(self.get(self.widget + 0x4C), 0)

    def test_existing_ui_reset_allows_retry_and_next_chapter(self):
        self.install_guard()
        self.click()
        self.put(self.widget + 0x4C, 0)
        self.assertEqual(self.click(), 3)
        self.quest += 1
        self.put(self.widget, self.quest)
        self.put(self.widget + 0x4C, 0)
        self.assertEqual(self.click(), 3)
        self.assertEqual(self.sent, [(2, self.quest - 1, 0, 0)] * 2 + [(2, self.quest, 0, 0)])

    def test_guarded_arguments_match_original_for_all_task_categories(self):
        for task_type, runtime, quest in ((0, 7, 70000155), (1, 1, 71000000),
                                         (2, 0, 73000000), (2, 0, 73000015)):
            with self.subTest(task_type=task_type, runtime=runtime, quest=quest):
                self.quest = quest
                self.put(self.widget, quest)
                self.put(self.record + 0x18, runtime)
                self.put(self.record + 0x1C, task_type)
                self.m.mem_write(self.SITE, self.ORIGINAL)
                self.assertEqual(self.click(), 3)
                original = self.sent[-1]
                self.install_guard()
                self.assertEqual(self.click(), 3)
                self.assertEqual(self.sent[-1], original)
                self.assertEqual(original, (task_type, quest, runtime, 0))


class NativeExecutionTests(unittest.TestCase):
    """Generated x86 with stubbed engine APIs, not original-client acceptance."""

    def setUp(self):
        self.bundle = chapter.build(chapter.default_texts(), chapter.default_triggers())
        self.m = uc.Uc(uc.UC_ARCH_X86, uc.UC_MODE_32)
        self.m.mem_map(0x400000, 0xA00000)
        self.m.mem_map(0x2000000, 0x50000)
        self.m.mem_write(chapter.CAVE, self.bundle['payload'])
        self.m.mem_write(chapter.monk.BASE, chapter.monk.payload())
        self.stack, self.end, self.frame = 0x2040000, 0x2041000, 0x2030000
        self.holder, self.root, self.record = 0x2000000, 0x2000100, 0x2000200
        self.effect, self.vtable = 0x2000300, 0x2000400
        self.put(self.holder, self.root)
        self.put(chapter.TASK_MANAGER, 0x2000500)
        self.put(self.effect, self.vtable)
        self.put(self.vtable + 0x10, 0x406122)
        self.quest = chapter.FIRST_QUEST
        self.allocations = 0
        self.fail_alloc = False
        self.shown = []
        self.button = 0
        self.stubs = {
            chapter.TASK_LOOKUP: 4, chapter.WINDOW_ALLOC: 0,
            chapter.WINDOW_CTOR: 0, chapter.WINDOW_INIT: 4,
            chapter.PANEL_BIND: 4, chapter.PANEL_SETUP: 8,
            chapter.IMAGE_CTOR: 0, chapter.IMAGE_LOAD: 20,
            chapter.REGISTRY: 0, chapter.REGISTER_INSERT: 4,
            chapter.HOOK_CHAIN: 0, 0x405F7E: 4, 0x407DCE: 0,
            0x406122: 0, 0x414353: 0, 0x4169A0: 0, 0x415064: 0,
            0x418B83: 0, 0x419B0A: 0, 0x402770: 4,
        }
        for address, args in self.stubs.items():
            self.m.mem_write(address, b'\xC2' + struct.pack('<H', args))
        self.m.hook_add(uc.UC_HOOK_CODE, self.stub)

    def put(self, address, value):
        self.m.mem_write(address, struct.pack('<I', value))

    def get(self, address):
        return struct.unpack('<I', self.m.mem_read(address, 4))[0]

    def stub(self, m, address, size, user):
        if address not in self.stubs:
            return
        arg = self.get(m.reg_read(x.UC_X86_REG_ESP) + 4)
        this = m.reg_read(x.UC_X86_REG_ECX)
        value = 0
        if address == chapter.TASK_LOOKUP:
            value = self.record if arg == self.quest else 0
        elif address == chapter.REGISTRY:
            value = self.holder
        elif address == chapter.WINDOW_ALLOC:
            if arg == chapter.WINDOW_SIZE and not self.fail_alloc:
                self.allocations += 1
                value = 0x3000000 + self.allocations * 0x1000
                m.mem_map(value, 0x1000)
            elif arg == chapter.IMAGE_SIZE:
                value = self.effect
        elif address in (chapter.WINDOW_CTOR, chapter.IMAGE_CTOR):
            value = this
        elif address == chapter.PANEL_SETUP:
            self.put(this + 0xFC, self.effect)
            # Canary after the 24-byte copy, still inside the native name field.
            m.mem_write(this + chapter.NAME_BYTES, b'Z' * (chapter.PANEL_NAME_BYTES - chapter.NAME_BYTES))
        elif address == chapter.IMAGE_LOAD:
            self.portrait = bytes(m.mem_read(arg, 64)).split(b'\0')[0].decode('ascii')
        elif address == chapter.REGISTER_INSERT:
            self.put(arg + 0x18, self.get(self.root + 8))
            self.put(self.root + 8, arg)
            self.shown.append((self.get(chapter.ACTIVE_QUEST), self.get(chapter.ACTIVE_PHASE),
                               self.get(chapter.SEG), self.name(arg), self.portrait))
        elif address == 0x4169A0:
            value = self.button
        m.reg_write(x.UC_X86_REG_EAX, value)

    def name(self, window):
        return bytes(self.m.mem_read(window + 0x200, chapter.NAME_BYTES)).split(b'\0')[0].decode('gbk')

    def run_code(self, address, window=0):
        self.m.reg_write(x.UC_X86_REG_EBP, self.frame)
        self.m.reg_write(x.UC_X86_REG_ECX, window)
        self.m.reg_write(x.UC_X86_REG_ESP, self.stack)
        for reg in (x.UC_X86_REG_EBX, x.UC_X86_REG_ESI, x.UC_X86_REG_EDI):
            self.m.reg_write(reg, 0x12345678)
        self.put(self.stack, self.end)
        self.m.emu_start(address, self.end, count=10000)
        self.assertEqual(self.m.reg_read(x.UC_X86_REG_EIP), self.end)
        self.assertEqual(self.m.reg_read(x.UC_X86_REG_ESP), self.stack + 4)
        for reg in (x.UC_X86_REG_EBX, x.UC_X86_REG_ESI, x.UC_X86_REG_EDI):
            self.assertEqual(self.m.reg_read(reg), 0x12345678)
        return self.m.reg_read(x.UC_X86_REG_EAX)

    def tick(self, phase=None, pushed=True):
        if phase is not None:
            self.put(self.record + 8, phase)
        self.m.mem_write(chapter.PUSH_FLAG, bytes([pushed]))
        self.run_code(chapter.UPDATE)

    def finish_window(self):
        win = self.get(chapter.OUR_WINDOW)
        count = self.get(chapter.OWNER + chapter.OWNER_COUNT)
        base = self.get(chapter.OWNER + chapter.OWNER_PAGES)
        self.button = 5
        for page in range(count):
            self.assertEqual(self.get(win + 0x48), page)
            self.assertEqual(bytes(self.m.mem_read(win + 0x228, 204)),
                             bytes(self.m.mem_read(base + page * 204, 204)))
            result = self.run_code(chapter.monk.DIALOGUE, win)
            self.assertEqual(result, 2 if page + 1 == count else 1)
        self.button = 0
        self.assertEqual(self.get(win + 0x34) & 0xFFFF, 2)
        self.put(self.root + 8, self.get(win + 0x18))
        self.m.mem_unmap(win, 0x1000)  # fail on any dangling-pointer read

    def test_all_107_speeches_page_and_retire_once(self):
        names = chapter.speaker_names()
        for episode in range(1, 17):
            self.quest = chapter.FIRST_QUEST + episode - 1
            self.tick(0)
            for phase in (0, 1):
                self.tick(phase + 1)
                rows = [r for r in self.bundle['report']['entries']
                        if r['episode'] == episode and r['objective'] == phase]
                for row in rows:
                    self.assertEqual(self.shown[-1], (self.quest, phase, row['speech'],
                                     names[row['npc']], 'interface\\NPC_%d_0.eff' % row['npc']))
                    win = self.get(chapter.OUR_WINDOW)
                    before = bytes(self.m.mem_read(win, chapter.WINDOW_SIZE))
                    for _ in range(4):
                        self.tick()
                    self.assertEqual(bytes(self.m.mem_read(win, chapter.WINDOW_SIZE)), before)
                    self.assertEqual(bytes(self.m.mem_read(win + 0x218, 16)), b'Z' * 16)
                    self.finish_window()
                    self.tick()
                self.assertEqual(self.get(chapter.OUR_WINDOW), 0)
                self.assertEqual(self.get(chapter.ACTIVE_QUEST), 0)
                for _ in range(20):
                    self.tick()
        self.assertEqual(len(self.shown), 107)

    def test_first_pushed_phase_one_uses_entrance_not_exit(self):
        self.tick(1)
        self.assertEqual(self.shown[0][:3], (self.quest, 0, 0))

    def test_manual_book_and_reconnect_phase_two_do_not_play(self):
        self.tick(1, pushed=False)
        self.tick(1)
        self.assertFalse(self.shown)
        self.quest += 1
        self.tick(2)
        self.assertFalse(self.shown)

    def test_new_quest_at_same_phase_is_not_suppressed(self):
        self.tick(1)
        self.finish_window()
        self.tick()
        self.quest += 1
        self.tick(1)
        self.assertEqual(self.shown[-1][:3], (self.quest, 0, 0))

    def test_existing_task_book_does_not_block_a_real_phase_transition(self):
        self.tick(0, pushed=False)
        self.tick(1, pushed=False)
        self.assertEqual(self.shown[-1][:3], (self.quest, 0, 0))

    def test_transient_record_absence_does_not_replay_completed_entrance(self):
        self.tick(1)
        self.finish_window()
        self.tick()
        quest = self.quest
        self.quest = 0
        self.tick()
        self.quest = quest
        self.tick()
        self.assertEqual(len(self.shown), 1)

    def test_task_change_does_not_replace_open_speech(self):
        self.quest += 2
        self.tick(0)
        self.tick(1)
        first = self.get(chapter.OUR_WINDOW)
        self.tick(2)
        self.assertEqual(self.get(chapter.OUR_WINDOW), first)
        self.finish_window()
        self.tick()
        self.assertEqual(self.shown[-1][:3], (self.quest, 0, 1))
        self.finish_window()
        self.tick()
        self.tick()
        self.assertEqual(self.shown[-1][:3], (self.quest, 1, 0))

    def test_missing_record_does_not_drop_active_remaining_segments(self):
        self.quest += 2
        self.tick(1)
        active = self.quest
        self.quest = 0
        self.finish_window()
        self.tick()
        self.assertEqual(self.shown[-1][:3], (active, 0, 1))

    def test_failed_allocation_retries_without_skipping_speech(self):
        self.fail_alloc = True
        self.tick(1)
        self.assertFalse(self.shown)
        self.fail_alloc = False
        self.tick()
        self.assertEqual(self.shown[-1][:3], (self.quest, 0, 0))

    def test_missing_table_match_never_falls_back_to_first_chapter(self):
        self.put(chapter.QUEST_COUNT, 17)
        self.quest += 16
        self.tick(1)
        self.assertFalse(self.shown)
        self.assertEqual(self.get(chapter.ACTIVE_QUEST), 0)


class BoundaryTests(unittest.TestCase):
    def test_full_names_fit_and_round_trip_without_truncation(self):
        for name in chapter.speaker_names().values():
            raw = chapter.encoded_name(name)
            self.assertEqual(len(raw), 24)
            self.assertEqual(raw.split(b'\0')[0].decode('gbk'), name)
        self.assertEqual(chapter.speaker_names()[55], '坏掉的斯巴特洛与手下')
        self.assertEqual(len(chapter.encoded_name('中' * 11)), 24)
        for name in ('a' * 24, 'a\0b', ''):
            with self.assertRaises(ValueError):
                chapter.encoded_name(name)

    def test_text_rows_always_have_a_terminator(self):
        for text in ('a' * 153, '中' * 60, '中' * 25 + 'a中'):
            for page in chapter.pages(text):
                for offset in range(0, 204, 51):
                    row = page[offset:offset + 51]
                    self.assertIn(0, row)
                    row.split(b'\0')[0].decode('gbk')

    def test_duplicate_and_gapped_speech_indices_are_rejected(self):
        for indices in ((0, 0), (0, 2), (1,)):
            with self.assertRaises(ValueError):
                chapter.entries(TEXTS, [dict(TRIGGERS[0], speech_index=i) for i in indices])


if __name__ == '__main__':
    unittest.main()
