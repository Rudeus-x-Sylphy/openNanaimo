import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
POLICY = ROOT / "release/components/dungeon_progression/settlement_result_policy.inc"
TCC_CANDIDATES = [ROOT / "tools/tcc/tcc.exe"]

class DungeonSettlementPolicyTests(unittest.TestCase):
    def test_death_rating_and_clear_rating_are_distinct(self):
        tcc = next((path for path in TCC_CANDIDATES if path.is_file()), None)
        self.assertIsNotNone(tcc)
        with tempfile.TemporaryDirectory(prefix="settlement-policy-") as temp:
            source = Path(temp) / "check.c"
            exe = Path(temp) / "check.exe"
            source.write_text(
                '#include <assert.h>\n#include "' + POLICY.as_posix() + '"\n'
                'int main(void){assert(dungeon_settlement_visible_rating(0u)==0u);'
                'assert(dungeon_settlement_visible_rating(1u)==5u);return 0;}\n',
                encoding="utf-8")
            subprocess.run([str(tcc), str(source), "-o", str(exe)], check=True)
            subprocess.run([str(exe)], check=True)

    def test_native_cf88_uses_failure_rating_when_progress_is_disallowed(self):
        text = (ROOT / "release/components/protocol_extensions/protocol_overrides.inc").read_text("utf-8")
        self.assertIn('p[off+0x0B]=(char)dungeon_settlement_visible_rating(allow_progress);', text)
        self.assertNotIn('p[off+0x0B]=5;', text)

if __name__ == "__main__":
    unittest.main()
