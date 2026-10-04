import hashlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import prepare_entertainment_resources as resources


class EntertainmentResourceTests(unittest.TestCase):
    def test_install_verify_and_preserve(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root)
            source, target, overlay = root / 'source', root / 'client', root / 'overlay'
            source.mkdir(); target.mkdir()
            data = bytes(range(217)) * 4
            (source / resources.STATE_NAME).write_bytes(data)
            (target / 'game.exe').write_bytes(b'client')
            with patch.object(resources, 'STATE_SHA256', hashlib.sha256(data).hexdigest()):
                report = resources.install(source, target, overlay)
                self.assertEqual(report['action'], 'install')
                self.assertFalse((target / resources.STATE_NAME).exists())
                self.assertFalse(overlay.exists())
                self.assertTrue(resources.install(source, target, overlay, apply=True)['applied'])
                self.assertEqual((target / resources.STATE_NAME).read_bytes(), data)
                self.assertEqual(resources.install(source, target, overlay, apply=True)['action'], 'verified')
                self.assertEqual((target / 'game.exe').read_bytes(), b'client')
                (target / resources.STATE_NAME).write_bytes(b'custom')
                with self.assertRaises(ValueError):
                    resources.install(source, target, overlay, apply=True)
                self.assertEqual((target / resources.STATE_NAME).read_bytes(), b'custom')

    def test_reject_unknown_or_truncated(self):
        for data in (b'', bytes(868), bytes(869)):
            with self.subTest(length=len(data)), self.assertRaises(ValueError):
                resources.validate_state(data)


if __name__ == '__main__':
    unittest.main()
