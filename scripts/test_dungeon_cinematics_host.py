"""Exercise the published embedded cinematic verifier on complete isolated resources."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

import dungeon_cinematics as cinema
import port_lumineos_resources as io


class HostCinematicTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tool = Path(os.environ.get('NANAIMO_CINEMATICS_ADAPTER', cinema.ROOT / 'adapter_runtime/Nanaimo.Adapter.exe'))
        cls.recipe, cls.payload = cinema.bundle(cinema.ROOT / 'resource-packs/nanaimo-dungeon-cinematics-kit.zip')

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='nanaimo-cinematic-host-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for name, raw in self.payload.items():
            io.atomic(io.safe(self.root, name), raw)
        self.marker = dict(schema=cinema.SCHEMA, id=self.recipe['id'], runtime_acceptance=False)
        io.atomic(self.root / cinema.MARKER, io.encoded(self.marker))

    def run_tool(self, kind):
        return subprocess.run([str(self.tool), '--tools', 'verify-resources', '--kind', kind,
                               '--client-root', str(self.root)], capture_output=True, text=True,
                              encoding='utf8', timeout=60,
                              creationflags=subprocess.CREATE_NO_WINDOW if os.name == 'nt' else 0)

    def test_full_release_and_damaged_resource(self):
        result = self.run_tool('cinematics')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('CINEMATIC_RESOURCES_PASS', result.stdout)
        name = next(r['path'] for r in self.recipe['files'] if r['action'] == 'translate-image')
        io.safe(self.root, name).write_bytes(b'customized')
        self.assertNotEqual(self.run_tool('cinematics').returncode, 0)

    def test_forged_marker_rejected(self):
        self.marker['id'] = '0' * 64
        io.atomic(self.root / cinema.MARKER, io.encoded(self.marker))
        self.assertNotEqual(self.run_tool('cinematics').returncode, 0)

    def test_l8_parent_and_exact_cinematic_layer(self):
        row = next(r for r in self.recipe['files'] if 'ep23_' in r['path'] and r['action'] == 'translate-image')
        before = row['accepted_before'][0]
        parent = dict(schema=io.SCHEMA, files=[dict(path=row['path'], **before)], preserve_l7_combat=[])
        parent['id'] = io.digest(io.encoded(parent))
        io.atomic(self.root / io.MARKER, io.encoded(parent))
        result = self.run_tool('lumineos')
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        parent.pop('id')
        parent['files'][0]['sha256'] = '0' * 64
        parent['id'] = io.digest(io.encoded(parent))
        io.atomic(self.root / io.MARKER, io.encoded(parent))
        self.assertNotEqual(self.run_tool('lumineos').returncode, 0)


if __name__ == '__main__':
    unittest.main()
