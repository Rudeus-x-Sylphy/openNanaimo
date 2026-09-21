param(
    [string]$OutputRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'adapter_runtime'),
    [string]$TccPath,
    [string]$DotnetPath,
    [string]$ResourceDataRoot,
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
    (Get-Command dotnet -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue)
) | Where-Object { $_ }
$dotnet = $dotnetCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $dotnet) { throw 'A .NET 8 SDK is required to build the full adapter.' }
$sdk = (& $dotnet --version).Trim()
if ([int]$sdk.Split('.')[0] -lt 8) { throw "A .NET 8 SDK is required; detected $sdk." }
[IO.Directory]::CreateDirectory($output) | Out-Null
$bridge = Join-Path $output 'nanaimo_gameplay_bridge.exe'
& $tcc -I $root -I (Join-Path $root 'adapter') -I (Join-Path $root 'release') (Join-Path $root 'adapter\nanaimo_gameplay_bridge.c') -o $bridge
if ($LASTEXITCODE) { throw 'Native gameplay bridge build failed.' }
& $dotnet publish (Join-Path $root 'managed-host\Nanaimo.Adapter.csproj') -c Release -f net6.0 -r win-x64 --self-contained true -o $output --nologo
if ($LASTEXITCODE) { throw 'Full adapter build failed.' }
$resourceSource = if ($ResourceDataRoot) { [IO.Path]::GetFullPath($ResourceDataRoot) } else { Join-Path $root 'managed\资源\数据' }
if (-not (Test-Path -LiteralPath $resourceSource)) { throw "Adapter data missing. Supply -ResourceDataRoot with the prepared 资源\数据 directory: $resourceSource" }
$resourceTarget = Join-Path $output '资源\数据'
[IO.Directory]::CreateDirectory($resourceTarget) | Out-Null
Copy-Item -Path (Join-Path $resourceSource '*') -Destination $resourceTarget -Force
$adapter = Join-Path $output 'Nanaimo.Adapter.exe'
foreach ($path in @($adapter,$bridge)) { if (-not (Test-Path -LiteralPath $path)) { throw "Build output missing: $path" } }
$files = @($adapter,$bridge) | ForEach-Object {
    [ordered]@{ name = [IO.Path]::GetFileName($_); size = (Get-Item -LiteralPath $_).Length; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
}
$manifest = [ordered]@{
    version = 1
    role = 'full Nanaimo adapter'
    built_at = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    framework = 'net6.0/win-x64/self-contained'
    sdk = $sdk
    files = $files
}
[IO.File]::WriteAllText((Join-Path $output 'adapter_manifest.json'),($manifest | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
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
Write-Output ("FULL_ADAPTER_BUILD_PASS adapter={0} bridge={1} output={2}" -f $files[0].sha256,$files[1].sha256,$output)
