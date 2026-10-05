$ErrorActionPreference='Stop'
$Root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$rootPrefix=$Root.TrimEnd('\')+'\'
$active=@(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($rootPrefix,[StringComparison]::OrdinalIgnoreCase) -and $_.Name -match '^(Nanaimo\.Adapter|nanaimo_gameplay_bridge|nanaimo_adapter|nanaimo_adapter_testports|nanaimo_client|game_unpack|game)\.exe$' })
if($active.Count -gt 0){throw 'Client/service still running. Close it explicitly before clearing private state.'}
$adapter=Join-Path $Root 'adapter_runtime\Nanaimo.Adapter.exe'
$manifest=Get-Content -LiteralPath (Join-Path $Root 'adapter_runtime\adapter_manifest.json') -Raw -Encoding UTF8|ConvertFrom-Json
if([int]$manifest.launcher_tools-lt1){throw 'Managed storage tools are required; refusing legacy file deletion.'}
& $adapter --tools state clear-legacy --root $Root
if($LASTEXITCODE){throw 'Database state clear refused.'}
# Standalone diagnostic runs have their own local game.db; normalized account
# tables and drafts are deliberately not cleared by this legacy-cache command.
$standaloneDb=Join-Path $Root 'game.db'
if(Test-Path -LiteralPath $standaloneDb){& $adapter --tools state clear-legacy --root $Root --database $standaloneDb;if($LASTEXITCODE){throw 'Standalone database state clear refused.'}}
$archive=[IO.Path]::GetFullPath((Join-Path $Root ('adapter_data\cleanup_archive\'+[guid]::NewGuid().ToString('N'))))
if(-not$archive.StartsWith($rootPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe cleanup archive.'}
New-Item -ItemType Directory -Path $archive -Force|Out-Null
$patterns=@('adapter_*.log','*_stderr.log','nanaimo_launcher_profile.ini','nanaimo_launcher_profile.json','adapter_ip.txt')
$rows=@()
foreach($pattern in $patterns){foreach($file in @(Get-ChildItem -LiteralPath $Root -Filter $pattern -File -ErrorAction SilentlyContinue)){
    $source=[IO.Path]::GetFullPath($file.FullName);$target=[IO.Path]::GetFullPath((Join-Path $archive $file.Name))
    if(-not$source.StartsWith($rootPrefix,[StringComparison]::OrdinalIgnoreCase)-or-not$target.StartsWith($archive+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe cleanup target.'}
    $rows+=[ordered]@{name=$file.Name;size=$file.Length;sha256=(Get-FileHash -LiteralPath $source).Hash}
    Move-Item -LiteralPath $source -Destination $target
}}
[IO.File]::WriteAllText((Join-Path $archive 'receipt.json'),($rows|ConvertTo-Json -Depth 4),[Text.UTF8Encoding]::new($false))
Write-Host 'Legacy state cleared in SQLite; original DAT, migration archives, inventory backups and character accounts preserved.'
