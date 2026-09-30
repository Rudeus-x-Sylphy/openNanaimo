param([string]$DotnetPath)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(-not$DotnetPath){$DotnetPath=Join-Path $root '.dotnet/dotnet.exe'}
$artifacts=Join-Path ([IO.Path]::GetTempPath()) ('nanaimo-local-registration-build-'+[guid]::NewGuid().ToString('N'))
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('nanaimo-local-registration-data-'+[guid]::NewGuid().ToString('N'))
& $DotnetPath build (Join-Path $PSScriptRoot 'LocalProfileRegistrationRegression.csproj') --artifacts-path $artifacts --nologo -v quiet
if($LASTEXITCODE-ne0){throw 'Local profile registration test build failed'}
$assembly=Join-Path $artifacts 'bin/LocalProfileRegistrationRegression/debug/Nanaimo.Adapter.dll'
& $DotnetPath $assembly $root $fixture
if($LASTEXITCODE-ne0){throw 'Local profile registration integration failed'}
Write-Output "Isolated test artifacts: $artifacts"
Write-Output "Isolated test database: $fixture"
