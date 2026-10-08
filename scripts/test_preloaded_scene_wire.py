"""Adapter request-driven scene lifecycle regression for repeated D00D reports."""
import json
import os
from pathlib import Path
import struct
import time
import unittest
import test_social_sync_boundaries as wire
ROOT=Path(__file__).resolve().parents[1]
class PreloadedSceneWireTests(unittest.TestCase):
    def test_well_retained_positive_transform_dies_and_epoch_resets(self):
        trace=[]
        with wire.room(1) as clients:
            c=clients[0]
            def transact(op,payload=b''):
                packet=wire.frame(op,payload);trace.append(dict(direction='C2S',hex=packet.hex()))
                c.sendall(packet);time.sleep(.12);frames=wire.flush(c)
                trace.extend(dict(direction='S2C',hex=f.hex()) for f in frames)
                return frames
            create=bytearray(44);create[0x23-8]=6;create[0x24-8]=2;create[0x26-8]=2
            def begin():
                transact(0xCF6C,create);transact(0xCFEB,bytes(4));transact(0xCF7F)
            def hit(selector):
                frames=transact(0xD00D,struct.pack('<HHHBBI',0,0,1424,1,0,selector))
                self.assertFalse(any(struct.unpack_from('<H',f,6)[0] in (0xD012,0xD013,0xD035,0xCF88) for f in frames))
                replies=[f for f in frames if struct.unpack_from('<H',f,6)[0]==0xD00E]
                self.assertEqual(len(replies),1)
                self.assertEqual(len(replies[0]),48)
                return replies[0]
            begin()
            # Hard slot6: well preload base701; collision rows are +2/+12.
            for selector in (703,713,718,728,733,743,748,758):
                for _ in range(3):
                    result=hit(selector);self.assertEqual(result[0x1A],0)
                    self.assertEqual(result[0x1B:],bytes(21))
            # Positive-HP preload uses its authored 4000 HP, not fallback140.
            result=hit(694);self.assertEqual(result[0x1A],0)
            for _ in range(10):
                result=hit(694)
                if result[0x1A]==200:break
            self.assertEqual(result[0x1A],200)
            self.assertEqual(struct.unpack_from('<H',result,0x18)[0],694)
            self.assertEqual(hit(694)[0x1A],0)
            begin();self.assertEqual(hit(694)[0x1A],0);self.assertEqual(hit(718)[0x1A],0)
        if os.environ.get('NANAIMO_SCENE_TRACE'):
            Path(os.environ['NANAIMO_SCENE_TRACE']).write_text(json.dumps(trace,indent=2),encoding='utf8')
        print('PRELOADED_SCENE_WIRE_PASS retained=24 positive=hp-gated duplicate=blocked epoch=reset')
if __name__=='__main__':unittest.main(verbosity=2)
