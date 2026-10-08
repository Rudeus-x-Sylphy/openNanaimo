"""Adapter deployment guards and exact-byte rollback without touching user state."""
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import deploy_adapter_runtime as deploy
class AdapterDeploymentTests(unittest.TestCase):
    def test_dry_run_install_and_rollback_preserve_private_state(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);source=root/'source';target=root/'target'
            for folder,content in ((source,b'new'),(target,b'old')):
                (folder/'adapter_runtime').mkdir(parents=True);(folder/'manifest').mkdir()
                (folder/'adapter_runtime/worker.exe').write_bytes(content)
                for name in ('source_closure.json','open_release_manifest.json'):(folder/'manifest'/name).write_text('{}')
            (target/'game.exe').write_bytes(b'client');(target/'nanaimo_launcher_profile.ini').write_bytes(b'profile')
            (target/'adapter_data').mkdir();(target/'adapter_data/save.db').write_bytes(b'save')
            manifests=({'critical_files':{'adapter_runtime/worker.exe':{}}},{'files':[]})
            with patch.object(deploy,'ROOT',source),patch.object(deploy,'verify_root',return_value=manifests),patch.object(deploy,'stopped') as stopped:
                self.assertEqual(deploy.deploy(target)['changed'],1);stopped.assert_not_called()
                result=deploy.deploy(target,True);self.assertEqual(result['status'],'installed')
                journal=json.loads((target/result['receipt']).read_text('utf8'));self.assertEqual((target/'adapter_runtime/worker.exe').read_bytes(),b'new')
                deploy.restore(target,journal);self.assertEqual((target/'adapter_runtime/worker.exe').read_bytes(),b'old')
                for name,value in [('game.exe',b'client'),('nanaimo_launcher_profile.ini',b'profile'),('adapter_data/save.db',b'save')]:self.assertEqual((target/name).read_bytes(),value)
    def test_running_adapter_refused_before_writes(self):
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);source=root/'source';target=root/'target'
            for folder in (source,target):
                (folder/'manifest').mkdir(parents=True)
                for name in ('source_closure.json','open_release_manifest.json'):(folder/'manifest'/name).write_text('{}')
            with patch.object(deploy,'ROOT',source),patch.object(deploy,'verify_root',return_value=({'critical_files':{}},{'files':[]})),patch.object(deploy,'stopped',side_effect=ValueError('running')):
                with self.assertRaisesRegex(ValueError,'running'):deploy.deploy(target,True)
                self.assertFalse((target/'deployment_backups').exists())
if __name__=='__main__':unittest.main(verbosity=2)
