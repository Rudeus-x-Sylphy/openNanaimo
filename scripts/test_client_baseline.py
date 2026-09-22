"""Regression gates for the fixed client and native adapter/UI integration."""
import re
import unittest
from pathlib import Path
from verify_client_baseline import verify, read_va, CLIENT_SHA256, PATCHED_CLIENT_SHA256, NATIVE_WINDOWS

PATCHED_CLIENT_SHA256 = 'FEF34EE03DA60BD86975B453D013AE8593AF086B0EA79413466C43B58EC19DDC'

ROOT = Path(__file__).resolve().parents[1]


class ClientBaselineTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        path = ROOT / 'game.exe'
        if not path.is_file():
            raise unittest.SkipTest('source-only tree: externally supplied client absent')
        cls.data = path.read_bytes()

    def test_exact_client(self):
        self.assertEqual(verify(self.data)['sha256'], CLIENT_SHA256)

    def test_native_windows(self):
        for va, expected in NATIVE_WINDOWS.items():
            with self.subTest(va=hex(va)):
                self.assertEqual(read_va(self.data, va, len(expected)), expected)

    def test_original_padding(self):
        self.assertEqual(read_va(self.data, 0xB98000, 0x2000), b'\xCC' * 0x2000)

    def test_reject_mutated_client(self):
        changed = bytearray(self.data)
        changed[0x11760] ^= 1
        with self.assertRaises(ValueError):
            verify(changed)

    def test_reject_truncated_client(self):
        with self.assertRaises(ValueError):
            verify(self.data[:-1])

    def test_unbacked_va_rejected(self):
        with self.assertRaises(ValueError):
            read_va(self.data, 0, 8)


class NativeIntegrationTests(unittest.TestCase):
    def test_launcher_and_connector_pin_baseline(self):
        for name in ['nanaimo_launcher.ps1', 'client_connect.ps1']:
            path = ROOT / 'gui_launcher' / name
            if not path.is_file():
                self.skipTest('source-only tree: launcher runtime not supplied')
            text = path.read_text('utf-8-sig')
            self.assertIn("$ExpectedClientHash='" + CLIENT_SHA256 + "'", text)
            self.assertIn(PATCHED_CLIENT_SHA256, text)
            self.assertNotRegex(text, r'(?i)flamethrower|Projectile DIY|Get-FlamethrowerSelection|projectileReverseBox|projectilePreview')

    def test_adapter_keeps_native_combat_resolution(self):
        adapter = (ROOT / 'release/components/adapter_core/teamplay_adapter.inc').read_text('utf8')
        runtime = (ROOT / 'release/components/game_session/gs_runtime.inc').read_text('utf8')
        self.assertIn('projectile_apply_effective_attack(projectile_ctx,&atk)', adapter)
        self.assertIn('stable_monster_hp_resolve_candidate_attack(owner_key,source_index,&atk)', adapter)
        self.assertIn('projectile_resolve_strict_source(boss_owner,boss_source,&boss_attack)', runtime)
        for p in (ROOT / 'release').rglob('*.inc'):
            self.assertNotRegex(p.read_text('utf8'), r'(?i)adapter_core_diy|g_diy_projectile|flamethrower')

    def test_no_retired_resource_dependencies(self):
        for name in ['projectile_client_compat.json', 'projectile_pon_catalog.json']:
            self.assertFalse((ROOT / 'gui_launcher/data' / name).exists())
        self.assertFalse(any((ROOT / 'flying/pon').glob('nanaimo_*.pon')))
        self.assertFalse((ROOT / 'gui_launcher/data/previews/projectiles').exists())


if __name__ == '__main__':
    unittest.main()
