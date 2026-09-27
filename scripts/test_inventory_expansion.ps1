param(
    [string]$DotnetPath,
    [string]$ResourceDataRoot,
    [switch]$Focused,
    [switch]$LauncherProfile
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $DotnetPath) { $DotnetPath = Join-Path $root '.dotnet/dotnet.exe' }
if (-not (Test-Path -LiteralPath $DotnetPath)) { throw 'Supply -DotnetPath with a .NET 8 SDK.' }
if (-not $ResourceDataRoot) { $ResourceDataRoot = Join-Path $root 'adapter_runtime/资源/数据' }
if (-not (Test-Path -LiteralPath (Join-Path $ResourceDataRoot 'IE._D23'))) {
    throw 'Supply -ResourceDataRoot with the existing prepared client data (including IE._D23).'
}
# Isolated build only: never publish to adapter_runtime or touch running processes.
$artifacts = [IO.Path]::GetFullPath((Join-Path $root 'build/inventory-expansion-checks'))
$buildRoot = [IO.Path]::GetFullPath((Join-Path $root 'build')) + [IO.Path]::DirectorySeparatorChar
if (-not $artifacts.StartsWith($buildRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe build path.' }
& $DotnetPath build (Join-Path $root 'tests/InventoryExpansion/InventoryExpansion.csproj') --artifacts-path $artifacts -c Release --nologo
if ($LASTEXITCODE) { throw "Expansion check build failed ($LASTEXITCODE)." }
$output = Join-Path $artifacts 'bin/InventoryExpansion/release'
$data = Join-Path $output '资源/数据'
[IO.Directory]::CreateDirectory($data) | Out-Null
Get-ChildItem -LiteralPath $ResourceDataRoot -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $data -Force
}
$checkArgs = @()
if ($Focused) { $checkArgs += '--focused' }
if ($LauncherProfile) { $checkArgs += '--launcher-profile' }
& $DotnetPath (Join-Path $output 'Nanaimo.Adapter.dll') @checkArgs
if ($LASTEXITCODE) { throw "Expansion checks failed ($LASTEXITCODE)." }
