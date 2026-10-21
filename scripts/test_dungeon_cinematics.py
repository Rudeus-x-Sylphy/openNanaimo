"""Sprite boundaries, release coverage and cinematic installation transactions."""
from dataclasses import replace
import json
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
                     'manifest/dungeon_cinematics_translations.tsv', 'manifest/dungeon_cinematics_revisions.tsv'):
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
                self.assertIn(row['layout']['font_size'], range(14, 21))
                self.assertFalse(row['runtime_acceptance'])

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


if __name__ == '__main__':
    unittest.main()
