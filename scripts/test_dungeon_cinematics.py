"""Sprite boundaries, release coverage and cinematic installation transactions."""
from dataclasses import replace
import json
import os
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

import cinematic_codec as codec
import dungeon_cinematics as cinema
import port_lumineos_resources as io


class SpriteTests(unittest.TestCase):
    def sprite(self, fmt=10):
        return codec.Sprite(7, 3, 7, 3, fmt, b'', struct.pack('<2f', 3.5, 1.5),
                            (0, 0xffff, 0, 0x8123, 0x1234, 0, 0xffff,
                             0, 0, 0, 0, 0, 0, 0,
                             0xffff, 0xffff, 0xffff, 0xffff, 0xffff, 0xffff, 0xffff))

    def test_sparse_relative_gaps_transparent_full_and_odd_width(self):
        for fmt in (8, 10):
            with self.subTest(fmt=fmt):
                sprite = self.sprite(fmt)
                raw = codec.encode_im3(sprite)
                self.assertEqual(codec.decode_im3(raw), sprite)
                self.assertEqual(codec.encode_im3(codec.decode_im3(raw)), raw)

    def test_atlas_metadata_and_pivot_preserved(self):
        sprite = replace(self.sprite(), width=7, height=6, frame_height=3,
                         sequences=struct.pack('<5I', 1, 2, 3, 4, 5), pixels=self.sprite().pixels * 2)
        self.assertEqual(codec.decode_im3(codec.encode_im3(sprite)), sprite)

    def test_quantization_alpha_and_channel_extremes(self):
        for fmt in (8, 10):
            self.assertEqual(codec.pack_pixel((255, 255, 255, 255), fmt), 0xffff)
            self.assertEqual(codec.pack_pixel((255, 255, 255, 0), fmt), 0)
            self.assertEqual(codec.unpack_pixel(codec.pack_pixel((0, 0, 0, 255), fmt), fmt), (0, 0, 0, 255))
        self.assertEqual(codec.unpack_pixel(codec.pack_pixel((85, 170, 255, 119), 10), 10), (85, 170, 255, 119))
        self.assertEqual(codec.pack_pixel((255, 0, 0, 127), 8), 0)
        self.assertEqual(codec.pack_pixel((255, 0, 0, 128), 8), 0xfc00)

    def test_all_truncations_and_trailing_bytes(self):
        raw = codec.encode_im3(self.sprite())
        for end in range(len(raw)):
            with self.subTest(end=end), self.assertRaises(ValueError):
                codec.decode_im3(raw[:end])
        trailing = bytearray(raw + b'xx')
        struct.pack_into('<I', trailing, 8, len(trailing) - 8)
        with self.assertRaises(ValueError):
            codec.decode_im3(trailing)

    def test_bad_format_geometry_run_lengths_and_scanline(self):
        raw = codec.encode_im3(self.sprite())
        for offset, fmt, value in ((28, '<I', 9), (12, '<I', 0), (20, '<I', 3),
                                   (44, '<H', 1), (46, '<h', -2), (48, '<H', 65535),
                                   (50, '<H', 65535)):
            damaged = bytearray(raw)
            struct.pack_into(fmt, damaged, offset, value)
            with self.subTest(offset=offset), self.assertRaises(ValueError):
                codec.decode_im3(damaged)


class ReleaseTests(unittest.TestCase):
    def test_export_scope(self):
        import export_patch
        for name in ('manifest/dungeon_cinematics.json', 'manifest/dungeon_cinematics_coverage.json',
                     'manifest/dungeon_cinematics_translations.tsv', 'manifest/dungeon_cinematics_revisions.tsv',
                     'manifest/dungeon_cinematics_terms.json'):
            export_patch.payload_policy(name, 'source')
        with self.assertRaises(export_patch.ExportError):
            export_patch.payload_policy('manifest/dungeon_cinematics_private.tsv', 'source')
        with self.assertRaises(export_patch.ExportError):
            export_patch.payload_policy('resource-packs/nanaimo-dungeon-cinematics-kit.zip', 'source')

    def test_coverage_and_pinned_translation_inputs(self):
        recipe = io.load(cinema.RECIPE)
        cinema.validate(recipe)
        self.assertEqual(recipe['summary']['restored_timelines'], 64)
        rows = cinema.translations()
        self.assertEqual(len([k for k in rows if int(k[2:4]) >= 16]), 144)
        for name, digest in recipe['translation_inputs'].items():
            self.assertEqual(io.file_hash(cinema.ROOT / name), digest)
        for row in recipe['dialogues']:
            if row['resource_episode'] >= 16:
                self.assertTrue(row['korean_transcript'])
                self.assertTrue(row['chinese_translation'])
                self.assertFalse(row['retained_cn_image'])
                self.assertEqual(row['layout']['font_size'], cinema.FONT_SIZE)
                self.assertFalse(row['runtime_acceptance'])

    def test_all_dialogues_use_one_face_size_and_screen_safe_bounds(self):
        recipe, payload = cinema.bundle(cinema.ROOT / 'resource-packs/nanaimo-dungeon-cinematics-kit.zip')
        texts = cinema.translations()
        self.assertEqual(len(texts), 720)
        self.assertEqual(recipe['summary']['fixed_font_dialogues'], 720)
        dialogues = {row['path']: row for row in recipe['dialogues']}
        for row in dialogues.values():
            self.assertFalse(row['retained_cn_image'])
            layout = row['layout']
            self.assertEqual(layout['font_size'], 20)
            self.assertEqual(''.join(layout['text_lines']), texts[row['id']]['chinese_translation'])
            sprite = codec.decode_im3(payload[row['path']])
            self.assertEqual(list(sprite.rgba().getbbox()), layout['bbox'])
        for row in recipe['effects']:
            effect = codec.decode_eff(payload[row['path']])
            for detail in row['dialogue_layers']:
                layer = effect['layers'][detail['layer']]
                actual = cinema.screen_bounds(layer, dialogues[detail['path']]['layout'], effect['duration'])
                self.assertEqual(actual, {key: detail[key] for key in actual})

    def test_canonical_monster_names_and_episode16_destination(self):
        names = {row['text_id']: row['chinese'] for row in cinema.terms()}
        self.assertEqual(names[2314], '枪弹新兵')
        self.assertEqual(names[2315], '旋转弹药发射机')
        self.assertEqual(names[2302], '火焰的石柱')
        self.assertEqual(names[2102], '不死神坛')
        texts = cinema.translations()
        self.assertIn(names[2314], texts['ep15_dg01_st00_intro_B_T_02']['chinese_translation'])
        self.assertIn(names[2315], texts['ep15_dg02_st00_intro_B_T_02']['chinese_translation'])
        self.assertIn(names[2302], texts['ep11_dg02_st00_intro_B_T_00']['chinese_translation'])
        self.assertEqual(texts['ep15_dg02_st01_intro_F_T_11']['chinese_translation'],
                         '唉……看来还得去拉米诺斯村。')

    def test_all_effects_retain_authored_timing_and_layer_counts(self):
        recipe = io.load(cinema.RECIPE)
        self.assertEqual(len(recipe['effects']), 160)
        for row in recipe['effects']:
            before = row['phase'] == 'before'
            self.assertEqual(row['duration'], 730)
            self.assertEqual(row['layers'], 27 if before else 26)
            self.assertEqual(len(row['dialogue_layers']), 5 if before else 4)

    def test_every_before_effect_has_a_placed_event(self):
        recipe = io.load(cinema.RECIPE)
        coverage = io.load(cinema.ROOT / 'manifest/dungeon_cinematics_coverage.json')
        self.assertEqual(coverage['recipe_id'], recipe['id'])
        expected = {Path(r['path']).name for r in recipe['effects'] if r['phase'] == 'before'}
        placed = {event['effect'] for stage in coverage['stages']
                  for slot in stage['slots'] for event in slot['events']}
        self.assertTrue(expected <= placed)
        self.assertEqual(len(expected), 80)
        self.assertFalse(coverage['resource_effects_without_stage_reference'])

    def test_kit_identity_and_every_resource_parse(self):
        recipe, payload = cinema.bundle(cinema.ROOT / 'resource-packs/nanaimo-dungeon-cinematics-kit.zip')
        self.assertEqual(len(payload), recipe['summary']['resources'])
        for row in recipe['files']:
            self.assertEqual(io.digest(payload[row['path']]), row['sha256'])

    def test_recipe_tamper_rejected(self):
        recipe = io.load(cinema.RECIPE)
        recipe['files'][0]['sha256'] = '0' * 64
        with self.assertRaises(ValueError):
            cinema.validate(recipe)

    def test_stage_corrections_reconstruct_exact_baselines(self):
        recipe, payload = cinema.bundle(cinema.ROOT / 'resource-packs/nanaimo-dungeon-cinematics-kit.zip')
        for row in recipe['files']:
            if row['path'] not in cinema.STAGE_FIXES:
                continue
            corrected = payload[row['path']]
            plain = bytearray(v ^ 40 for v in corrected)
            offset = row['correction']['field_offset']
            plain[offset:offset + len(row['correction']['before'])] = row['correction']['before'].encode('ascii')
            original = bytes(v ^ 40 for v in plain)
            self.assertEqual(cinema.identity(original), row['accepted_before'][0])
            actual, detail = cinema.patch_stage(original, *cinema.STAGE_FIXES[row['path']])
            self.assertEqual(actual, corrected)
            self.assertEqual(len(detail['changed_bytes']), 1)
            self.assertEqual(actual[:offset], original[:offset])
            self.assertEqual(actual[offset + 260:], original[offset + 260:])


class TransactionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / 'client'
        self.root.mkdir()
        self.payload = {cinema.REL + 'first.im3':b'candidate-one', cinema.REL + 'second.im3':b'candidate-two'}
        self.before = {name:b'baseline-' + name.encode('ascii') for name in self.payload}
        self.recipe = dict(schema=cinema.SCHEMA, id='1' * 64, runtime_acceptance=False, files=[
            dict(path=name, **cinema.identity(raw), accepted_before=[cinema.identity(self.before[name])])
            for name, raw in self.payload.items()])
        self.recipe_path = Path(self.temp.name) / 'recipe.json'
        io.atomic(self.recipe_path, io.encoded(self.recipe))
        for name, raw in self.before.items():
            io.atomic(io.safe(self.root, name), raw)
        for target, value in (('RECIPE', self.recipe_path),):
            mock = patch.object(cinema, target, value)
            mock.start()
            self.addCleanup(mock.stop)
        mock = patch.object(cinema, 'validate')
        mock.start()
        self.addCleanup(mock.stop)
        mock = patch.object(cinema, 'bundle', side_effect=lambda _: (self.recipe, dict(self.payload)))
        mock.start()
        self.addCleanup(mock.stop)
        mock = patch.object(io, 'assert_not_running')
        mock.start()
        self.addCleanup(mock.stop)

    def check_original(self):
        for name, raw in self.before.items():
            self.assertEqual(io.safe(self.root, name).read_bytes(), raw)

    def test_install_idempotence_and_exact_rollback(self):
        result = cinema.apply('fixture', self.root)
        self.assertEqual(result['changed'], 2)
        self.assertEqual(cinema.apply('fixture', self.root)['status'], 'already-installed')
        cinema.restore(self.root, result['receipt'])
        self.check_original()
        self.assertFalse(io.safe(self.root, cinema.MARKER).exists())
        self.assertEqual(cinema.restore(self.root, result['receipt'])['status'], 'already-rolled-back')

    def previous_marker(self):
        self.recipe['upgrade_from'] = '2' * 64
        io.atomic(self.recipe_path, io.encoded(self.recipe))
        receipt = cinema.BACKUPS + '/cinematics-' + '0' * 32 + '/receipt.json'
        io.atomic(io.safe(self.root, receipt), io.encoded(dict(schema=cinema.SCHEMA,
            id=self.recipe['upgrade_from'], client=str(self.root), receipt=receipt,
            status='installed', files=[])))
        raw = io.encoded(dict(schema=cinema.SCHEMA, id=self.recipe['upgrade_from'], receipt=receipt))
        io.atomic(self.root / cinema.MARKER, raw)
        return raw

    def test_upgrade_and_rollback_restore_previous_installation_identity(self):
        marker = self.previous_marker()
        result = cinema.apply('fixture', self.root)
        self.assertEqual(result['status'], 'installed')
        self.assertEqual(cinema.apply('fixture', self.root)['status'], 'already-installed')
        cinema.restore(self.root, result['receipt'])
        self.check_original()
        self.assertEqual((self.root / cinema.MARKER).read_bytes(), marker)

    def test_upgrade_rejects_partially_replaced_previous_release(self):
        self.previous_marker()
        name = next(iter(self.payload))
        io.atomic(io.safe(self.root, name), self.payload[name])
        with self.assertRaisesRegex(ValueError, 'Previous cinematic release differs'):
            cinema.apply('fixture', self.root)


    def test_unknown_target_refused_before_any_write(self):
        name = list(self.before)[1]
        io.safe(self.root, name).write_bytes(b'custom')
        with self.assertRaises(ValueError):
            cinema.apply('fixture', self.root)
        self.assertEqual(io.safe(self.root, list(self.before)[0]).read_bytes(), self.before[list(self.before)[0]])
        self.assertFalse(io.safe(self.root, cinema.MARKER).exists())

    def test_failure_during_install_restores_all_bytes(self):
        original = io.atomic
        failed = False
        def interrupted(path, data):
            nonlocal failed
            if path == io.safe(self.root, list(self.payload)[1]) and not failed:
                failed = True
                raise OSError('simulated atomic replace failure')
            return original(path, data)
        with patch.object(io, 'atomic', side_effect=interrupted), self.assertRaises(OSError):
            cinema.apply('fixture', self.root)
        self.check_original()
        self.assertFalse(io.safe(self.root, cinema.MARKER).exists())

    def test_user_edits_and_damaged_backup_block_full_rollback(self):
        result = cinema.apply('fixture', self.root)
        name = list(self.payload)[1]
        io.safe(self.root, name).write_bytes(b'edited-after-install')
        with self.assertRaises(ValueError):
            cinema.restore(self.root, result['receipt'])
        self.assertEqual(io.safe(self.root, list(self.payload)[0]).read_bytes(), self.payload[list(self.payload)[0]])
        io.safe(self.root, name).write_bytes(self.payload[name])
        journal = cinema.validate_receipt(self.root, result['receipt'])
        io.safe(self.root, journal['files'][0]['backup']).write_bytes(b'corrupt')
        with self.assertRaises(ValueError):
            cinema.restore(self.root, result['receipt'])

    def test_interrupted_journal_recovery(self):
        result = cinema.apply('fixture', self.root)
        journal = cinema.validate_receipt(self.root, result['receipt'])
        journal['status'] = 'applying'
        io.atomic(io.safe(self.root, result['receipt']), io.encoded(journal))
        with self.assertRaises(ValueError):
            cinema.apply('fixture', self.root)
        cinema.restore(self.root, result['receipt'])
        self.check_original()

    def test_path_and_receipt_ownership_protection(self):
        result = cinema.apply('fixture', self.root)
        with self.assertRaises(ValueError):
            cinema.validate_receipt(self.root, '../receipt.json')
        journal = cinema.validate_receipt(self.root, result['receipt'])
        journal['files'][0]['backup'] = '../outside'
        io.atomic(io.safe(self.root, result['receipt']), io.encoded(journal))
        with self.assertRaises(ValueError):
            cinema.restore(self.root, result['receipt'])


class DialogueLayoutTests(unittest.TestCase):
    def font_face(self):
        from PIL import ImageFont
        font = Path(os.environ.get('WINDIR', '')) / 'Fonts' / 'simsun.ttc'
        if not font.is_file():
            self.skipTest('The separately supplied cinematic font is required')
        return ImageFont.truetype(str(font), 20)

    def effect(self):
        import copy
        tracks = [[(0.0, 0.0, 0, 20)] for _ in range(12)]
        tracks[5] = tracks[6] = [(2.0, -0.0625, 0, 20)]
        tracks[7] = [(255.0, 0.0, 0, 20)]
        def layer(index, name):
            return dict(index=index, name=name, srcblend=5, dstblend=2,
                        tracks=copy.deepcopy(tracks), info=bytes(48).hex())
        return dict(duration=20, unknown=0, layers=[layer(0, 'ep15_dg00_st00_intro_F_T_00.im3'),
                    layer(1, 'ep15_dg00_st00_intro_B_T_00.im3'), layer(2, 'portrait.im3')])

    def test_effect_roundtrip_and_only_text_position_scale_change(self):
        before = self.effect()
        raw = codec.encode_eff(before)
        self.assertEqual(codec.encode_eff(codec.decode_eff(raw)), raw)
        after = codec.decode_eff(cinema.normalize_dialogue_layers(raw))
        self.assertEqual(after['layers'][2], before['layers'][2])
        for index, x in ((0, -200.0), (1, 200.0)):
            self.assertEqual(after['layers'][index]['tracks'][0], [(x, 0.0, 0, 20)])
            for track in range(12):
                if track not in (0, 5, 6):
                    self.assertEqual(after['layers'][index]['tracks'][track],
                                     before['layers'][index]['tracks'][track])

    def test_screen_check_rejects_offscreen_even_with_unclipped_sprite(self):
        layer = self.effect()['layers'][0]
        layer['tracks'][0] = [(-390.0, 0.0, 0, 20)]
        layer['tracks'][5] = layer['tracks'][6] = [(1.0, 0.0, 0, 20)]
        layout = dict(canvas=[352, 28], bbox=[4, 4, 348, 24])
        with self.assertRaisesRegex(ValueError, 'safe viewport'):
            cinema.screen_bounds(layer, layout, 20)

    def test_wrap_preserves_text_names_and_punctuation(self):
        face = self.font_face()
        text = '旋转弹药发射机，把那家伙打下来！'
        lines = cinema.wrap_dialogue(text, face)
        self.assertEqual(''.join(lines), text)
        self.assertTrue(any('旋转弹药发射机' in line for line in lines))
        self.assertTrue(all(line[0] not in '，。！？…' for line in lines))

    def test_wrap_rejects_more_than_two_lines(self):
        face = self.font_face()
        with self.assertRaisesRegex(ValueError, 'shortening'):
            cinema.wrap_dialogue('很长的对白' * 30, face)

    def test_offline_html_embeds_data_without_external_assets(self):
        import preview_dungeon_cinematics as preview
        from html.parser import HTMLParser
        class Assets(HTMLParser):
            external = []
            def handle_starttag(self, tag, attributes):
                for name, value in attributes:
                    if name in ('src', 'href') and value and not value.startswith(('data:', '#')):
                        self.external.append(value)
        raw = preview.html_document(dict(recipe_id='a' * 64, text='</script>'))
        parser = Assets()
        parser.feed(raw.decode('utf-8'))
        self.assertFalse(parser.external)
        self.assertIn(b'\\u003c/script>', raw)
        self.assertNotIn(b'__DATA__', raw)


if __name__ == '__main__':
    unittest.main()
