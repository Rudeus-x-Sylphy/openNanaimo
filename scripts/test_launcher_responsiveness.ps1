param([string]$EvidencePath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$root=Split-Path $PSScriptRoot -Parent
# Load function definitions only: do not initialize the production database,
# launch clients, stop adapters, or mutate the user-owned client tree.
foreach($relative in @('gui_launcher/nanaimo_launcher.ps1','gui_launcher/inventory_admin_gui.ps1','gui_launcher/projectile_browser.ps1','gui_launcher/launcher_worker.ps1')){
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root $relative),[ref]$tokens,[ref]$errors)
    if($errors){throw ($errors|Out-String)}
    foreach($node in $ast.FindAll({param($n)$n-is[Management.Automation.Language.FunctionDefinitionAst]},$true)){. ([scriptblock]::Create($node.Extent.Text))}
}
foreach($name in @('Client','Adapter','AdapterBridge','AdapterManifest','AdapterRuntimeRoot','LegacyAdapter','AdapterData','AdapterLogs','AdapterStop','AdapterLog','AdapterErr','RuntimeIdentityPath','ClientCompatibilityTool','ClientCompatibilityOverlay','ClientCompatibilityReport','SocialClientLauncher','SocialAdapterIpState','ProfileIni','ProfileJson','ProfileStateRoot','ExpectedAdapterSize','ExpectedAdapterHash','ExpectedBridgeSize','ExpectedBridgeHash','ExpectedAdapterDllSize','ExpectedAdapterDllHash','PureNewPlayerProfile','PureNewPlayerAccountState','ActiveGameOption')){Set-Variable -Name $name -Value ''}
$AdapterLogs=Join-Path $root 'build\launcher-worker-check-logs'
$script:LauncherSessionCache=@{timings=(New-Object Collections.ArrayList)}
$form=New-Object Windows.Forms.Form;$tabs=New-Object Windows.Forms.TabControl;$status=New-Object Windows.Forms.Label;$socialStatus=New-Object Windows.Forms.Label
$form.Controls.Add($tabs);$form.Controls.Add($status)
$script:LauncherWorkTimer=New-Object Windows.Forms.Timer;$script:LauncherWorkTimer.Interval=20
$script:LauncherWorkTimer.add_Tick({Complete-LauncherWork})
$script:pulses=0;$pulse=New-Object Windows.Forms.Timer;$pulse.Interval=15;$pulse.add_Tick({$script:pulses++})
$script:completed=$false;$uiThread=[Threading.Thread]::CurrentThread.ManagedThreadId
Start-LauncherWork 'worker-check' {param($data)if($form){throw 'UI control leaked into worker'};Start-Sleep -Milliseconds 650;[Threading.Thread]::CurrentThread.ManagedThreadId} @{} {
    param($result)
    if([Threading.Thread]::CurrentThread.ManagedThreadId-ne$uiThread){throw 'callback not on UI thread'}
    if($result[-1]-eq$uiThread){throw 'work ran on UI thread'}
    $script:completed=$true;$form.Close()
}
try{Start-LauncherWork 'illegal-reentry' {} @{} {};throw 'reentry allowed'}catch{if($_.Exception.Message-notmatch'已有启动器操作'){throw}}
$deadline=New-Object Windows.Forms.Timer;$deadline.Interval=10000;$deadline.add_Tick({$form.Close()})
$pulse.Start();$deadline.Start();[void]$form.ShowDialog()
if(-not$script:completed-or$script:pulses-lt10-or$script:LauncherWork-or-not$tabs.Enabled){throw "worker responsiveness failure completed=$script:completed pulses=$script:pulses"}
# Real completion handler handles worker failure, restores controls, and
# permits a subsequent chained operation without showing a blocking dialog.
$script:failureObserved=$false
Start-LauncherWork 'failure-check' {throw 'EXPECTED_WORKER_FAILURE'} @{} {} {
    param($message)
    if($message-notmatch'EXPECTED_WORKER_FAILURE'){throw $message}
    $script:failureObserved=$true;$form.Close()
}
[void]$form.ShowDialog()
if(-not$script:failureObserved-or$script:LauncherWork-or-not$tabs.Enabled){throw 'failed work left GUI busy'}
$script:chainCompleted=$false
Start-LauncherWork 'chain-one' {return 1} @{} {
    param($result)
    Start-LauncherWork 'chain-two' {return 2} @{} {param($result);$script:chainCompleted=($result[-1]-eq2);$form.Close()}
}
[void]$form.ShowDialog()
if(-not$script:chainCompleted-or$script:LauncherWork){throw 'chained operation was blocked or lost'}
# Exercise the real compatibility cache with a native-tools stub; no retail
# client assets are read or changed by this construction check.
$fixture=Join-Path $root ('build/launcher-cache-check-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture)|Out-Null
$Root=$fixture;$Client=Join-Path $fixture 'game.exe';$AdapterRuntimeRoot=$fixture
$AdapterManifest=Join-Path $fixture 'adapter_manifest.json';$ClientCompatibilityReport=Join-Path $fixture 'report.json';$ClientCompatibilityOverlay=Join-Path $fixture 'overlay'
[IO.File]::WriteAllText($Client,'test-client')
[IO.File]::WriteAllText($AdapterManifest,'{"launcher_tools":1}')
$probe=Join-Path $fixture 'probe.bin';[IO.File]::WriteAllText($probe,'a')
[IO.Directory]::CreateDirectory((Join-Path $fixture 'gui_launcher'))|Out-Null
[IO.File]::WriteAllText((Join-Path $fixture 'gui_launcher/level200_client_check.ps1'),"param(`$Client) 'STUB_LEVEL200_CHECK'")
$ClientCompatibilityTool=Join-Path $fixture 'tool.ps1'
[IO.File]::WriteAllText($ClientCompatibilityTool,'$global:compatibilityRuns++
$operations=@(''level200_town_title_mask'',''level200_town_level_shift'',''level200_town_level_mask'',''level200_entry_code'',''level200_entry_hook'')|ForEach-Object{@{operation=$_}}
$report=@{verification=@{all_pass=$true};operations=$operations;session_inputs=@(''game.exe'',''probe.bin'')}
[IO.File]::WriteAllText($ClientCompatibilityReport,($report|ConvertTo-Json -Depth 6))
$global:LASTEXITCODE=0
')
$global:compatibilityRuns=0;$script:LauncherSessionCache=@{}
[void](Ensure-ClientCompatibility);[void](Ensure-ClientCompatibility)
if($global:compatibilityRuns-ne1){throw "unchanged shared preparation did not reuse: $global:compatibilityRuns"}
[IO.File]::WriteAllText($probe,'changed')
[void](Ensure-ClientCompatibility)
if($global:compatibilityRuns-ne2){throw 'changed source did not invalidate cache'}
Remove-Item -LiteralPath $probe
[void](Ensure-ClientCompatibility)
if($global:compatibilityRuns-ne3){throw 'missing source did not invalidate cache'}
$proof="LAUNCHER_WORKER_PASS gui_timer_pulses=$script:pulses worker_thread=true ui_callback=true reentry_guard=true native_failure_propagation=true failure_unlock=true chained_work=true compatibility_reuse=true invalidation=true runtime_acceptance=false"
if($EvidencePath){[IO.File]::WriteAllText([IO.Path]::GetFullPath($EvidencePath),$proof+"`r`n",(New-Object Text.UTF8Encoding($false)))}
Write-Output $proof
$pulse.Dispose();$deadline.Dispose();$script:LauncherWorkTimer.Dispose();$form.Dispose()
