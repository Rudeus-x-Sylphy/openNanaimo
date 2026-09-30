param([string]$DotnetPath)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $DotnetPath) { $DotnetPath = Join-Path $root '.dotnet/dotnet.exe' }
if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
    throw 'Specify -DotnetPath with a .NET 8 SDK executable.'
}
$project = Join-Path $root 'tests/FriendReciprocityRegression/FriendReciprocityRegression.csproj'
$artifacts = Join-Path $root 'tests/FriendReciprocityRegression/obj/artifacts'
& $DotnetPath build $project -c Release --artifacts-path $artifacts --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Friend regression build failed ($LASTEXITCODE)." }
& $DotnetPath (Join-Path $artifacts 'bin/FriendReciprocityRegression/release/Nanaimo.Adapter.dll')
if ($LASTEXITCODE -ne 0) { throw "Friend regression failed ($LASTEXITCODE)." }
