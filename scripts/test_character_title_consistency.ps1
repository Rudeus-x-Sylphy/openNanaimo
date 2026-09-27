param([string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path)
$ErrorActionPreference = 'Stop'
$gui = Join-Path $Root 'gui_launcher/nanaimo_launcher.ps1'
$managed = Join-Path $Root 'managed/Services/NetworkAdapterService.cs'
$release = Join-Path $Root 'release/components/protocol'
$source = Get-Content -LiteralPath $managed -Raw
$checks = @(
    @($source -match 'payload\[5\] = CharacterTitleState\.GetGrade\(session\.Character\)')
    @($source -match 'payload\[0x24 - NativeHeaderLength\] = CharacterTitleState\.GetGrade\(character\)')
    @($source -match 'payload\[3\] = CharacterTitleState\.GetGrade\(character\)')
    @($source -match 'payload\[0x0B\] = CharacterTitleState\.GetGrade\(player\)')
    @((Get-Content (Join-Path $release 'protocol.inc') -Raw) -match 'p\[0x49\].*progression_dungeon_grade_current')
    @((Get-Content (Join-Path $Root 'release/components/protocol_extensions/protocol_overrides.inc') -Raw) -match 'p\[off\+0x07\].*dg\.grade')
)
if($checks -contains $false){throw 'character title carrier source check failed'}
$guiResult = & powershell -NoProfile -ExecutionPolicy Bypass -File $gui -SelfTestTitleIO
if($LASTEXITCODE){throw 'GUI title self-test failed'}
if(($guiResult -join "`n") -notmatch 'CHARACTER_TITLE_IO_PASS'){throw 'GUI title self-test did not report pass'}
Write-Output 'CHARACTER_TITLE_SOURCE_CHECK_PASS carriers=271A,C355,CF71,CF0E,CF88,CF8A release=PASS gui=PASS'
