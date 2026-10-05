param([string]$Repository,[string]$DataRoot,[string]$Identity,[string]$OutputPath)
$ErrorActionPreference='Stop'
. (Join-Path $Repository 'gui_launcher/inventory_admin_gui.ps1')
$backend=Join-Path $Repository 'adapter_runtime/Nanaimo.Adapter.exe'
$ctx=[pscustomobject]@{Profiles=@(Get-InventoryAdminProfiles $DataRoot $backend '')}
$selected=Find-InventoryAdminProfile $ctx $Identity
if(-not$selected){throw ('No selected test profile: '+$Identity+' profiles='+($ctx.Profiles|ConvertTo-Json -Depth 3 -Compress))}
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $Repository 'gui_launcher/nanaimo_launcher.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw $errors[0]}
$node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name-eq'Register-ClientProfile'},$true)
. ([scriptblock]::Create($node.Extent.Text))
function Sync-LauncherProfileIdentity{return $selected}
function Read-IniProfile{throw 'Startup INI must not be read'}
function Save-Profile{throw 'Selecting an account must not save shared state'}
function Send-LocalLaunchRegistration($ip,$bytes,$description){[IO.File]::WriteAllBytes($OutputPath,$bytes)}
[void](Register-ClientProfile '127.0.0.1')
