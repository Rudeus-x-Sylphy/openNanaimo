"""Resource provenance and deterministic native generation checks (not client acceptance)."""
from pathlib import Path
import unittest
from scripts.generate_avatar_resource_effects import render

ROOT = Path(__file__).resolve().parents[1]


class AvatarResourceCatalogTests(unittest.TestCase):
    def test_checked_in_native_catalog_matches_managed_runtime_resource(self):
        resource = next((ROOT / 'adapter_runtime').rglob('ava._D1'), None)
        if resource is None:
            self.skipTest('local client catalog is not installed')
        try:
            import Crypto.Cipher.AES  # noqa: F401
        except ImportError:
            self.skipTest('offline AES validation requires PyCryptodome')
        expected = render(resource)
        actual = (ROOT / 'release/components/inventory_instances/avatar_resource_data.inc').read_text('utf8')
        self.assertEqual(actual, expected)
        self.assertIn('{10110337u,0u,200u,0u,300u}', actual)
        self.assertIn('{10110062u,930u,0u,155u,0u}', actual)
        self.assertNotIn('{10150103u,', actual)  # butterfly wings: defense, not HP or MP


if __name__ == '__main__':
    unittest.main()
