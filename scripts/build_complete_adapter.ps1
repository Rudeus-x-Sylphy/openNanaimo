param(
    [string]$OutputRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'adapter_runtime'),
    [string]$TccPath,
    [string]$DotnetPath,
    [string]$ResourceDataRoot,
    [string]$TargetFramework,
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputRoot)
$tccCandidates = @(
    $TccPath,
    (Join-Path $root 'tools\tcc\tcc.exe')
) | Where-Object { $_ }
$tcc = $tccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $tcc) { throw 'TinyCC not found. Supply -TccPath or install tools\tcc\tcc.exe.' }
$dotnetCandidates = @(
    $DotnetPath,
    (Join-Path $root '.dotnet\dotnet.exe'),
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
) | Where-Object { $_ }
$dotnet = $dotnetCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $dotnet) { throw 'A .NET 8 SDK is required to build the full adapter.' }
$sdk = (& $dotnet --version).Trim()
if ([int]$sdk.Split('.')[0] -lt 8) { throw "A .NET 8 SDK is required; detected $sdk." }
if (-not $TargetFramework) {
    $refPacks = Join-Path (Split-Path $dotnet -Parent) 'packs\Microsoft.NETCore.App.Ref'
    $TargetFramework = if (Test-Path (Join-Path $refPacks '6.*')) { 'net6.0' } else { 'net8.0' }
}
if ($TargetFramework -notin @('net6.0','net8.0')) { throw "TargetFramework must be net6.0 or net8.0; got $TargetFramework." }
[IO.Directory]::CreateDirectory($output) | Out-Null
$bridge = Join-Path $output 'nanaimo_gameplay_bridge.exe'
& $tcc -I $root -I (Join-Path $root 'adapter') -I (Join-Path $root 'release') (Join-Path $root 'adapter\nanaimo_gameplay_bridge.c') -o $bridge
if ($LASTEXITCODE) { throw 'Native gameplay bridge build failed.' }
& $dotnet publish (Join-Path $root 'managed-host\Nanaimo.Adapter.csproj') -c Release -f $TargetFramework -r win-x64 --self-contained true -o $output --nologo
if ($LASTEXITCODE) { throw 'Full adapter build failed.' }
$resourceRoot = Join-Path $root 'adapter_runtime'
$resourceContainerName = [string][char]0x8D44 + [char]0x6E90
$resourceDataName = [string][char]0x6570 + [char]0x636E
$resourceSource = if ($ResourceDataRoot) { [IO.Path]::GetFullPath($ResourceDataRoot) } else {
    $resourceContainer = Get-ChildItem -LiteralPath $resourceRoot -Directory | Where-Object Name -eq $resourceContainerName | Select-Object -First 1
    if (-not $resourceContainer) { throw "Adapter resource container missing under $resourceRoot." }
    Join-Path $resourceContainer.FullName $resourceDataName
}
if (-not (Test-Path -LiteralPath $resourceSource)) { throw "Adapter data missing. Supply -ResourceDataRoot with the prepared resource directory: $resourceSource" }
$resourceTarget = Join-Path $output (Join-Path $resourceContainerName $resourceDataName)
[IO.Directory]::CreateDirectory($resourceTarget) | Out-Null
if(-not [IO.Path]::GetFullPath($resourceSource).TrimEnd('\').Equals([IO.Path]::GetFullPath($resourceTarget).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)){Copy-Item -Path (Join-Path $resourceSource '*') -Destination $resourceTarget -Force}
$adapter = Join-Path $output 'Nanaimo.Adapter.exe'
foreach ($path in @($adapter,$bridge)) { if (-not (Test-Path -LiteralPath $path)) { throw "Build output missing: $path" } }
$manifestPath = Join-Path $output 'adapter_manifest.json'
$files = Get-ChildItem -LiteralPath $output -Recurse -File | Where-Object {
    -not [IO.Path]::GetFullPath($_.FullName).Equals([IO.Path]::GetFullPath($manifestPath),[StringComparison]::OrdinalIgnoreCase)
} | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetFullPath($_.FullName).Substring($output.Length).TrimStart('\','/').Replace('\','/')
    [ordered]@{ name = $relative; size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
$manifest = [ordered]@{
    version = 1
    role = 'full Nanaimo adapter'
    built_at = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    framework = "$TargetFramework/win-x64/self-contained"
    sdk = $sdk
    files = $files
}
$manifestJson = ($manifest | ConvertTo-Json -Depth 5).Replace("`r`n", "`n")
[IO.File]::WriteAllText($manifestPath,$manifestJson,[Text.UTF8Encoding]::new($false))
if ($SelfTest) {
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    $testRoot = [IO.Path]::GetFullPath((Join-Path $tempRoot ('open-nanaimo-full-adapter-' + [guid]::NewGuid().ToString('N'))))
    if (-not $testRoot.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe self-test path: $testRoot" }
    [IO.Directory]::CreateDirectory($testRoot) | Out-Null
    try {
        $profile = Join-Path $testRoot 'profile.ini'
        [IO.File]::WriteAllText($profile,"version=2`r`nname_hex=41444150544552`r`nlevel=25`r`ngender=1`r`npet=15009205`r`npet_age_a=3`r`npet_age_b=3`r`nhp_max=1500`r`nhp_current=1500`r`nmp_max=500`r`nmp_current=500`r`ncoin=999999`r`nnana_point=999999`r`n",[Text.Encoding]::ASCII)
        $logs = Join-Path $testRoot 'logs'
        & $adapter --self-test --data (Join-Path $testRoot 'data') --native $bridge --profile $profile --login-port 53005 --world-port 53050 --profile-port 53999 --log-directory $logs
        if ($LASTEXITCODE) {
            $detail = if (Test-Path -LiteralPath (Join-Path $logs 'adapter-error.log')) { Get-Content -Raw -LiteralPath (Join-Path $logs 'adapter-error.log') } else { 'No adapter error log.' }
            throw "Full adapter self-test failed: $detail"
        }
        $log = Join-Path $logs 'adapter.log'
        if (-not (Test-Path -LiteralPath $log) -or (Get-Content -Raw -LiteralPath $log) -notmatch 'MIGRATION_CHECKS_PASS') { throw 'Full adapter self-test did not reach MIGRATION_CHECKS_PASS.' }
        Write-Output "FULL_ADAPTER_SELFTEST_PASS $log"
    } finally {
        if ([IO.Directory]::Exists($testRoot)) { [IO.Directory]::Delete($testRoot,$true) }
    }
}
$adapterRow=@($files|Where-Object name -eq 'Nanaimo.Adapter.exe')[0]
$bridgeRow=@($files|Where-Object name -eq 'nanaimo_gameplay_bridge.exe')[0]
Write-Output ("FULL_ADAPTER_BUILD_PASS adapter={0} bridge={1} files={2} output={3}" -f $adapterRow.sha256,$bridgeRow.sha256,$files.Count,$output)
