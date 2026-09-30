"""Execute local profile selection with real WinForms selectors and backend processes."""
import os
import shutil
import subprocess
import unittest
from pathlib import Path

import test_inventory_admin_profiles as fixtures
ROOT=fixtures.ROOT


@unittest.skipUnless(shutil.which('powershell.exe'), 'Windows PowerShell required')
class ProfileSelectionTests(unittest.TestCase):
    def test_real_selectors_clone_save_reopen_and_textbox_priority(self):
        fixture=fixtures.CardPreservationTests()
        with fixture.fixture() as (root,db):
            shutil.copytree(ROOT/'gui_launcher/data',root/'gui_launcher/data')
            before=db.read_bytes()
            script=ROOT/'scripts/test_inventory_profile_selection.ps1'
            env=dict(os.environ,PROFILE_TEST_ROOT=str(root),PROFILE_TEST_REPO=str(ROOT))
            proc=subprocess.run(['powershell.exe','-NoProfile','-STA','-ExecutionPolicy','Bypass','-File',str(script)],
                                cwd=ROOT,env=env,capture_output=True,timeout=120)
            output=proc.stdout.decode('utf-8',errors='replace')+proc.stderr.decode('utf-8',errors='replace')
            self.assertEqual(proc.returncode,0,output)
            self.assertIn('LOCAL_PROFILE_SELECTION_PASS',output)
            self.assertEqual(db.read_bytes(),before)

    def test_single_default_profile_builds_all_management_tabs(self):
        fixture=fixtures.CardPreservationTests()
        with fixture.fixture() as (root,_):
            single=root/'single'
            shutil.copytree(ROOT/'gui_launcher/data',single/'gui_launcher/data')
            shutil.copy2(ROOT/'gui_launcher/inventory_admin_backend.py',single/'gui_launcher/inventory_admin_backend.py')
            script = r"""
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
. (Join-Path $env:PROFILE_TEST_REPO 'gui_launcher/inventory_admin_gui.ps1')
$tabs=New-Object Windows.Forms.TabControl
try {
    $ctx=Initialize-InventoryAdmin $tabs $env:PROFILE_TEST_ROOT '416C706861'
    if(@($ctx.Profiles).Count-ne1-or$ctx.NameHex-ne'416C706861'-or$ctx.ProfileSelectors.Count-ne5){throw 'Single profile selection is incomplete'}
    Write-Output 'SINGLE_PROFILE_TABS_PASS'
} finally {$tabs.Dispose()}
"""
            env=dict(os.environ,PROFILE_TEST_ROOT=str(single),PROFILE_TEST_REPO=str(ROOT))
            proc=subprocess.run(['powershell.exe','-NoProfile','-STA','-ExecutionPolicy','Bypass','-Command',script],
                                cwd=ROOT,env=env,capture_output=True,timeout=120)
            output=proc.stdout.decode('utf-8',errors='replace')+proc.stderr.decode('utf-8',errors='replace')
            self.assertEqual(proc.returncode,0,output)
            self.assertIn('SINGLE_PROFILE_TABS_PASS',output)


if __name__=='__main__':unittest.main()
