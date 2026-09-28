param([string]$Root=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,[string]$TccPath)
$ErrorActionPreference='Stop'
$tcc=if($TccPath){[IO.Path]::GetFullPath($TccPath)}else{Join-Path $Root 'tools\tcc\tcc.exe'}
$source=Join-Path $PSScriptRoot 'ChannelReentryNativeRegression.c'
$runtime=Join-Path $Root 'release\components\channel_reentry\channel_reentry_runtime.inc'
$current=Join-Path $Root 'release\components\channel_reentry\current_runtime.inc'
$realtime=Join-Path $Root 'release\components\multiplayer_combat\realtime_runtime.inc'
$protocol=Join-Path $Root 'release\components\protocol\protocol.inc'
$gs=Join-Path $Root 'release\components\game_session\gs_runtime.inc'
if(-not(Test-Path -LiteralPath $tcc)){throw "TCC missing: $tcc"}
$runtimeText=Get-Content -Raw -LiteralPath $runtime
$currentText=Get-Content -Raw -LiteralPath $current
$realtimeText=Get-Content -Raw -LiteralPath $realtime
$protocolText=Get-Content -Raw -LiteralPath $protocol
$gsText=Get-Content -Raw -LiteralPath $gs
if($runtimeText -match 'normal allocation' -or $runtimeText -match 'CLOSE_GRACE_MS'){throw 'unsafe channel reentry slot fallback is still present'}
foreach($anchor in @('preferred-reject','channel_reentry_consume_channel_reentry_unlocked','channel_reentry_ticket_epoch_matches(generation','channel_reentry_scene_is_carryable','!g_proxy[preferred].in_use&&!g_multi_backend_active[preferred]&&channel_reentry_consume')){if(-not $runtimeText.Contains($anchor)){throw "missing runtime anchor: $anchor"}}
foreach($anchor in @('channel_reentry_retry_epoch_matches','g_actor_attachment_staged_remote_c47f_target_generation','!g_multi_transport_alive[target]')){if(-not $currentText.Contains($anchor)){throw "missing C47F epoch anchor: $anchor"}}
foreach($anchor in @('g_multi_slot_reentry_scene','reentry_scene_pending')){if(-not $realtimeText.Contains($anchor)){throw "missing scene-carry anchor: $anchor"}}
foreach($anchor in @('g_channel_reentry_scene_selector','p[0x20]','p[0x21]')){if(-not $protocolText.Contains($anchor)){throw "missing C355 scene anchor: $anchor"}}
foreach($anchor in @('channel_reentry_should_restore_scene','restore first C367 synthetic page','current_village_selector=g_multi_conn[multiplayer_idx].village_selector')){if(-not $gsText.Contains($anchor)){throw "missing GS scene restore anchor: $anchor"}}
$out=Join-Path $env:TEMP ('open-nanaimo-channel-reentry-native-'+[guid]::NewGuid().ToString('N')+'.exe')
try{
  & $tcc -I $Root $source -o $out
  if($LASTEXITCODE){throw 'native channel reentry regression compile failed'}
  & $out
  if($LASTEXITCODE){throw 'native channel reentry regression failed'}
  Write-Output 'CHANNEL_REENTRY_SOURCE_CHECK_PASS no-overlap-fallback ticket-generation ticket-commit scene-gate C47F-generation-guard'
}finally{
  if(Test-Path -LiteralPath $out){[IO.File]::Delete($out)}
}