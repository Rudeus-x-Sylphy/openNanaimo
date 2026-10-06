param([string]$DotnetPath, [string]$ResourceDataRoot)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $DotnetPath) { $DotnetPath = Join-Path $root '.dotnet/dotnet.exe' }
if (-not $ResourceDataRoot) { $ResourceDataRoot = Join-Path $root 'adapter_runtime/资源/数据' }
$artifacts = Join-Path $root 'build/character-combat-progression-checks'
# Isolated test artifacts; published runtime and running processes are untouched.
& $DotnetPath build (Join-Path $root 'tests/CharacterCombatProgression/CharacterCombatProgression.csproj') --artifacts-path $artifacts -c Release --nologo
if ($LASTEXITCODE) { throw "Progression check build failed ($LASTEXITCODE)." }
$output = Join-Path $artifacts 'bin/CharacterCombatProgression/release'
$data = Join-Path $output '资源/数据'
[IO.Directory]::CreateDirectory($data) | Out-Null
Get-ChildItem -LiteralPath $ResourceDataRoot -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $data -Force
}
& $DotnetPath (Join-Path $output 'Nanaimo.Adapter.dll')
if ($LASTEXITCODE) { throw "Progression checks failed ($LASTEXITCODE)." }
