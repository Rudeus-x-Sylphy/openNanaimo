"""Resource-derived launcher catalog and production authored drop compatibility."""
import csv
import json
import unittest
import os
import shutil
import subprocess
import test_inventory_admin_profiles as fixtures
from pathlib import Path
import generate_inventory_card_catalog as catalog

ROOT=Path(__file__).resolve().parents[1]
class InventoryCardCatalogTests(unittest.TestCase):
    def test_generated_catalog_is_current(self):
        for name,text in catalog.generate().items():
            self.assertEqual((catalog.DATA/name).read_bytes(),text.encode('utf-8'))
    def test_actual_card_json_is_in_published_contract(self):
        import hashlib
        import refresh_manifest
        name='gui_launcher/data/inventory_cards.json'
        self.assertIn(name,refresh_manifest.REPOSITORY_FILES)
        manifest=json.loads((ROOT/'manifest/open_release_manifest.json').read_text('utf-8'))
        self.assertIn(name,manifest['critical_files'])
        row=manifest['critical_files'][name]
        self.assertEqual(row['sha256'],hashlib.sha256((ROOT/name).read_bytes()).hexdigest().upper())

    @unittest.skipUnless(os.name=='nt', 'PowerShell installation validation requires Windows')
    def test_summary_560_cannot_hide_old_420_row_json(self):
        import tempfile
        with tempfile.TemporaryDirectory(prefix='nanaimo-old-card-catalog-') as temp:
            root=Path(temp);data=root/'gui_launcher/data';data.mkdir(parents=True)
            shutil.copy2(catalog.DATA/'inventory_catalog_summary.json',data/'inventory_catalog_summary.json')
            rows=json.loads((catalog.DATA/'inventory_cards.json').read_text('utf-8'))
            (data/'inventory_cards.json').write_text(json.dumps([r for r in rows if 13000001<=r['id']<=13000420]),'utf-8')
            script=root/'check.ps1'
            script.write_text("$ErrorActionPreference='Stop'\n. '"+str(ROOT/'gui_launcher/inventory_admin_gui.ps1')+"'\nTest-InventoryAdminInstallation '"+str(root)+"'\n",encoding='utf-8-sig')
            result=subprocess.run(['powershell','-NoProfile','-ExecutionPolicy','Bypass','-File',str(script)],capture_output=True,creationflags=0x08000000)
            self.assertNotEqual(result.returncode,0)
            self.assertIn(b'420/560',result.stderr)

    def test_sp_event_codes_and_authored_drops_are_editable(self):
        rows=json.loads((catalog.DATA/'inventory_cards.json').read_text('utf-8'))
        by_code={r['id']:r for r in rows}
        self.assertEqual(len(rows),560);self.assertEqual(len(by_code),560)
        for i in range(20):
            row=by_code[12000001+i]
            self.assertEqual(row['skill_point_value'],i%10+1)
            self.assertEqual(row['family'],'SP')
        for code in range(50000001,50000101):
            self.assertEqual(by_code[code]['source'],'EDdakgi._D19')
        with (ROOT/'release/components/cards/card_drop_cn.csv').open(encoding='utf-8-sig') as f:
            drops=list(csv.DictReader(f))
        self.assertEqual(len(drops),3752)
        self.assertEqual(len({r['card_id'] for r in drops}),494)
        self.assertTrue(all(int(r['card_id']) in by_code for r in drops))
        for unknown in (12000000,12000021,50000000,50000101,42424242,60000000):
            self.assertNotIn(unknown,by_code)


    @unittest.skipUnless(os.name=='nt', 'WinForms requires Windows')
    def test_real_gui_bulk_grant_save_reload_remove(self):
        with fixtures.CardPreservationTests().fixture() as (root,db):
            data=root/'gui_launcher/data';data.mkdir(parents=True)
            for path in catalog.DATA.glob('inventory_*.json'):
                shutil.copy2(path,data/path.name)
            script=root/'gui-test.ps1'
            script.write_text(r'''param([string]$Repository,[string]$Fixture)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
. (Join-Path $Repository 'gui_launcher/inventory_admin_gui.ps1')
$form=New-Object Windows.Forms.Form
$form.ShowInTaskbar=$false;$form.Opacity=0;$form.Size='1150,950'
$tabs=New-Object Windows.Forms.TabControl;$tabs.Dock='Fill';$form.Controls.Add($tabs)
try{
    $ctx=Initialize-InventoryAdmin $tabs $Fixture '416C706861'
    [void]$form.Show();$tabs.SelectedTab=$ctx.CardAddAllButton.Parent
    [Windows.Forms.Application]::DoEvents()
    if($ctx.Catalog.cards.Count-ne560){throw 'catalog not complete'}
    $ctx.CardAddAllButton.PerformClick()
    foreach($code in @(12000001,12000020,50000001,50000100,22000001,22000011,22000020)){
        if(-not@($ctx.Cards|Where-Object{$_.code-eq$code-and$_.count-gt0}).Count){throw "GUI did not grant $code"}
    }
    [void](Save-InventoryAdminState $ctx $ctx.NameHex $ctx.Shop $ctx.Profile)
    $snap=Read-InventoryAdminSnapshot $Fixture $ctx.Backend $ctx.SelectedProfile
    foreach($code in @(12000001,12000020,50000001,50000100,22000001,22000011,22000020)){
        if(-not@($snap.cards|Where-Object{$_.code-eq$code-and$_.count-gt0}).Count){throw "GUI grant not persisted $code"}
    }
    $ctx.CardRemoveAllButton.PerformClick()
    [void](Save-InventoryAdminState $ctx $ctx.NameHex $ctx.Shop $ctx.Profile)
    $snap=Read-InventoryAdminSnapshot $Fixture $ctx.Backend $ctx.SelectedProfile
    if($snap.cards.Count-ne2-or-not@($snap.cards|Where-Object{$_.code-eq42424242-and$_.count-eq3}).Count-or-not@($snap.cards|Where-Object{$_.code-eq60000000-and$_.count-eq1}).Count){throw 'unknown preservation or known deletion failed'}
    Write-Output 'GUI_SP_EVENT_GRANT_SAVE_RELOAD_REMOVE_PASS'
}finally{$form.Dispose()}
''',encoding='utf-8-sig')
            result=subprocess.run(['powershell','-NoProfile','-STA','-ExecutionPolicy','Bypass','-File',str(script),str(ROOT),str(root)],capture_output=True,timeout=120,creationflags=0x08000000)
            self.assertEqual(result.returncode,0,(result.stdout+result.stderr).decode('utf-8',errors='replace'))
            self.assertIn(b'GUI_SP_EVENT_GRANT_SAVE_RELOAD_REMOVE_PASS',result.stdout)

if __name__=='__main__':unittest.main()
