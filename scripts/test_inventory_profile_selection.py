"""Execute local profile selection with real WinForms selectors and backend processes."""
import os
import sqlite3
from contextlib import closing
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
            with closing(sqlite3.connect(db)) as con:
                tables=[row[0] for row in con.execute("SELECT name FROM sqlite_master WHERE type='table'")]
                before={table:con.execute('SELECT * FROM "'+table+'"').fetchall() for table in tables}
            script=ROOT/'scripts/test_inventory_profile_selection.ps1'
            env=dict(os.environ,PROFILE_TEST_ROOT=str(root),PROFILE_TEST_REPO=str(ROOT))
            proc=subprocess.run(['powershell.exe','-NoProfile','-STA','-ExecutionPolicy','Bypass','-File',str(script)],
                                cwd=ROOT,env=env,capture_output=True,timeout=120)
            output=proc.stdout.decode('utf-8',errors='replace')+proc.stderr.decode('utf-8',errors='replace')
            self.assertEqual(proc.returncode,0,output)
            self.assertIn('LOCAL_PROFILE_SELECTION_PASS',output)
            with closing(sqlite3.connect(db)) as con:
                after={table:con.execute('SELECT * FROM "'+table+'"').fetchall() for table in tables}
                self.assertEqual(after,before)  # Draft rows are new DB records, not changes to existing characters.
                self.assertGreater(con.execute("SELECT COUNT(*) FROM NativeState WHERE Content IS NOT NULL AND Name LIKE '%.json'").fetchone()[0],0)

    def test_stale_sidecar_promotes_in_same_window_without_discarding_edits(self):
        with fixtures.CardPreservationTests().fixture() as (root,db):
            shutil.copytree(ROOT/'gui_launcher/data',root/'gui_launcher/data')
            script = r"""
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
. (Join-Path $env:PROFILE_TEST_REPO 'gui_launcher/inventory_admin_gui.ps1')
$root=$env:PROFILE_TEST_ROOT;$backend=Join-Path $env:PROFILE_TEST_REPO 'adapter_runtime/Nanaimo.Adapter.exe'
$real=@(Get-InventoryAdminProfiles $root $backend '416C706861'|Where-Object{$_.character_id-eq11})[0]
$snap=Read-InventoryAdminSnapshot $root $backend $real
# Simulate the cached pre-login sidecar, while first login has now created its DB row.
$stale=[pscustomobject]@{username='Alpha';character_name='Alpha';name_hex='416C706861';character_id=$null;account_id=$null;display='Alpha'}
$snap.character_id=$null
$ctx=[pscustomobject]@{Root=$root;Backend=$backend;AccountSuffix='';NameHex='';CharacterId=$null;SelectedProfile=$null;Profiles=@($stale);ProfileSelectors=New-Object Collections.ArrayList;ProfileSelectorSync=$false;ProfileChanged=$null;Profile=$null;Clothing=New-Object Collections.ArrayList;Pets=New-Object Collections.ArrayList;GameItems=New-Object Collections.ArrayList;Furniture=New-Object Collections.ArrayList;Cards=New-Object Collections.ArrayList;Shop=$null;RefreshAll=$null}
$form=New-Object Windows.Forms.Form
try {
    Import-InventoryAdminSnapshot $ctx $snap $stale
    for($i=0;$i-lt6;$i++){$panel=New-Object Windows.Forms.Panel;$form.Controls.Add($panel);Add-InventoryAdminProfileSelector $panel $ctx}
    $ctx.Shop.coin=777;$ctx.Shop.selected_pet=15009205
    [void]$ctx.GameItems.Add([pscustomobject]@{code=46000008;count=3;carrier='cash'})
    $ctx.ProfileChanged={throw 'Identity promotion must not reload and discard edits'}
    Set-InventoryAdminProfile $ctx $stale
    [void](Ensure-InventoryAdminProfile $ctx 'Alpha')
    if($ctx.CharacterId-ne11-or$ctx.SelectedProfile.username-ne'P1'){throw 'stale sidecar was not promoted'}
    foreach($selector in $ctx.ProfileSelectors){if($selector.SelectedItem.character_id-ne11){throw 'selector still points at sidecar'}}
    if($ctx.Shop.coin-ne777-or$ctx.Shop.selected_pet-ne15009205-or$ctx.GameItems.Count-ne1){throw 'pending edits lost'}
    $saved=Save-InventoryAdminState $ctx $ctx.NameHex $ctx.Shop $ctx.Profile
    if($saved.source-ne'database'-or$saved.character_id-ne11){throw 'save still went to files'}
    $fresh=Read-InventoryAdminSnapshot $root $backend $ctx.SelectedProfile
    if($fresh.shop.coin-ne777-or$fresh.shop.selected_pet-ne15009205-or@($fresh.game_items|Where-Object{$_.code-eq46000008-and$_.count-eq3}).Count-ne1){throw 'database readback mismatch'}
    Write-Output 'STALE_PROFILE_PROMOTION_PASS selectors=6 edits=preserved target=database'
} finally {$form.Dispose()}
"""
            env=dict(os.environ,PROFILE_TEST_ROOT=str(root),PROFILE_TEST_REPO=str(ROOT))
            proc=subprocess.run(['powershell.exe','-NoProfile','-STA','-ExecutionPolicy','Bypass','-Command',script],
                                cwd=ROOT,env=env,capture_output=True,timeout=120)
            output=proc.stdout.decode('utf-8',errors='replace')+proc.stderr.decode('utf-8',errors='replace')
            self.assertEqual(proc.returncode,0,output)
            self.assertIn('STALE_PROFILE_PROMOTION_PASS',output)
            self.assertFalse((root/'inventory_admin_profiles').exists())

    def test_profile_change_clears_previous_characters_equipment_and_pet_controls(self):
        script = r"""
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
$path=Join-Path $env:PROFILE_TEST_REPO 'gui_launcher/nanaimo_launcher.ps1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($path,[ref]$tokens,[ref]$errors)
if($errors.Count){throw $errors[0]}
foreach($fn in @('New-ChoiceList','Bind-Combo','Select-ComboId','Get-SelectedData','Update-PetAgeOptions','Selected-PetAge')){
    $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name-eq$fn},$true)
    . ([scriptblock]::Create($node.Extent.Text))
}
$form=New-Object Windows.Forms.Form
try {
    $comboMap=@{}
    foreach($part in @('hair','body','top','bottom','accessory','effect')){
        $combo=New-Object Windows.Forms.ComboBox;$form.Controls.Add($combo)
        $choices=New-ChoiceList @([pscustomobject]@{id=10130337;name='old';gender='M'})
        Bind-Combo $combo $choices 10130337;$comboMap[$part]=$combo
    }
    $petCombo=New-Object Windows.Forms.ComboBox;$form.Controls.Add($petCombo)
    $choices=New-ChoiceList @([pscustomobject]@{id=15009205;name='old';max_age=3;display_age=3}) -Pet
    Bind-Combo $petCombo $choices 15009205
    $petAgeCombo=New-Object Windows.Forms.ComboBox;$form.Controls.Add($petAgeCombo)
    $nameBox=[pscustomobject]@{Text='Old'};$genderCombo=[pscustomobject]@{SelectedIndex=1}
    foreach($var in @('levelBox','hpMaxBox','mpMaxBox','attackBox','defenseBox','coinBox','nanaPointBox')){Set-Variable $var ([pscustomobject]@{Value=1;Minimum=0;Maximum=65535})}
    $inventoryAdmin=[pscustomobject]@{ProfileChanged=$null}
    $source=Get-Content -LiteralPath $path -Raw -Encoding UTF8
    $start=$source.IndexOf('$inventoryAdmin.ProfileChanged={');$end=$source.IndexOf('}.GetNewClosure()',$start)+'}.GetNewClosure()'.Length
    . ([scriptblock]::Create($source.Substring($start,$end-$start)))
    $ctx=[pscustomobject]@{Profile=[pscustomobject]@{character_name='Empty';level=1;gender=0;hp_max=1500;mp_max=100;attack=0;defense=0;coin=0;nana_point=0};Shop=[pscustomobject]@{equipped=@(0,0,0,0,0);effect=0;selected_pet=0}}
    &$inventoryAdmin.ProfileChanged $ctx
    foreach($combo in $comboMap.Values){if((Get-SelectedData $combo).id-ne0){throw 'previous appearance leaked'}}
    if((Get-SelectedData $petCombo).id-ne0-or(Selected-PetAge)-ne0-or$nameBox.Text-ne'Empty'){throw 'previous pet/name leaked'}
    Write-Output 'EMPTY_PROFILE_CONTROLS_PASS'
} finally {$form.Dispose()}
"""
        env=dict(os.environ,PROFILE_TEST_REPO=str(ROOT))
        proc=subprocess.run(['powershell.exe','-NoProfile','-STA','-ExecutionPolicy','Bypass','-Command',script],
                            cwd=ROOT,env=env,capture_output=True,timeout=120)
        output=proc.stdout.decode('utf-8',errors='replace')+proc.stderr.decode('utf-8',errors='replace')
        self.assertEqual(proc.returncode,0,output)
        self.assertIn('EMPTY_PROFILE_CONTROLS_PASS',output)

    def test_single_default_profile_builds_all_management_tabs(self):
        fixture=fixtures.CardPreservationTests()
        with fixture.fixture() as (root,_):
            single=root/'single'
            shutil.copytree(ROOT/'gui_launcher/data',single/'gui_launcher/data')
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
