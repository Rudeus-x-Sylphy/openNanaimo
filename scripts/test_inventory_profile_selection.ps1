$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
. (Join-Path $env:PROFILE_TEST_REPO 'gui_launcher/inventory_admin_gui.ps1')
$root=$env:PROFILE_TEST_ROOT
$backend=Join-Path $env:PROFILE_TEST_REPO 'adapter_runtime/Nanaimo.Adapter.exe'
$profiles=Get-InventoryAdminProfiles $root $backend '416C706861'
$ctx=[pscustomobject]@{Root=$root;Backend=$backend;AccountSuffix='';NameHex='';CharacterId=$null;SelectedProfile=$null;Profiles=@($profiles);ProfileSelectors=New-Object Collections.ArrayList;ProfileSelectorSync=$false;ProfileChanged=$null;Profile=$null;Clothing=New-Object Collections.ArrayList;Pets=New-Object Collections.ArrayList;GameItems=New-Object Collections.ArrayList;Furniture=New-Object Collections.ArrayList;Cards=New-Object Collections.ArrayList;Shop=$null;RefreshAll=$null}
$form=New-Object Windows.Forms.Form
try{
    Set-InventoryAdminProfile $ctx (Find-InventoryAdminProfile $ctx 'P1')
    for($i=0;$i-lt6;$i++){$panel=New-Object Windows.Forms.Panel;$form.Controls.Add($panel);Add-InventoryAdminProfileSelector $panel $ctx}
    $inventoryAdmin=$ctx
    $nameBox=New-Object Windows.Forms.TextBox
    $pureNewPlayerBox=[pscustomobject]@{Checked=$false}
    $ctx.ProfileChanged={param($c) $nameBox.Text=$c.Profile.character_name}.GetNewClosure()
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $env:PROFILE_TEST_REPO 'gui_launcher/nanaimo_launcher.ps1'),[ref]$tokens,[ref]$errors)
    if($errors.Count){throw $errors[0]}
    foreach($fn in @('Sync-LauncherProfileIdentity','Capture-LauncherProfileEditorState','Register-ClientProfile')){
        $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $fn},$true)
        . ([scriptblock]::Create($node.Extent.Text))
    }
    $levelBox=[pscustomobject]@{Value=23};$genderCombo=[pscustomobject]@{SelectedIndex=1}
    $comboMap=@{};foreach($part in @('hair','body','top','bottom','accessory','effect')){$comboMap[$part]=[pscustomobject]@{id=0}}
    $petCombo=[pscustomobject]@{id=0}
    function Get-SelectedData($combo){return $combo}
    function Get-SkillSelection{$grades=New-Object int[] 16;$grades[0]=3;$grades[8]=4;return [pscustomobject]@{projectile_route=0;meat_route=0;grades=$grades;slot_z=[uint32]52000008;slot_x=[uint32]52000000}}
    function Get-ResourceSelection{return [ordered]@{hp_max=2345;mp_max=345;attack=6;defense=7;coin=888;nana_point=999;apartment_recommendation_points=456}}
    $nameBox.Text='Fresh'
    $selected=Sync-LauncherProfileIdentity
    if($selected.character_name-ne'Fresh'-or$ctx.CharacterId-ne$null-or$ctx.Profile.level-ne23-or$ctx.Profile.gender-ne1-or$ctx.Profile.hp_max-ne2345-or$ctx.Shop.coin-ne888){throw ('clone identity/resources lost: '+($ctx.Profile|ConvertTo-Json -Compress))}
    if($ctx.Cards.Count-ne4){throw 'clone discarded protected cards'}
    if($ctx.Profile.skill_slot_z-ne52000008-or$ctx.Profile.skill_slot_x-ne52000000-or$ctx.Profile.skill_grade8-ne4){throw 'clone discarded skill editor selection'}
    foreach($selector in $ctx.ProfileSelectors){if($selector.SelectedItem.character_name-ne'Fresh'){throw 'selector not switched'}}
    if(Test-Path (Join-Path $root 'nanaimo_inventory_state_v1.dat')){throw 'choosing a name changed active inventory'}
    $ctx.Shop.coin=7654;$ctx.Profile.hp_max=3456
    [void](Sync-LauncherProfileIdentity)
    if($ctx.Shop.coin-ne7654-or$ctx.Profile.hp_max-ne3456){throw 'same identity reloaded and lost edits'}
    [void](Save-InventoryAdminState $ctx $ctx.NameHex $ctx.Shop $ctx.Profile)
    $nameBox.Text='P2';[void](Sync-LauncherProfileIdentity)
    if($ctx.CharacterId-ne22-or$nameBox.Text-ne'Beta'){throw 'username did not select existing character'}
    foreach($selector in $ctx.ProfileSelectors){if($selector.SelectedItem.character_id-ne22){throw 'existing selection did not propagate'}}
    $nameBox.Text='Fresh';[void](Sync-LauncherProfileIdentity)
    if($ctx.Shop.coin-ne7654-or$ctx.Profile.hp_max-ne3456){throw 'saved clone did not reload independently'}
    # An account name has priority over an unrelated character display name.
    $ctx.Profiles=@([pscustomobject]@{username='Other';character_name='P1';name_hex='5031';character_id=33})+$ctx.Profiles
    if((Find-InventoryAdminProfile $ctx 'P1').character_id-ne11){throw 'account identity priority'}
    $ProfileIni=Join-Path $root 'missing-profile.ini'
    function Read-IniProfile{throw 'Old startup profile must not be consulted'}
    function Send-LocalLaunchRegistration($ip,$bytes,$description){$script:registration=[Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json}
    [void](Register-ClientProfile '127.0.0.1')
    if($registration.LocalAccount-ne'Fresh'-or$registration.PureNewPlayer){throw 'clone registration ignored textbox'}
    $nameBox.Text='Alpha';[void](Register-ClientProfile '127.0.0.1')
    if($registration.LocalAccount-ne'P1'){throw 'character display name did not resolve to database username'}
    $nameBox.Text='Fresh';[void](Sync-LauncherProfileIdentity)
    $pureNewPlayerBox.Checked=$true;$nameBox.Text='DoNotCreate'
    [void](Sync-LauncherProfileIdentity)
    if($ctx.SelectedProfile.character_name-ne'Fresh'){throw 'pure mode changed normal selection'}
    $pureNewPlayerBox.Checked=$false
    $oldHex=$ctx.NameHex;$oldCount=$ctx.Profiles.Count;$nameBox.Text=('x'*16)
    try{[void](Sync-LauncherProfileIdentity);throw 'invalid name accepted'}catch{if($_.Exception.Message-eq'invalid name accepted'){throw}}
    if($ctx.NameHex-ne$oldHex-or$ctx.Profiles.Count-ne$oldCount){throw 'failed clone changed selection'}
    $again=Get-InventoryAdminProfiles $root $backend '416C706861'
    if(@($again|Where-Object{$_.character_name-eq'Fresh'}).Count-ne1){throw 'clone missing after reopen'}
    $chineseName='Fresh'+[char]0x4e2d+[char]0x6587
    $nameBox.Text=$chineseName;[void](Sync-LauncherProfileIdentity)
    $again=Get-InventoryAdminProfiles $root $backend '416C706861'
    if(@($again|Where-Object{$_.character_name-eq$chineseName}).Count-ne1-or$ctx.Profile.apartment_recommendation_points-ne456){throw 'Chinese name/resource persistence'}
    Write-Output 'LOCAL_PROFILE_SELECTION_PASS selectors=6 backend=real clone=isolated source=unchanged priority=textbox edits=preserved'
}finally{$form.Dispose();if($nameBox){$nameBox.Dispose()}}
