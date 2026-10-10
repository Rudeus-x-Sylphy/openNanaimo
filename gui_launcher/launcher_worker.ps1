# Runspace worker bridge. Only detached snapshots and backend functions cross
# the boundary; WinForms controls are read/written exclusively by the UI thread.
function Start-LauncherWork([string]$Stage,[scriptblock]$Work,[hashtable]$Data,[scriptblock]$Completed,[scriptblock]$Failed=$null){
    if($script:LauncherWork){throw '已有启动器操作在执行，请等待完成。'}
    $initial=[Management.Automation.Runspaces.InitialSessionState]::CreateDefault()
    foreach($name in @('Assert-ManagedLauncherTools','Test-ClientBinary','Test-AdapterBinary','Get-LocalAdapters','Stop-LocalAdapter','Test-AdapterPort','Get-AdapterListenerOwners','Assert-AdapterPortsAvailable','Assert-StartedAdapterListeners','Write-RuntimeIdentity','Start-LocalAdapter','Send-LocalLaunchRegistration','Register-SocialAccount','Normalize-NetworkIPv4','Ensure-ClientCompatibility','Get-CompatibilityFingerprint','Get-InventoryAdminProfiles','Refresh-InventoryAdminProfiles','Invoke-InventoryAdminBackend','Get-InventoryBackendPipe','Send-InventoryBackendRequest','Write-InventoryAdminTempJson','Save-InventoryAdminState','Invoke-LauncherState','Write-DungeonGradeState','Read-ProjectileSettings','Find-InventoryAdminProfile','Set-InventoryAdminProfile','Import-InventoryAdminSnapshot','Read-InventoryAdminSnapshot','Ensure-InventoryAdminProfile','Add-InventoryAdminProfileToSelectors','ConvertTo-InventoryAdminNameHex','Write-LauncherProfileRequest','Assert-LocalAdapterRunning','Write-PureNewPlayerRuntimeProfile','Register-PureNewPlayer','New-PureNewPlayerAccountName','Save-PureNewPlayerUsername','Register-ClientProfile','Install-LaunchModeConfig','Get-LaunchModeConfigText','Test-LaunchModeTemplate','Test-LaunchModeConfig','Start-SocialParticipant')){
        $fn=Get-Command $name -CommandType Function
        $initial.Commands.Add((New-Object Management.Automation.Runspaces.SessionStateFunctionEntry($name,$fn.Definition)))
    }
    foreach($name in @('Root','Client','Adapter','AdapterBridge','AdapterManifest','AdapterRuntimeRoot','LegacyAdapter','AdapterData','AdapterLogs','AdapterStop','AdapterLog','AdapterErr','RuntimeIdentityPath','ClientCompatibilityTool','ClientCompatibilityOverlay','ClientCompatibilityReport','SocialClientLauncher','SocialAdapterIpState','ProfileIni','ProfileJson','ProfileStateRoot','PSScriptRoot','ExpectedAdapterSize','ExpectedAdapterHash','ExpectedBridgeSize','ExpectedBridgeHash','ExpectedAdapterDllSize','ExpectedAdapterDllHash','PureNewPlayerProfile','PureNewPlayerAccountState','ActiveGameOption')){
        $initial.Variables.Add((New-Object Management.Automation.Runspaces.SessionStateVariableEntry($name,(Get-Variable $name -ValueOnly),'')))
    }
    # Shared cache is launcher-session scoped. Only one operation may mutate it.
    $initial.Variables.Add((New-Object Management.Automation.Runspaces.SessionStateVariableEntry('InventoryBackendSessions',@{},'')))
    $initial.Variables.Add((New-Object Management.Automation.Runspaces.SessionStateVariableEntry('LauncherSessionCache',$script:LauncherSessionCache,'')))
    $progress=[hashtable]::Synchronized(@{Stage=$Stage})
    $initial.Variables.Add((New-Object Management.Automation.Runspaces.SessionStateVariableEntry('LauncherProgress',$progress,'')))
    $runspace=[Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace($initial);$runspace.ApartmentState='STA';$runspace.Open()
    $pipeline=[PowerShell]::Create();$pipeline.Runspace=$runspace
    [void]$pipeline.AddScript('param($work,$data) $ErrorActionPreference="Stop"; & ([scriptblock]::Create($work)) $data').AddArgument($Work.ToString()).AddArgument($Data)
    try{$async=$pipeline.BeginInvoke()}catch{$pipeline.Dispose();$runspace.Dispose();throw}
    $script:LauncherWork=@{Pipeline=$pipeline;Runspace=$runspace;Async=$async;Completed=$Completed;Failed=$Failed;Progress=$progress;Watch=[Diagnostics.Stopwatch]::StartNew();Stage=$Stage;LastStage=$Stage;StageStart=0.0;Phases=(New-Object Collections.ArrayList)}
    $tabs.Enabled=$false;$status.Text=$Stage;$socialStatus.Text=$Stage;$script:LauncherWorkTimer.Start()
}
function Complete-LauncherWork {
    $job=$script:LauncherWork;if(-not$job){return}
    if($job.Progress.Stage-ne$job.LastStage){[void]$job.Phases.Add(@{stage=$job.LastStage;seconds=$job.Watch.Elapsed.TotalSeconds-$job.StageStart});$job.LastStage=$job.Progress.Stage;$job.StageStart=$job.Watch.Elapsed.TotalSeconds}
    $message=('{0}（{1:N1} 秒）'-f$job.Progress.Stage,$job.Watch.Elapsed.TotalSeconds)
    $status.Text=$message;$socialStatus.Text=$message
    if(-not$job.Async.IsCompleted){return}
    $script:LauncherWorkTimer.Stop()
    try{
        $result=@($job.Pipeline.EndInvoke($job.Async))
        if($job.Pipeline.HadErrors){throw ($job.Pipeline.Streams.Error|Out-String)}
        $script:LauncherWork=$null;$tabs.Enabled=$true
        &$job.Completed $result
    }catch{$status.Text='操作失败：'+$_.Exception.Message;$socialStatus.Text=$status.Text;if($job.Failed){&$job.Failed $_.Exception.Message}else{[Windows.Forms.MessageBox]::Show($_.Exception.Message,'启动器操作失败')|Out-Null}}
    finally{
        [void]$job.Phases.Add(@{stage=$job.LastStage;seconds=$job.Watch.Elapsed.TotalSeconds-$job.StageStart})
        $timing=[pscustomobject]@{stage=$job.Stage;seconds=$job.Watch.Elapsed.TotalSeconds;phases=@($job.Phases);failed=$job.Pipeline.HadErrors;at=(Get-Date).ToString('o')}
        [void]$script:LauncherSessionCache.timings.Add($timing)
        try{[IO.Directory]::CreateDirectory($AdapterLogs)|Out-Null;[IO.File]::AppendAllText((Join-Path $AdapterLogs 'launcher-timings.jsonl'),($timing|ConvertTo-Json -Depth 6 -Compress)+"`n",(New-Object Text.UTF8Encoding($false)))}catch{}
        $job.Pipeline.Dispose();$job.Runspace.Dispose();if($script:LauncherWork-eq$job){$script:LauncherWork=$null};if(-not$script:LauncherWork){$tabs.Enabled=$true}
    }
}
function Get-CompatibilityFingerprint {
    # Metadata invalidation is a performance cache, not a cryptographic proof.
    # Full derivation/verification runs again when any input identity changes.
    $paths=@($Client,$ClientCompatibilityTool,$AdapterManifest,(Join-Path $AdapterRuntimeRoot 'Nanaimo.Adapter.dll'),(Join-Path $AdapterRuntimeRoot 'Nanaimo.Gameplay.dll'),$ClientCompatibilityReport,(Join-Path $Root 'nanaimo_projectile.ini'),(Join-Path $Root 'manifest\patch_runtime_requirements.json'))
    $paths+=@((Join-Path $Root 'openNanaimo-l7-l8-resources.json'),(Join-Path $Root '.openNanaimo-korean-pets.json'))
    if($LauncherSessionCache.compatibilityReport){
        foreach($relative in $LauncherSessionCache.compatibilityReport.session_inputs){
            if([IO.Path]::IsPathRooted($relative)-or@($relative -split '[\/]')-contains'..'){throw 'Unsafe compatibility cache input'}
            $paths+=Join-Path $Root $relative
        }
    }
    return (@($paths|Sort-Object -Unique|ForEach-Object{if(Test-Path -LiteralPath $_){$f=Get-Item -LiteralPath $_;''+$f.FullName+'|'+$f.Length+'|'+$f.LastWriteTimeUtc.Ticks+'|'+$f.CreationTimeUtc.Ticks+'|'+[int]$f.Attributes}else{'missing|'+$_}})-join "`n")
}
function Get-LauncherEditorFingerprint {
    $values=@($nameBox.Text,$levelBox.Value,$genderCombo.SelectedIndex,$titleCombo.SelectedIndex,$pureNewPlayerBox.Checked,$skipTutorialBox.Checked,$unlockAllDungeonsBox.Checked,$petCombo.SelectedIndex,$petAgeCombo.SelectedIndex,$attackCombo.SelectedIndex,$projectileRouteCombo.SelectedIndex,$meatRouteCombo.SelectedIndex,$skillZCombo.SelectedIndex,$skillXCombo.SelectedIndex,$skillSlotExpiryApplyBox.Checked)
    foreach($control in @($comboMap.Values)+@($skillGradeBoxes)+@($hpMaxBox,$mpMaxBox,$attackBox,$defenseBox,$coinBox,$nanaPointBox,$apartmentPointsBox,$cardKeyNormalBox,$cardKeyGoldBox,$cardKeyMysteryBox,$cardKeySpecialBox,$freeMagicKeyExpiryBox,$quickbarExpiryBox,$skillSlotExpiryBox)){
        if($control-is[Windows.Forms.ComboBox]){$values+=$control.SelectedIndex}else{$values+=$control.Value}
    }
    return (($values -join '|')+'|'+([ordered]@{clothing=@($inventoryAdmin.Clothing);pets=@($inventoryAdmin.Pets);game=@($inventoryAdmin.GameItems);furniture=@($inventoryAdmin.Furniture);cards=@($inventoryAdmin.Cards)}|ConvertTo-Json -Depth 8 -Compress))
}
function New-LauncherInventorySnapshot {
    $source=@{Root=$inventoryAdmin.Root;Backend=$inventoryAdmin.Backend;Profiles=$inventoryAdmin.Profiles;NameHex=$inventoryAdmin.NameHex;CharacterId=$inventoryAdmin.CharacterId;SelectedProfile=$inventoryAdmin.SelectedProfile;Shop=$inventoryAdmin.Shop;Profile=$inventoryAdmin.Profile;AccountSuffix=$inventoryAdmin.AccountSuffix;Clothing=@($inventoryAdmin.Clothing);Pets=@($inventoryAdmin.Pets);GameItems=@($inventoryAdmin.GameItems);Furniture=@($inventoryAdmin.Furniture);Cards=@($inventoryAdmin.Cards)}
    $copy=$source|ConvertTo-Json -Depth 12 -Compress|ConvertFrom-Json
    foreach($name in @('Clothing','Pets','GameItems','Furniture','Cards')){$list=New-Object Collections.ArrayList;foreach($row in $copy.$name){[void]$list.Add($row)};$copy.$name=$list}
    $copy|Add-Member ProfileSelectors @();$copy|Add-Member ProfileSelectorSync $false;$copy|Add-Member RefreshAll $null;$copy|Add-Member ProfileChanged $null
    return $copy
}
function Start-SaveLauncherProfile([switch]$ProfileOnly){
    if($pureNewPlayerBox.Checked){throw '纯新手档不会保存或导入 GUI 角色配置。'}
    Capture-LauncherProfileEditorState
    $data=@{Context=(New-LauncherInventorySnapshot);Identity=$nameBox.Text;ProfileOnly=[bool]$ProfileOnly}
    Start-LauncherWork '解析所选档案身份（单次查询）' {
        param($data)
        if((Get-LocalAdapters).Count){throw '请先停止本项目适配器，再保存离线档案。'}
        [void](Ensure-InventoryAdminProfile $data.Context $data.Identity)
        return $data.Context
    } $data ({
        param($result)
        $resolved=$result[-1]
        Apply-LauncherInventoryContext $inventoryAdmin $resolved
        if($ProfileOnly){
            Capture-LauncherProfileEditorState
            Start-LauncherWork '保存所选档案' {param($data)if((Get-LocalAdapters).Count){throw '适配器正在运行，不能离线保存。'};Save-InventoryAdminState $data.Context $data.Context.NameHex $data.Context.Shop $data.Context.Profile -IdentityResolved -TitleGrade $data.TitleGrade} @{Context=(New-LauncherInventorySnapshot)} {param($result)$status.Text="所选档案已保存；备份：$($result[-1].backup)"}
        }else{Save-Profile -Background -IdentityResolved}
    }.GetNewClosure())
}
function Write-LauncherProfileRequest($request){
    if((Get-LocalAdapters).Count){throw '适配器正在运行，不能离线保存。'}
    $LauncherProgress.Stage='保存数据库与库存（离线备份、事务）'
    $adminResult=Save-InventoryAdminState $request.Context $request.Hex $request.Shop -IdentityResolved -TitleGrade $request.TitleGrade
    $LauncherProgress.Stage='写入启动配置与称号'
    [IO.File]::WriteAllLines($ProfileIni,[string[]]$request.Lines,(New-Object Text.ASCIIEncoding))
    $titleStatePath=$null
    if($request.TitleGrade-ge0-and$adminResult.source-ne'database'){$titleStatePath=Write-DungeonGradeState $ProfileStateRoot $request.Hex $request.TitleGrade}
    $request.View.title.state_file=$titleStatePath
    $request.View.inventory_admin.backup=$adminResult.backup
    $request.View.inventory_admin.account_suffix=$adminResult.account_suffix
    [IO.File]::WriteAllText($ProfileJson,($request.View|ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
    return $adminResult
}
function Apply-LauncherInventoryContext($context,$resolved){
    $changed=([string]$context.NameHex-ne[string]$resolved.NameHex-or[string]$context.CharacterId-ne[string]$resolved.CharacterId)
    foreach($name in @('Profiles','NameHex','CharacterId','SelectedProfile','Shop','Profile','AccountSuffix')){$context.$name=$resolved.$name}
    foreach($name in @('Clothing','Pets','GameItems','Furniture','Cards')){$context.$name.Clear();foreach($row in $resolved.$name){[void]$context.$name.Add($row)}}
    if($changed){&$context.RefreshAll;&$context.ProfileChanged $context}
    $context.ProfileSelectorSync=$true
    try{foreach($selector in $context.ProfileSelectors){$selector.Items.Clear();foreach($profile in $resolved.Profiles){[void]$selector.Items.Add($profile)};for($i=0;$i-lt$selector.Items.Count;$i++){if([string]$selector.Items[$i].name_hex-eq$resolved.NameHex-and[string]$selector.Items[$i].character_id-eq[string]$resolved.CharacterId){$selector.SelectedIndex=$i;break}}}}finally{$context.ProfileSelectorSync=$false}
    Update-LaunchPreview
}
function Start-SelectLauncherProfile($context,$profile){
    $previous=New-LauncherInventorySnapshot
    Start-LauncherWork '载入所选档案' {param($data);Set-InventoryAdminProfile $data.Context $data.Profile;return $data.Context} @{Context=(New-LauncherInventorySnapshot);Profile=$profile} ({
        param($result);Apply-LauncherInventoryContext $context $result[-1];$status.Text='已载入所选档案。'
    }.GetNewClosure()) ({
        param($message);Apply-LauncherInventoryContext $context $previous;$status.Text='档案载入失败：'+$message
    }.GetNewClosure())
}
function Start-LocalClientWork($selected=$null){
    $info=Get-SelectedLaunchModeInfo
    $data=@{Info=$info;Pure=[bool]$pureNewPlayerBox.Checked;RequestedUsername=$pureNewPlayerUsernameBox.Text;Selected=$selected;Pet=(Get-SelectedData $petCombo)}
    Start-LauncherWork '准备并启动本地客户端' {
        param($data)
        Assert-LocalAdapterRunning
        if($data.Pure){[void](Write-PureNewPlayerRuntimeProfile)}
        Test-ClientBinary;Install-LaunchModeConfig $data.Info
        $compatibility=Ensure-ClientCompatibility $data.Pet
        $LauncherProgress.Stage='注册账号并启动客户端（复用现有服务）'
        $account=if($data.Pure){Register-PureNewPlayer $data.Info.AdapterIP $data.RequestedUsername}else{Register-ClientProfile $data.Info.AdapterIP $data.Selected}
        if($data.Info.ClientArgs.Count){Start-Process -FilePath $Client -ArgumentList ([string[]]$data.Info.ClientArgs) -WorkingDirectory $Root|Out-Null}else{Start-Process -FilePath $Client -WorkingDirectory $Root|Out-Null}
        return $account
    } $data {param($result);$status.Text="账号 $($result[-1]) 已注册到现有适配器，客户端已启动。"}
}
