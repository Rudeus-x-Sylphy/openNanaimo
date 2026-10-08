"""Bounded source isolation for the page-union deployment; no real installation writes."""
import unittest
from pathlib import Path
import tempfile
import prepare_card_page_update as p
import deploy_card_use_update as d

DB='before\n        await InitializeEventCardUsesAsync(connection, cancellationToken);\nafter'
NET='old-apartment-logic\n        public LuckyCardRequestWindow EventCardRequests { get; } = new();\n                if (unionType == 40)\nVIP'
CAN='unpublished-apartment-change\n                if (unionType == 20)\n                { page_only(); }\n\n                if (unionType == 40)\nVIP'

class PageDeploymentTests(unittest.TestCase):
    def test_preserves_unrelated_installed_code(self):
        db,net=p.overlay(DB,NET,CAN)
        self.assertIn('InitializeCardPageUnionsAsync',db)
        self.assertIn('CardPageRequests',net)
        self.assertIn('page_only()',net)
        self.assertIn('old-apartment-logic',net)
        self.assertNotIn('unpublished-apartment-change',net)
    def test_idempotent_exact_overlay(self):
        db,net=p.overlay(DB,NET,CAN)
        self.assertEqual(p.overlay(db,net,CAN),(db,net))
    def test_missing_or_duplicate_anchor_refused(self):
        for db in ('unknown',DB+DB):
            with self.assertRaises(ValueError):p.overlay(db,NET,CAN)
    def test_changed_existing_handler_refused(self):
        with self.assertRaisesRegex(ValueError,'differs'):
            p.overlay(DB,NET.replace('                if (unionType == 40)','                if (unionType == 20)\n { wrong(); }\n                if (unionType == 40)'),CAN)
    def test_ambiguous_canonical_handler_refused(self):
        with self.assertRaises(ValueError):p.overlay(DB,NET,CAN+CAN)
    def test_snapshot_must_be_inside_build(self):
        with tempfile.TemporaryDirectory() as tmp:
            root=Path(tmp)
            with self.assertRaisesRegex(ValueError,'within repository build'):
                d.stage(root,root,root/'snapshot',root/'stage',root/'other-source')

if __name__=='__main__':unittest.main()
