"""PET release transaction checks; never touch the installed client or active service."""
import tempfile
from pathlib import Path
import unittest
from unittest.mock import patch
import deploy_pet_catalog_update as d

class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory(prefix='nanaimo-pet-deploy-')
        self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
        (self.root/'pi._D7').write_bytes(b'old');(self.root/'game.db').write_bytes(b'private')
        self.data={'pi._D7':b'new','new.json':b'new-file'}
        self.guard=patch.object(d.io,'assert_not_running');self.guard.start();self.addCleanup(self.guard.stop)
        self.payload=patch.object(d,'build_payloads',return_value=self.data);self.build_mock=self.payload.start();self.addCleanup(self.payload.stop)
        self.check=patch.object(d,'verify_installed');self.verify_mock=self.check.start();self.addCleanup(self.check.stop)
    def test_dry_run_preserves_everything(self):
        self.assertEqual(d.deploy(self.root)['changed'],2)
        self.assertEqual((self.root/'pi._D7').read_bytes(),b'old');self.assertFalse((self.root/'new.json').exists())
    def test_backup_idempotence_and_exact_rollback(self):
        result=d.deploy(self.root,True);self.assertEqual(result['changed'],2)
        self.assertEqual(d.deploy(self.root,True)['changed'],0)
        self.assertEqual((self.root/'game.db').read_bytes(),b'private')
        d.rollback(self.root,result['receipt'])
        self.assertEqual((self.root/'pi._D7').read_bytes(),b'old');self.assertFalse((self.root/'new.json').exists())
    def test_validation_failure_rolls_back(self):
        with patch.object(d,'verify_installed',side_effect=ValueError('failed')):
            with self.assertRaises(ValueError):d.deploy(self.root,True)
        self.assertEqual((self.root/'pi._D7').read_bytes(),b'old');self.assertFalse((self.root/'new.json').exists())
    def test_edited_target_is_not_overwritten_by_rollback(self):
        result=d.deploy(self.root,True);(self.root/'pi._D7').write_bytes(b'user-edit')
        with self.assertRaisesRegex(ValueError,'edited'):d.rollback(self.root,result['receipt'])
        self.assertEqual((self.root/'pi._D7').read_bytes(),b'user-edit')
    def test_active_client_refused_before_payload_or_writes(self):
        with patch.object(d.io,'assert_not_running',side_effect=ValueError('active')):
            with self.assertRaisesRegex(ValueError,'active'):d.deploy(self.root,True)
        self.assertEqual((self.root/'pi._D7').read_bytes(),b'old')
    def test_resources_only_reuses_backup_and_verification(self):
        with patch.object(d,'build_resource_payloads',return_value=self.data) as scoped:
            result=d.deploy(self.root,True,resources_only=True)
        scoped.assert_called_once_with(self.root.resolve())
        self.assertEqual(result['status'],'installed')
        self.build_mock.assert_not_called();self.verify_mock.assert_called_once()
        self.assertEqual((self.root/'game.db').read_bytes(),b'private')
        d.rollback(self.root,result['receipt'])
        self.assertEqual((self.root/'pi._D7').read_bytes(),b'old')

    def test_resources_only_preflight_failure_preserves_installation(self):
        with patch.object(d,'build_resource_payloads',side_effect=ValueError('source pin mismatch')):
            with self.assertRaisesRegex(ValueError,'pin mismatch'):
                d.deploy(self.root,True,resources_only=True)
        self.assertEqual((self.root/'pi._D7').read_bytes(),b'old')
        self.assertFalse((self.root/'deployment_backups').exists())

    def test_wrong_receipt_and_escape_rejected(self):
        result=d.deploy(self.root,True);path=self.root/result['receipt'];j=d.io.load(path);j['client']='elsewhere';d.io.atomic(path,d.io.encoded(j))
        with self.assertRaisesRegex(ValueError,'Wrong'):d.rollback(self.root,result['receipt'])
        with self.assertRaises(ValueError):d.rollback(self.root,'../outside')

if __name__=='__main__':unittest.main()
