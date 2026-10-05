import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import deploy_lumineos_release as deploy

class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.client=Path(self.temp.name);self.repo=self.client/'repo';self.repo.mkdir()
        self.bundle=self.client/'bundle';self.bundle.mkdir()
        (self.client/'game.exe').write_bytes(b'original game')
        (self.client/'nanaimo_launcher_profile.ini').write_bytes(b'private state')
        self.data={'game.exe':b'patched game','adapter_runtime/check.bin':b'new runtime',
                   'manifest/open_release_manifest.json':b'{"critical_files":{}}'}
        for target,value in [('ROOT',self.repo),('payloads',lambda _:self.data)]:
            p=patch.object(deploy,target,value);p.start();self.addCleanup(p.stop)
        for obj,name in [(deploy.resources,'assert_not_running'),(deploy.package,'verify_records'),(deploy.package,'verify_runtime_contract')]:
            p=patch.object(obj,name);p.start();self.addCleanup(p.stop)
        self.install=patch.object(deploy.resources,'apply',return_value={'receipt':'resource-receipt'}).start()
        self.addCleanup(patch.stopall)
        self.resource_rollback=patch.object(deploy.resources,'rollback').start()
        patch.object(deploy.resources,'verify',return_value={'verified':878}).start()

    def test_dry_run_does_not_change_client(self):
        result=deploy.deploy(self.client,self.bundle)
        self.assertEqual(result['status'],'dry-run');self.install.assert_not_called()
        self.assertEqual((self.client/'game.exe').read_bytes(),b'original game')

    def test_install_rollback_preserves_user_state_and_new_file_cleanup(self):
        result=deploy.deploy(self.client,self.bundle,True)
        self.install.assert_called_once_with(bundle=self.bundle,client=self.client)
        self.assertEqual(result['preserved_state_files'],1)
        self.assertEqual((self.client/'game.exe').read_bytes(),b'patched game')
        self.assertEqual(deploy.rollback(self.client,result['receipt'])['status'],'rolled-back')
        self.assertEqual((self.client/'game.exe').read_bytes(),b'original game')
        self.assertFalse((self.client/'adapter_runtime/check.bin').exists())
        self.assertEqual((self.client/'nanaimo_launcher_profile.ini').read_bytes(),b'private state')
        self.resource_rollback.assert_called_once_with(self.client,'resource-receipt')

    def test_partial_code_failure_restores_code_and_resource_transaction(self):
        original=deploy.resources.atomic
        def write(path,data):
            if path==self.client/'adapter_runtime/check.bin' and data==b'new runtime':raise OSError('injected')
            return original(path,data)
        with patch.object(deploy.resources,'atomic',side_effect=write):
            with self.assertRaisesRegex(OSError,'injected'):deploy.deploy(self.client,self.bundle,True)
        self.assertEqual((self.client/'game.exe').read_bytes(),b'original game')
        self.resource_rollback.assert_called_once()

    def test_rollback_refuses_unrelated_edits(self):
        result=deploy.deploy(self.client,self.bundle,True)
        (self.client/'game.exe').write_bytes(b'user edit')
        with self.assertRaisesRegex(ValueError,'edited deployment target'):deploy.rollback(self.client,result['receipt'])
        self.assertEqual((self.client/'game.exe').read_bytes(),b'user edit')

if __name__=='__main__':unittest.main()
