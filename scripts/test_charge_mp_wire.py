"""Validate production worker dispatch and F102 resource snapshots."""
import importlib.util
import os
from pathlib import Path
import socket
import struct
import subprocess
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('native_social_support', ROOT/'tests/SocialRegression/test_native_social.py')
support = importlib.util.module_from_spec(spec)
spec.loader.exec_module(support)


class ChargeMpWireTests(unittest.TestCase):
    def test_firing_debits_worker_snapshot_without_hit_or_self_refresh(self):
        port = None
        for candidate in range(64000, 65000, 10):
            reserved = []
            try:
                for value in range(candidate, candidate + 9):
                    item = socket.socket()
                    reserved.append(item)
                    item.bind(('127.0.0.1', value))
                port = candidate
                break
            except OSError:
                pass
            finally:
                for item in reserved:
                    item.close()
        self.assertIsNotNone(port)
        with tempfile.TemporaryDirectory(prefix='nanaimo-charge-wire-') as directory:
            work = Path(directory)
            bridge = Path(os.environ['NANAIMO_CHARGE_BRIDGE']).resolve() if 'NANAIMO_CHARGE_BRIDGE' in os.environ else work/'bridge.exe'
            if 'NANAIMO_CHARGE_BRIDGE' not in os.environ:
                subprocess.run([str(ROOT/'tools/tcc/tcc.exe'), '-I', str(ROOT),
                                str(ROOT/'adapter/nanaimo_gameplay_bridge.c'), '-o', str(bridge)],
                               check=True, capture_output=True, timeout=120)
            profile = work/'profile.ini'
            profile.write_text('version=2\nname_hex=436861726765\nlevel=1\npet=0\nhp_max=2000\nhp_current=2000\nmp_max=2760\nmp_current=2760\n', encoding='ascii')
            connection = None
            with (work/'native.txt').open('wb') as output:
                process = subprocess.Popen([str(bridge), str(port+8), '0', '0', '0', str(profile), str(port)],
                                           cwd=work, stdout=output, stderr=output,
                                           creationflags=subprocess.CREATE_NO_WINDOW)
                try:
                    deadline = time.monotonic()+10
                    while connection is None:
                        try:
                            connection = socket.create_connection(('127.0.0.1', port), timeout=3)
                        except OSError:
                            if time.monotonic() >= deadline:
                                raise
                            time.sleep(0.05)
                    connection.settimeout(5)

                    def exchange(op, payload, reply):
                        connection.sendall(support.frame(op, payload))
                        return support.receive(connection, reply)

                    def snapshot():
                        captured = []
                        connection.sendall(support.frame(0xF101))
                        response = support.receive(connection, 0xF102, captured)
                        self.assertEqual([struct.unpack_from('<H', p, 6)[0] for p in captured], [0xF102])
                        return struct.unpack_from('<I', response, 8+28)[0]

                    def shot(kind, mp, tick):
                        payload = bytearray(32)
                        struct.pack_into('<I', payload, 0, 4)
                        payload[8] = kind
                        struct.pack_into('<H', payload, 14, mp)
                        struct.pack_into('<I', payload, 20, tick)
                        connection.sendall(support.frame(0x044C, payload))

                    state = bytearray(support.seed(4, 0))
                    for offset, value in ((20, 2000), (24, 2760), (28, 2760)):
                        struct.pack_into('<I', state, offset, value)
                    exchange(0xF100, state, 0xF102)
                    shot(20, 0, 1)
                    self.assertEqual(snapshot(), 2760)  # Before the battle barrier.
                    selection = bytearray(32)
                    selection[26] = 1
                    exchange(0xCF6C, selection, 0xCF6D)
                    exchange(0xC587, b'', 0xC588)
                    exchange(0xCF70, bytes(4), 0xCF71)
                    exchange(0xCFD9, b'', 0xCFDA)
                    exchange(0xCFEB, bytes(4), 0xCFEC)
                    exchange(0xCFD3, b'', 0xCFD4)
                    exchange(0xCFD5, struct.pack('<I', 1), 0xCFD6)
                    exchange(0xCF7F, b'', 0xCF80)
                    shot(20, 2716, 100)
                    self.assertEqual(snapshot(), 2716)  # Charged miss, no target report.
                    shot(20, 2716, 100)
                    shot(20, 0, 99)
                    self.assertEqual(snapshot(), 2716)
                    for kind in (10, 30, 70):
                        shot(kind, 0, 101)
                    shot(20, 65535, 102)
                    self.assertEqual(snapshot(), 2716)
                    shot(20, 2672, 120)
                    self.assertEqual(snapshot(), 2672)
                    connection.sendall(support.frame(0xCF73))
                    shot(20, 0, 130)
                    self.assertEqual(snapshot(), 2672)  # Leaving disarms resource spending.
                except Exception:
                    print((work/'native.txt').read_text('utf8', errors='replace')[-12000:])
                    raise
                finally:
                    if connection is not None:
                        connection.close()
                    process.terminate()
                    process.wait(timeout=10)
                    if 'NANAIMO_CHARGE_EVIDENCE' in os.environ:
                        import shutil
                        shutil.copyfile(work/'native.txt', os.environ['NANAIMO_CHARGE_EVIDENCE'])


if __name__ == '__main__':
    unittest.main(verbosity=2)
