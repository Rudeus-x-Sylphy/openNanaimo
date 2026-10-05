"""Source-level launcher policy guards; WinForms geometry uses -SelfTestLayout."""
import json
import shutil
import subprocess
import tempfile
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

class LauncherContractTests(unittest.TestCase):

    @unittest.skipUnless(shutil.which('powershell.exe'), 'Windows PowerShell required')
    def test_social_start_does_not_save_local_inventory(self):
        # Execute the actual GUI click body, with process/UI boundaries stubbed.
        script = r"""
$ErrorActionPreference='Stop'
$text=Get-Content -LiteralPath 'gui_launcher/nanaimo_launcher.ps1' -Raw -Encoding UTF8
$body=[regex]::Match($text,'(?s)\$socialAdapterBtn\.add_Click\(\{\s*try\{(.*?)\}catch').Groups[1].Value
if(-not$body){throw 'social click handler not found'}
$ProfileIni=Join-Path $env:TEST_TMP 'profile.ini'
$SocialAdapterIpState=Join-Path $env:TEST_TMP 'adapter_ip.txt'
[IO.File]::WriteAllText($ProfileIni,'sentinel-profile')
$socialIpBox=[pscustomobject]@{Text='192.0.2.10'}
$socialStatus=[pscustomobject]@{Text=''}
$pureNewPlayerBox=[pscustomobject]@{Checked=$true}
$script:calls=@()
function Normalize-NetworkIPv4($ip){return $ip}
function Save-Profile {throw 'UNRELATED_LOCAL_SAVE_CALLED'}
function Stop-LocalAdapter {$script:calls+='stop'}
function Start-LocalAdapter($profile,$ip){
 if($profile-ne$ProfileIni-or$ip-ne'192.0.2.10'){throw 'wrong startup arguments'}
 $script:calls+='start';return [pscustomobject]@{Id=1234}
}
& ([scriptblock]::Create($body))
if(($script:calls-join',')-ne'stop,start'){throw 'wrong startup sequence'}
if([IO.File]::ReadAllText($ProfileIni)-ne'sentinel-profile'){throw 'profile modified'}
if([IO.File]::ReadAllText($SocialAdapterIpState).Trim()-ne'192.0.2.10'){throw 'address not saved'}
if($socialStatus.Text-notmatch'1234'){throw 'success not displayed'}
$ProfileIni=Join-Path $env:TEST_TMP 'missing-profile.ini';$script:calls=@()
try{& ([scriptblock]::Create($body));throw 'missing profile accepted'}catch{
 if($_.Exception.Message-notmatch'Adapter profile missing:'){throw}
}
if($script:calls.Count){throw 'existing adapter stopped before profile preflight'}
'SOCIAL_START_NO_LOCAL_SAVE_PASS'
"""
        self.run_powershell_check(script, 'SOCIAL_START_NO_LOCAL_SAVE_PASS')

    @unittest.skipUnless(shutil.which('powershell.exe'), 'Windows PowerShell required')
    def test_inventory_backend_preserves_complete_error(self):
        script = r"""
$ErrorActionPreference='Stop'
. ./gui_launcher/inventory_admin_gui.ps1
[IO.File]::WriteAllText((Join-Path $env:TEST_TMP 'adapter_manifest.json'),'{"launcher_tools":1}')
$bad=Join-Path $env:TEST_TMP 'bad.cmd'
[IO.File]::WriteAllText($bad,"@echo off`r`necho UNKNOWN_CARD_SENTINEL 1>&2`r`nexit /b 1`r`n")
try{Invoke-InventoryAdminBackend @($bad);throw 'failure not propagated'}catch{
 $message=$_.Exception.Message
 if($message-notmatch'UNKNOWN_CARD_SENTINEL'-or$message-notmatch'exit=1'){throw "truncated error: $message"}
}
if($ErrorActionPreference-ne'Stop'){throw 'caller error policy not restored'}
$good=Join-Path $env:TEST_TMP 'good.cmd'
[IO.File]::WriteAllText($good,'@echo {"ok":true}')
$result=Invoke-InventoryAdminBackend @($good)|ConvertFrom-Json
if(-not$result.ok){throw 'successful JSON changed'}
if($ErrorActionPreference-ne'Stop'){throw 'success changed caller error policy'}
'BACKEND_FULL_ERROR_PASS'
"""
        self.run_powershell_check(script, 'BACKEND_FULL_ERROR_PASS')


    @unittest.skipUnless(shutil.which('powershell.exe'), 'Windows PowerShell required')
    def test_social_preparation_failure_never_registers_account(self):
        script = r"""
$ErrorActionPreference='Stop'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PWD 'gui_launcher/nanaimo_launcher.ps1'),[ref]$tokens,[ref]$errors)
$node=$ast.Find({param($n) $n-is[Management.Automation.Language.FunctionDefinitionAst]-and$n.Name-eq'Start-SocialParticipant'},$true)
. ([scriptblock]::Create($node.Extent.Text))
$Root=$env:TEST_TMP;$Client=Join-Path $Root 'game.exe'
$SocialAdapterIpState=Join-Path $Root 'adapter_ip.txt'
$SocialClientLauncher=Join-Path $Root 'refuse.ps1'
[IO.File]::WriteAllText($SocialClientLauncher,"throw 'PREPARE_REFUSED'")
$socialIpBox=[pscustomobject]@{Text='192.0.2.10'}
$script:registered=$false
function Normalize-NetworkIPv4($value){return $value}
function Get-LaunchModeInfo{ return [pscustomobject]@{ClientArgs=@()} }
function Test-ClientBinary{}
function Get-LaunchModeConfigText{return 'config'}
function Ensure-ClientCompatibility{}
function Register-SocialAccount{$script:registered=$true;throw 'REGISTERED_TOO_EARLY'}
try{Start-SocialParticipant 1 'P1';throw 'failure not propagated'}catch{
 if($_.Exception.Message-notmatch'PREPARE_REFUSED'){throw}
}
if($script:registered-or(Test-Path -LiteralPath $SocialAdapterIpState)){throw 'failed prepare modified registration state'}
'SOCIAL_PREFLIGHT_BEFORE_REGISTRATION_PASS'
"""
        self.run_powershell_check(script, 'SOCIAL_PREFLIGHT_BEFORE_REGISTRATION_PASS')

    def run_powershell_check(self, script, marker):
        import os
        with tempfile.TemporaryDirectory(prefix='nanaimo-launcher-check-') as temp:
            ps = Path(temp) / 'check.ps1'
            ps.write_text(script, encoding='utf-8-sig')
            result = subprocess.run(
                ['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(ps)],
                cwd=ROOT, env={**os.environ, 'TEST_TMP': temp},
                capture_output=True, text=True, errors='replace', timeout=45)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn(marker, result.stdout)

    def test_apartment_exterior_is_applied_during_normal_launch(self):
        text = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        self.assertIn("'--land-purchase','--apartment-exterior','--apartment-recommendation','--dungeon7'", text)

    def test_adapter_artifacts_and_process_contract(self):
        text = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        native_build = (ROOT / 'scripts/build_adapter.ps1').read_text('utf-8-sig')
        full_build = (ROOT / 'scripts/build_complete_adapter.ps1').read_text('utf-8-sig')
        cleanup = (ROOT / 'scripts/clean_generated_state.ps1').read_text('utf-8-sig')
        self.assertIn("$AdapterRuntimeRoot=Join-Path $Root 'adapter_runtime'", text)
        self.assertIn("$Adapter=Join-Path $AdapterRuntimeRoot 'Nanaimo.Adapter.exe'", text)
        self.assertIn("$AdapterBridge=Join-Path $AdapterRuntimeRoot 'nanaimo_gameplay_bridge.exe'", text)
        self.assertIn("$AdapterManifest=Join-Path $AdapterRuntimeRoot 'adapter_manifest.json'", text)
        self.assertIn('function Test-AdapterBinary', text)
        self.assertIn('$SelfTestAdapterManifest', text)
        self.assertIn('foreach($row in @($contract.files))', text)
        self.assertIn("'Nanaimo.Adapter.dll','Nanaimo.Gameplay.dll'", text)
        self.assertIn("Get-ChildItem -LiteralPath $output -Recurse -File", full_build)
        self.assertIn('function Get-LocalAdapters', text)
        self.assertIn('@($Adapter,$AdapterBridge,$LegacyAdapter)', text)
        self.assertIn('function Start-LocalAdapter', text)
        self.assertIn('function Stop-LocalAdapter', text)
        self.assertIn('Start-Process -FilePath $Adapter ', text)
        self.assertIn("--login-port','11005','--world-port','12050','--profile-port','11999'", text)
        self.assertIn('start_local_adapter=$launchModeInfo.StartLocalAdapter', text)
        self.assertIn("$Mod=Join-Path $Root 'adapter'", native_build)
        self.assertIn("@('nanaimo_adapter.c','nanaimo_adapter.exe')", native_build)
        self.assertIn("@('nanaimo_adapter_testports.c','nanaimo_adapter_testports.exe')", native_build)
        self.assertIn("'managed-host\\Nanaimo.Adapter.csproj'", full_build)
        self.assertIn("'adapter_runtime'", full_build)
        self.assertIn("'Nanaimo.Adapter.exe'", full_build)
        self.assertIn("'nanaimo_gameplay_bridge.exe'", full_build)
        self.assertIn("nanaimo_gameplay_bridge", cleanup)
        self.assertIn("--tools state clear-legacy", cleanup)
        self.assertNotIn("Remove-Item", cleanup)
        self.assertIn("$AdapterLog=Join-Path $Root 'adapter_nanaimo_launcher.log'", text)
        self.assertIn("$AdapterErr=Join-Path $Root 'adapter_nanaimo_launcher_stderr.log'", text)
        self.assertIn("$adapterDllRow=@($adapterContract.files|Where-Object name -eq 'Nanaimo.Adapter.dll')[0]", text)
        self.assertIn('Release: $ReleaseIdentity | Canonical entry: start_nanaimo_launcher.bat', text)
        self.assertIn('function Get-AdapterListenerOwners', text)
        self.assertIn('function Assert-AdapterPortsAvailable', text)
        self.assertIn('function Assert-StartedAdapterListeners', text)
        self.assertIn('function Write-RuntimeIdentity', text)
        self.assertIn('Assert-AdapterPortsAvailable', text.split('function Start-LocalAdapter', 1)[1])
        self.assertIn('Write-RuntimeIdentity $proc', text)
    def test_apartment_points_are_a_persistent_launcher_setting(self):
        launcher = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        for expected in ("'apartment_recommendation_points' 1000", "$apartmentPointsBox=New-ResourceNumeric",
                         'apartment_recommendation_points=[uint32][decimal]$apartmentPointsBox.Value',
                         '"apartment_recommendation_points=$($resources.apartment_recommendation_points)"',
                         'apartment_recommendation_points=$resources.apartment_recommendation_points',
                         '$apartmentPointsBox.Value=1000'):
            self.assertIn(expected, launcher)
        database = (ROOT / 'managed/Services/DatabaseService.ApartmentLauncher.cs').read_text('utf-8-sig')
        self.assertIn('DefaultApartmentRecommendationPoints = 1000', database)
        self.assertIn('ConfiguredPoints=$points', database)
        profile = (ROOT / 'managed/Services/DatabaseService.NativeDungeon.cs').read_text('utf-8-sig')
        self.assertEqual(profile.count('await ApplyApartmentLauncherPointsAsync('), 2)

    def test_connection_parameter_uses_adapter_name(self):
        text = (ROOT / 'gui_launcher/client_connect.ps1').read_text('utf-8-sig')
        self.assertTrue(text.startswith('param([Parameter(Position=0)][string]$AdapterIP,'))
        self.assertNotIn('[Alias(', text)

    def test_launcher_applies_and_verifies_user_owned_compatibility_overlay(self):
        text = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        self.assertIn("$ClientCompatibilityTool=$Adapter", text)
        self.assertNotIn("Get-ClientCompatibilityPython", text)
        self.assertIn("'--tools','compatibility'", text)
        self.assertIn("$ClientCompatibilityOverlay=Join-Path $AdapterData 'client_compatibility_overlay'", text)
        self.assertIn("'--apartment-recommendation'", text)
        self.assertIn('function Ensure-ClientCompatibility', text)
        self.assertIn("'--furniture','--native-state','--dungeon-state','--inventory-gift-display','--land-purchase','--apartment-exterior','--apartment-recommendation','--dungeon7','--overwrite','--apply'", text)
        self.assertNotIn("'--emotion'", text)
        self.assertIn('the dedicated Index redirect provides click-time furniture access while C393 provides the bounded scene snapshot.', text)
        self.assertNotIn("'--all','--overwrite','--apply'", text)
        self.assertIn('$report.verification.all_pass', text)
        click = text.split('$clientBtn.add_Click({', 1)[1].split('# Pet lookup tab', 1)[0]
        self.assertNotIn('Stop-Process', click)
        self.assertNotIn('Stop-LocalAdapter', click)
        self.assertNotIn('Start-LocalAdapter', click)
        self.assertLess(click.index('Install-LaunchModeConfig $launchModeInfo'),
                        click.index('Ensure-ClientCompatibility'))
        self.assertLess(click.index('Ensure-ClientCompatibility'),
                        click.index('Register-ClientProfile $launchModeInfo.AdapterIP'))
        self.assertLess(click.index('Ensure-ClientCompatibility'),
                        click.index('Start-Process -FilePath $Client'))
    def test_launcher_and_recipe_manifest_select_identical_operations(self):
        text = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        block = text.split('function Ensure-ClientCompatibility {', 1)[1].split('function Register-ClientProfile', 1)[0]
        args = re.findall(r"'(--[a-z0-9-]+)'", block.split('$arguments=', 1)[1].split('$output=', 1)[0])
        recipe = json.loads((ROOT / 'manifest/patch_runtime_requirements.json').read_text('utf-8'))
        selected = [arg for arg in args if arg not in ('--source-root', '--output-root', '--tools')]
        self.assertEqual(selected, recipe['compatibility_derivation']['launcher_selection']['arguments'])
        self.assertIn('--native-state', selected)
        self.assertNotIn('--revival-display', selected)

    def test_local_entry_reuses_loopback_adapter_and_allows_multiple_clients(self):
        launcher = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        self.assertIn("$Client=Join-Path $Root 'game.exe'", launcher)
        self.assertNotIn("'nanaimo_client.exe'", launcher)
        self.assertIn('[switch]$SelfTestLocalEntry', launcher)
        click = launcher.split('$clientBtn.add_Click({', 1)[1].split('# Pet lookup tab', 1)[0]
        for required in (
                "$launchModeInfo.Key-ne'network'-or$launchModeInfo.AdapterIP-ne'127.0.0.1'",
                'Assert-LocalAdapterRunning', 'Write-PureNewPlayerRuntimeProfile',
                'Test-ClientBinary;Install-LaunchModeConfig $launchModeInfo',
                'Ensure-ClientCompatibility',
                'Register-PureNewPlayer $launchModeInfo.AdapterIP',
                'Register-ClientProfile $launchModeInfo.AdapterIP',
                'Start-Process -FilePath $Client'):
            self.assertIn(required, click)
        for forbidden in ('Stop-LocalAdapter', 'Start-LocalAdapter', 'Save-Profile',
                          'Get-Process -Name game', 'Stop-Process'):
            self.assertNotIn(forbidden, click)
        self.assertLess(click.index('Install-LaunchModeConfig $launchModeInfo'),
                        click.index('Ensure-ClientCompatibility'))
        self.assertLess(click.index('Ensure-ClientCompatibility'),
                        click.index('Register-ClientProfile $launchModeInfo.AdapterIP'))
        self.assertLess(click.index('Register-ClientProfile $launchModeInfo.AdapterIP'),
                        click.index('Start-Process -FilePath $Client'))
        self.assertLess(launcher.index("$tabSocial=New-Object Windows.Forms.TabPage"),
                        launcher.index("$tabStart=New-Object Windows.Forms.TabPage"))
        self.assertIn("$adapterBtn.Text='\u542f\u52a8\u9002\u914d\u5668'", launcher)

        connector = (ROOT / 'gui_launcher/client_connect.ps1').read_text('utf-8-sig')
        self.assertIn("$Client=Join-Path $Root 'game.exe'", connector)
        self.assertIn("Where-Object{$_.Path-eq$Client}|Stop-Process", connector)

    def test_no_mode_or_endpoint_editor(self):
        text = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        for removed in ('$launchModeCombo', '$networkIpBox', '$HostNetwork', '$StandaloneOptionTemplate'):
            self.assertNotIn(removed, text)
        self.assertIn("function Get-SelectedLaunchMode {return 'network'}", text)
        self.assertIn("function Get-NetworkIpInput {return '127.0.0.1'}", text)
        self.assertIn("$defaultName=if($ini.name_hex){Decode-NameHex $ini.name_hex}else{'Greyrat'}", text)

    def test_restore_defaults_and_layout_gate_exist(self):
        text = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        self.assertIn('$SelfTestLayout', text)
        self.assertIn('Bounds.IntersectsWith', text)
        self.assertIn('ScrollControlIntoView($control)', text)
        for part, code in dict(hair=10130337, body=10100028, top=10110337,
                               bottom=10120352, accessory=10150103, effect=10160017).items():
            self.assertIn(f'Select-ComboId $comboMap.{part} {code}', text)

    def test_default_mp_is_500_in_launcher_restore_and_adapter(self):
        text = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        self.assertIn("$defaultMpMax=Read-ProfileU16 $ini 'mp_max' 500", text)
        self.assertNotIn("Read-ProfileU16 $ini 'mp_current'", text)
        self.assertNotIn('$mpCurrentBox', text)
        self.assertNotIn('$hpCurrentBox', text)
        restore = text.split('$defaultBtn.add_Click({', 1)[1].split('})', 1)[0]
        self.assertIn('$mpMaxBox.Value=500;', restore)
        adapter = (ROOT / 'release/components/adapter_core/profile_resources_runtime.inc').read_text('utf-8')
        self.assertIn('static unsigned g_profile_mp_max=500u;', adapter)
        self.assertIn('static unsigned g_profile_mp_current=500u;', adapter)

    def test_connection_details_are_not_presented(self):
        text = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        # Check presentation sinks only, not internal mode objects or saved config.
        forbidden = re.compile(
            r"杩炴帴鏂瑰紡|杩炴帴妯″紡|鍚姩妯″紡|閫傞厤鍣ㄥ湴鍧€|鍥哄畾浣跨敤|\bNetwork\b|127\.0\.0\.1|"
            r"ServerIP|network_ip|launch_mode|\bMode=|\bLogin=|Stand_?alone|(?<!\w)-q(?!\w)",
            re.IGNORECASE)
        for line in text.splitlines():
            if '.Text=' in line or line.startswith('Add-Label $tabStart '):
                with self.subTest(line=line[:100]):
                    self.assertIsNone(forbidden.search(line))
        preview = text.split('function Update-LaunchPreview', 1)[1].split(
            '$refreshLaunchInfoBtn.add_Click', 1)[0]
        self.assertIsNone(forbidden.search(preview.split('$lines=@(', 1)[1]))
        for internal in ('$modeInfo', '$optionState', '$templateState',
                         '$clientArgText', '$clientCommand', '$clientCall'):
            self.assertNotIn(internal, preview)
        for diagnostic in ('Character and profile', 'Resources:', 'Skills:',
                           'Profile INI', 'Profile JSON', 'Binary validation',
                           "Client-State-Line", "Client compatibility is prepared", '$ExpectedAdapterHash', 'Working directory:'):
            self.assertIn(diagnostic, preview)

    def test_visible_text_and_startup_compaction_have_runtime_guards(self):
        text = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        self.assertIn('function Assert-NoVisibleConnectionText', text)
        self.assertIn('if(-not$control.Visible){return}', text)
        self.assertIn('foreach($child in $control.Controls)', text)
        layout = text.split('if($SelfTestLayout){', 1)[1].split(
            'if($SelfTestCatalogPreview){', 1)[0]
        self.assertIn('foreach($page in $tabs.TabPages)', layout)
        self.assertIn('Assert-NoVisibleConnectionText $form', layout)
        self.assertIn('Update-LaunchPreview -ComputeHashes', layout)
        self.assertIn('$nameBox.Top-$pureNewPlayerBox.Top-ne40', layout)
        self.assertIn('$levelBox.Top-$nameBox.Top-ne40', layout)
        for guard in ('$pureNewPlayerBox.Top-ne104', '$pureNewPlayerUsernameBox.Top-ne104',
                      '$levelBox.Top-ne184', '$titleCombo.Top-ne230',
                      '$comboMap.body.Top-ne361', '$saveBtn.Top-ne704', '$status.Top-ne759'):
            self.assertIn(guard, layout)
        self.assertNotIn('Save-Profile', layout)
        self.assertNotIn('Start-Process', layout)

    def test_pure_new_player_profile_reaches_native_startup_boundary(self):
        launcher = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        pure_profile = launcher.split('function Write-PureNewPlayerRuntimeProfile', 1)[1].split(
            'function Register-PureNewPlayer', 1)[0]
        self.assertIn("'free_magic_key_expiry=2000010100'", pure_profile)
        self.assertNotIn("'free_magic_key_expiry=0'", pure_profile)
        self.assertIn("'skip_tutorial=0'", pure_profile)
        self.assertIn("'unlock_all_dungeons=0'", pure_profile)
        self.assertIn('Test-PureNewPlayerNativeStartup $written', launcher)
        click = launcher.split('$clientBtn.add_Click({', 1)[1].split('# Pet lookup tab', 1)[0]
        self.assertIn('Write-PureNewPlayerRuntimeProfile', click)
        self.assertIn('Register-PureNewPlayer $launchModeInfo.AdapterIP', click)
        self.assertNotIn('Start-LocalAdapter', click)
        self.assertNotIn('Stop-LocalAdapter', click)
        self.assertLess(click.index('Write-PureNewPlayerRuntimeProfile'),
                        click.index('Register-PureNewPlayer $launchModeInfo.AdapterIP'))
        native_probe = launcher.split('function Test-PureNewPlayerNativeStartup', 1)[1].split(
            'function Get-AdapterStartupFailureDetail', 1)[0]
        self.assertIn('Start-Process -FilePath $AdapterBridge', native_probe)
        self.assertIn('Test-AdapterPort ($first+20)', native_probe)
        self.assertIn('Test-AdapterPort ($first+7)', native_probe)
        self.assertIn("function Test-AdapterPort([int]$port,[string]$targetHost='127.0.0.1')", launcher)
        self.assertIn('BeginConnect($targetHost,$port', launcher)
        self.assertNotIn("[string]$host='127.0.0.1'", launcher)
        self.assertIn("Join-Path $AdapterLogs 'adapter-error.log'", launcher)
        host = (ROOT / 'managed-host/Program.cs').read_text('utf-8-sig')
        self.assertIn('NativeWorkerExitDetail()', host)
        self.assertIn('if (completed == ended && !stop.IsCancellationRequested)', host)

    def test_dynamic_transport_and_social_contract(self):
        text = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        for invariant in (
            "ClientArgs=[string[]]@('-q',':1:1:0:3:4:-i','5:-r',(\"6:7:1:{0}:\"-f$networkIp))",
            "Login='Network Login Game';AdapterIP=$networkIp",
            'StartLocalAdapter=[Net.IPAddress]::IsLoopback($parsed)',
            "'--bind-address',$bindAddress",
            "function Register-SocialAccount",
            "LocalAccount=$account;PureNewPlayer=$true",
            "function Start-SocialParticipant",
            "$socialPlayButtons.Count",
            "tabSocial.Text='社交模式'",
            "tabStart.Text='本地模式'",
            'Install-LaunchModeConfig $launchModeInfo',
            'Register-ClientProfile $launchModeInfo.AdapterIP',
            "$SocialClientLauncher=Join-Path $Root 'gui_launcher\\start_social_client.ps1'",
            '$launch=& $SocialClientLauncher -ClientPath $Client -SocialSlot $slot',
            '-LaunchModeConfigText $launchConfig',
            '$($launch.WorkDirectory)',
            "$AdapterManifest=Join-Path $AdapterRuntimeRoot 'adapter_manifest.json'",
            'Get-Content -LiteralPath $AdapterManifest -Raw -Encoding UTF8|ConvertFrom-Json',
            'Test-AdapterBinary',
        ):
            self.assertIn(invariant, text)
        service = (ROOT / 'managed/Services/NetworkAdapterService.NativeDungeon.cs').read_text('utf-8-sig')
        host = (ROOT / 'managed-host/Program.cs').read_text('utf-8-sig')
        self.assertIn('ConcurrentDictionary<string, ConcurrentQueue', service)
        self.assertIn('GetOrAdd(sourceKey', service)
        self.assertIn('TryGetValue(sourceKey', service)
        self.assertIn('RunLocalProfileListenerAsync(IPAddress bindAddress', service)
        self.assertIn('BindAddress = bindAddressText', host)
        self.assertIn('Host = bindAddressText', host)
        self.assertIn('RunLocalProfileListenerAsync(bindAddress', host)
        protocol = (ROOT / 'managed/Services/NetworkAdapterService.cs').read_text('utf-8-sig')
        self.assertEqual(protocol.count('UnlockAllDungeons && !IsPureNewProfile(session)'), 2)

    def test_furniture_lookup_has_preview_assets(self):
        launcher = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        rows = json.loads((ROOT / 'gui_launcher/data/inventory_furniture.json').read_text('utf-8-sig'))
        preview = json.loads((ROOT / 'gui_launcher/data/previews/furniture_icons.json').read_text('utf-8-sig'))
        self.assertEqual(len(rows), 781)
        self.assertEqual(len(preview), 781)
        self.assertTrue((ROOT / 'gui_launcher/data/previews/furniture_icons.png').is_file())
        for invariant in ("tabFurniture.Text='装饰家具查表'", "New-GridTable 'furniture'",
                          "Update-CatalogGridPreview 'furniture'", '$furniturePreviewPane.Picture.Image'):
            self.assertIn(invariant, launcher)
    def test_inventory_admin_profiles_are_character_scoped(self):
        launcher = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        gui = (ROOT / 'gui_launcher/inventory_admin_gui.ps1').read_text('utf-8-sig')
        backend = (ROOT / 'managed-host/LauncherTools.Inventory.cs').read_text('utf-8-sig') + (ROOT / 'managed-host/LauncherTools.InventoryWrite.cs').read_text('utf-8-sig')
        self.assertIn('Add-InventoryAdminProfileSelector $tabResources $inventoryAdmin', launcher)
        self.assertIn('$profileSaveBtn=New-Object Windows.Forms.Button', launcher)
        self.assertIn('$profileSaveBtn.add_Click', launcher)
        self.assertIn('foreach($adminTab in @($tab,$petTab,$gameTab,$fTab,$cTab))', gui)
        self.assertIn("'--character-id'", gui)
        self.assertIn('case "profiles":', backend)
        self.assertIn('case "clone":', backend)
        self.assertIn('static JsonObject DbSnapshot(long id)', backend)
        self.assertIn('JsonObject DbApply(long id', backend)

    def test_local_text_identity_is_resolved_before_save_and_registration(self):
        launcher=(ROOT/'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        save=launcher.split('function Save-Profile {',1)[1].split('if($SelfTestProfileIO){',1)[0]
        self.assertLess(save.index('Sync-LauncherProfileIdentity'),save.index('Capture-LauncherProfileEditorState'))
        self.assertLess(save.index('Save-InventoryAdminState'),save.index('[IO.File]::WriteAllLines($ProfileIni'))
        entry=launcher.split('$clientBtn.add_Click({',1)[1].split('if($SelfTestLocalEntry){',1)[0]
        self.assertLess(entry.index('Sync-LauncherProfileIdentity'),entry.index('Register-ClientProfile'))
        self.assertNotIn('Assert-LauncherSavedProfileIdentity',launcher)
        self.assertNotIn('Test-Path -LiteralPath $ProfileIni',entry)
        registration=launcher.split('function Register-ClientProfile(',1)[1].split('function New-PureNewPlayerAccountName',1)[0]
        self.assertIn('LocalAccount=$account;PureNewPlayer=$false',registration)
        self.assertNotIn('$ProfileIni',registration)
        self.assertIn('$nameBox.add_Leave',launcher)

    def test_pet_admin_reserves_profile_selector_row(self):
        gui = (ROOT / 'gui_launcher/inventory_admin_gui.ps1').read_text('utf-8-sig')
        layout = re.search(
            r'\$petContentTop=(\d+);\$petSearchTop=(\d+);\$petGridTop=(\d+);\$petGridHeight=(\d+)',
            gui,
        )
        self.assertIsNotNone(layout)
        content_top, search_top, grid_top, grid_height = map(int, layout.groups())
        profile_bottom = 16 + 28
        self.assertGreater(content_top, profile_bottom)
        self.assertGreater(search_top, content_top + 22)
        self.assertGreater(grid_top, search_top + 25)
        self.assertLessEqual(grid_top + grid_height, 535)
        self.assertIn("$ph3.Location=New-Object Drawing.Point(740,$petContentTop)", gui)
        self.assertIn("$gs.Location=New-Object Drawing.Point(740,$petSearchTop)", gui)

    def test_launcher_profile_field_map_and_title_pipeline(self):
        launcher = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        importer = (ROOT / 'managed/Services/DatabaseService.NativeDungeon.cs').read_text('utf-8-sig')
        state = (ROOT / 'managed/Services/NativeDungeonState.cs').read_text('utf-8-sig')
        village = (ROOT / 'managed/Services/NetworkAdapterService.cs').read_text('utf-8-sig')
        room = (ROOT / 'managed/Services/DungeonProtocol.cs').read_text('utf-8-sig')
        save = launcher.split('function Save-Profile {', 1)[1].split('if($SelfTestProfileIO){', 1)[0]

        field_groups = {
            'connector': {'version', 'launch_mode', 'network_ip'},
            'managed_profile': {
                'gender', 'name_hex', 'dungeon_grade', 'level', 'pet',
                'initial_attack_mode', 'equip_hair', 'equip_body', 'equip_top',
                'equip_bottom', 'equip_accessory', 'equip_effect', 'hp_max',
                'mp_max', 'attack', 'defense',
                'coin', 'nana_point', 'card_key_gold', 'card_key_mystery',
                'free_magic_key_expiry', 'quickbar_expiry', 'skill_slot_expiry', 'skill_config',
                'skill_projectile_route', 'skill_meat_route', 'skill_slot_z',
                'skill_slot_x',
            },
            # Audited source gaps retained as explicit classifications so a new
            # field cannot silently disappear from the launcher -> adapter map.
            'known_gap': {
                'skip_tutorial', 'card_key_normal', 'card_key_special',
                'pet_age_a', 'pet_age_b',
            },
        }
        mapped = set().union(*field_groups.values())
        expected = mapped | {f'skill_grade{i}' for i in range(16)}
        for key in sorted(expected):
            if key.startswith('skill_grade'):
                self.assertIn('$lines+="skill_grade$i=', save)
            else:
                self.assertRegex(save, rf"['\"]?{re.escape(key)}=")
        self.assertEqual(len(mapped), sum(len(group) for group in field_groups.values()))

        self.assertIn('ParseLauncherDungeonGrade', importer)
        self.assertIn('ApplyLauncherDungeonGradeAsync', importer)
        self.assertIn('NativeDungeonState.DungeonGradeOffset', importer)
        self.assertIn('public const int DungeonGradeOffset = 5024;', state)
        self.assertIn('payload[0x24 - NativeHeaderLength] = CharacterTitleState.GetGrade(character);', village)
        self.assertIn('payload[0x49 - 8] = CharacterTitleState.GetGrade(character);', room)

    def test_expansion_expiry_controls_are_explicit_and_non_minting(self):
        launcher = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        self.assertIn("'\u5168\u5f00\u5feb\u6377\u680f\u671f\u9650\uff080=\u672a\u542f\u7528\uff09'", launcher)
        self.assertIn("'Z/X\u69fd\u671f\u9650\uff080=\u672a\u542f\u7528\uff09'", launcher)
        self.assertIn("$skillSlotExpiryConfigured=$ini.ContainsKey('skill_slot_expiry')", launcher)
        self.assertIn('if($resources.skill_slot_expiry_apply){$lines+="skill_slot_expiry=', launcher)
        self.assertIn("\u672a\u52fe\u9009=\u4fdd\u7559\u89d2\u8272\u6570\u636e\u5e93\u73b0\u503c", launcher)
        self.assertIn("\u672a\u6765\u671f\u9650\u8868\u793a\u6269\u5bb9\u5df2\u751f\u6548", launcher)
        self.assertNotIn("Read-ProfileUInt64 $ini 'quickbar_expiry' 2099123123", launcher)

    def test_profile_values_have_managed_and_native_carriers(self):
        profile = (ROOT / 'managed/Services/DatabaseService.NativeDungeon.cs').read_text('utf-8-sig')
        state = (ROOT / 'managed/Services/NativeDungeonState.cs').read_text('utf-8-sig')
        bridge = (ROOT / 'release/components/game_session/managed_bridge.inc').read_text('utf-8-sig')
        protocol = (ROOT / 'release/components/protocol/protocol_state_sync_base.inc').read_text('utf-8-sig')
        for field in ('AttackModifier=$attack', 'DefenseFlat=$defense', 'InitialAttackMode=$attackMode',
                      'Level=$level, Experience=$exp'):
            self.assertIn(field, profile)
        for carrier in ('AttackModifierOffset', 'DefenseFlatOffset', 'PetCombatLevelOffset'):
            self.assertIn(carrier, state)
        self.assertIn('MANAGED_PET_COMBAT_LEVEL_OFFSET 5116u', bridge)
        self.assertIn('g_managed_pet_combat_level=managed_get', bridge)
        self.assertIn('g_managed_pet_combat_level', protocol)

    def test_client_and_installer_export_denied(self):
        from export_patch import payload_policy, ExportError
        for filename in ('game.exe', 'nanaimo_client.exe', 'installer/setup.exe', 'official.zip'):
            for layer in ('runtime', 'source', 'docs'):
                with self.subTest(filename=filename, layer=layer), self.assertRaises(ExportError):
                    payload_policy(filename, layer)

if __name__ == '__main__':
    unittest.main()
