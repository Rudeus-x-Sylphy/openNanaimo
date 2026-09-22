"""Source-level launcher policy guards; WinForms geometry uses -SelfTestLayout."""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

class LauncherContractTests(unittest.TestCase):
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
        self.assertIn("$_.Name -match '^(nanaimo_adapter|nanaimo_client|game_unpack)'", cleanup)
        self.assertIn("$AdapterLog=Join-Path $Root 'adapter_nanaimo_launcher.log'", text)
        self.assertIn("$AdapterErr=Join-Path $Root 'adapter_nanaimo_launcher_stderr.log'", text)
    def test_connection_parameter_uses_adapter_name(self):
        text = (ROOT / 'gui_launcher/client_connect.ps1').read_text('utf-8-sig')
        self.assertTrue(text.startswith('param([Parameter(Position=0)][string]$AdapterIP,'))
        self.assertNotIn('[Alias(', text)

    def test_launcher_applies_and_verifies_user_owned_compatibility_overlay(self):
        text = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        self.assertIn("$ClientCompatibilityTool=Join-Path $Root 'scripts\\prepare_client_compatibility.py'", text)
        self.assertIn("$ClientCompatibilityOverlay=Join-Path $AdapterData 'client_compatibility_overlay'", text)
        self.assertIn('function Ensure-ClientCompatibility', text)
        self.assertIn("'--emotion','--dungeon7','--overwrite','--apply'", text)
        self.assertNotIn("'--all','--overwrite','--apply'", text)
        self.assertIn('$report.verification.all_pass', text)
        click = text.split('$clientBtn.add_Click({', 1)[1].split('# Pet lookup tab', 1)[0]
        self.assertLess(click.index('Stop-Process -Force'), click.index('Ensure-ClientCompatibility'))
        self.assertLess(click.index('Ensure-ClientCompatibility'), click.index('Start-LocalAdapter'))
        self.assertLess(click.index('Ensure-ClientCompatibility'), click.index('Start-Process -FilePath $Client'))
    def test_game_entry_and_scoped_stop(self):
        for filename in ('nanaimo_launcher.ps1', 'client_connect.ps1'):
            text = (ROOT / 'gui_launcher' / filename).read_text('utf-8-sig')
            self.assertIn("$Client=Join-Path $Root 'game.exe'", text)
            self.assertIn("Where-Object{$_.Path-eq$Client}|Stop-Process", text)
            self.assertNotIn("'nanaimo_client.exe'", text)

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
        self.assertIn("Read-ProfileU16 $ini 'mp_current' 500 -AllowZero", text)
        restore = text.split('$defaultBtn.add_Click({', 1)[1].split('})', 1)[0]
        self.assertIn('$mpMaxBox.Value=500;', restore)
        self.assertIn('$mpCurrentBox.Value=500;', restore)
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
        self.assertIn('$levelBox.Top-$nameBox.Top-ne40', layout)
        for guard in ('$levelBox.Top-ne144', '$titleCombo.Top-ne190',
                      '$comboMap.body.Top-ne321', '$saveBtn.Top-ne650', '$status.Top-ne705'):
            self.assertIn(guard, layout)
        self.assertNotIn('Save-Profile', layout)
        self.assertNotIn('Start-Process', layout)

    def test_fixed_transport_and_profile_contract_remain_internal(self):
        text = (ROOT / 'gui_launcher/nanaimo_launcher.ps1').read_text('utf-8-sig')
        for invariant in (
            "ClientArgs=[string[]]@('-q',':1:1:0:3:4:-i','5:-r','6:7:1:127.0.0.1:')",
            "Login='Network Login Game';AdapterIP='127.0.0.1'",
            'StartLocalAdapter=$true',
            '"launch_mode=$launchMode","network_ip=$networkIp"',
            'launch_mode=$launchMode;network_ip=$networkIp;',
            'Install-LaunchModeConfig $launchModeInfo',
            'Register-ClientProfile $launchModeInfo.AdapterIP',
            'Start-Process -FilePath $Client -ArgumentList ([string[]]$launchModeInfo.ClientArgs)',
            "$AdapterManifest=Join-Path $AdapterRuntimeRoot 'adapter_manifest.json'",
            'Get-Content -LiteralPath $AdapterManifest -Raw -Encoding UTF8|ConvertFrom-Json',
            'Test-AdapterBinary',
        ):
            self.assertIn(invariant, text)

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
        self.assertIn('unsigned pet_level=g_managed_pet_combat_level;', protocol)

    def test_client_and_installer_export_denied(self):
        from export_patch import payload_policy, ExportError
        for filename in ('game.exe', 'nanaimo_client.exe', 'installer/setup.exe', 'official.zip'):
            for layer in ('runtime', 'source', 'docs'):
                with self.subTest(filename=filename, layer=layer), self.assertRaises(ExportError):
                    payload_policy(filename, layer)

if __name__ == '__main__':
    unittest.main()
