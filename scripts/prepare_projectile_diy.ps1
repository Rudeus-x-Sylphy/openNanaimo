param([Parameter(Mandatory=$true)][string]$ClientRoot,[switch]$Apply,[switch]$Uninstall)
$ErrorActionPreference='Stop'
$recipeRoot=Split-Path $PSScriptRoot -Parent
$client=[IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
if(-not(Test-Path -LiteralPath $client -PathType Container)){throw 'Client root is not a directory'}
if(-not('Nanaimo.Projectile.Bytes'-as[type])){Add-Type -Path (Join-Path $recipeRoot 'gui_launcher\projectile_resources.cs')}
function Safe-ProjectilePath([string]$relative){
    if($relative-match'(^[\\/]|:|(^|[\\/])\.\.?([\\/]|$))'){throw "Unsafe resource path: $relative"}
    $full=[IO.Path]::GetFullPath((Join-Path $client $relative))
    if(-not$full.StartsWith($client+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Resource escapes client root'}
    $probe=$full;while($probe){if(Test-Path -LiteralPath $probe){if((Get-Item -LiteralPath $probe -Force).Attributes-band[IO.FileAttributes]::ReparsePoint){throw "Resource reparse point: $probe"}};$probe=Split-Path $probe -Parent}
    return $full
}
function Read-ProjectileBytes([string]$path){if(Test-Path -LiteralPath $path -PathType Leaf){return ,[IO.File]::ReadAllBytes($path)};return $null}
$exePath=Safe-ProjectilePath 'game.exe'
if($Apply-and@(Get-Process -Name game -ErrorAction SilentlyContinue|Where-Object{$_.Path-eq$exePath}).Count){throw 'Exit this game client before installing DIY; the adapter may remain running.'}
$recipe=Get-Content -LiteralPath (Join-Path $recipeRoot 'manifest\projectile_diy_patch.json') -Raw -Encoding UTF8|ConvertFrom-Json
$exe=[IO.File]::ReadAllBytes($exePath);$originalExe=$exe.Clone();[Nanaimo.Projectile.Bytes]::Validate($exe)
foreach($site in $recipe.sites){[Nanaimo.Projectile.Bytes]::Patch($exe,[int]$site.va,[Nanaimo.Projectile.Bytes]::Hex([string]$site.known[0]),[Nanaimo.Projectile.Bytes]::Hex([string]$site.target),[bool]$Uninstall)}
$files=[ordered]@{};$before=@{};$inputs=@{}
function Add-ProjectileResource([string]$relative,[byte[]]$bytes,[string]$previousSha256=''){
    $path=Safe-ProjectilePath $relative;$old=Read-ProjectileBytes $path
    if($null-ne$old-and-not[Nanaimo.Projectile.Bytes]::Equal($old,$bytes)-and(-not$previousSha256-or[Nanaimo.Projectile.Bytes]::Hash($old)-ne$previousSha256)){throw "Refusing to overwrite unmanaged DIY resource: $relative"}
    $files[$relative]=$bytes;$before[$relative]=$old
}
if(-not$Uninstall){
    $ponName='flying/pon/mis_ep01_hd_end_02_cannon_ball.pon';$effName='effs/game/shootinggamebasic/mis_ep01_hd_end_02_cannon ball.eff'
    $inputs[$ponName]=[IO.File]::ReadAllBytes((Safe-ProjectilePath $ponName));$inputs[$effName]=[IO.File]::ReadAllBytes((Safe-ProjectilePath $effName))
    Add-ProjectileResource 'flying/pon/nanaimo_basketball.pon' ([Nanaimo.Projectile.Bytes]::BasketballPon($inputs[$ponName]))
    Add-ProjectileResource 'effs/game/shootinggamebasic/nanaimo_basketball.eff' ([Nanaimo.Projectile.Bytes]::BasketballEff($inputs[$effName])) ([Nanaimo.Projectile.Bytes]::Hash([Nanaimo.Projectile.Bytes]::BasketballEffImageOnly($inputs[$effName])))
    $ball=[IO.File]::ReadAllBytes((Join-Path $recipeRoot 'scripts\assets\projectile\basketball.im3'))
    if([Nanaimo.Projectile.Bytes]::Hash($ball)-ne$recipe.basketball_sha256){throw 'Authored basketball IM3 identity mismatch'}
    Add-ProjectileResource 'effs/game/shootinggamebasic/nanaimo_basketball.im3' $ball ([string]$recipe.basketball_legacy_sha256)
    $aliases=Get-Content -LiteralPath (Join-Path $recipeRoot 'gui_launcher\data\projectile_aliases.json') -Raw -Encoding UTF8|ConvertFrom-Json
    foreach($p in $aliases.PSObject.Properties){$source='flying/pon/'+$p.Name;$bytes=[IO.File]::ReadAllBytes((Safe-ProjectilePath $source));$inputs[$source]=$bytes;Add-ProjectileResource ('flying/pon/'+$p.Value) $bytes}
}
# Restore only the reviewed sites on uninstall; never restore a whole old executable.
# Additive resources stay harmless on disk. Remove enablement with the hooks.
if($Uninstall){
    $config='nanaimo_projectile.ini';$old=Read-ProjectileBytes (Safe-ProjectilePath $config)
    if($null-ne$old){$text=[Text.Encoding]::ASCII.GetString($old);$files[$config]=[Text.Encoding]::ASCII.GetBytes(($text-replace'(?m)^enabled=1\r?$','enabled=0'));$before[$config]=$old}
}
# Executable is committed last, after every resource is in place.
$files['game.exe']=$exe;$before['game.exe']=$originalExe
$changed=@($files.Keys|Where-Object{-not[Nanaimo.Projectile.Bytes]::Equal($before[$_],$files[$_])})
$backups=@();$applied=New-Object 'Collections.Generic.List[string]'
if($Apply){
    foreach($name in $inputs.Keys){if(-not[Nanaimo.Projectile.Bytes]::Equal([IO.File]::ReadAllBytes((Safe-ProjectilePath $name)),$inputs[$name])){throw "Source changed: $name"}}
    foreach($name in $files.Keys){if(-not[Nanaimo.Projectile.Bytes]::Equal((Read-ProjectileBytes (Safe-ProjectilePath $name)),$before[$name])){throw "Concurrent change: $name"}}
    foreach($name in $changed){if($null-ne$before[$name]){$hash=[Nanaimo.Projectile.Bytes]::Hash($before[$name]);$backup=Safe-ProjectilePath ('.openNanaimo-projectile-backups/'+$hash+'/'+$name);$old=Read-ProjectileBytes $backup;if($null-eq$old){[Nanaimo.Projectile.Bytes]::AtomicWrite($backup,$before[$name])}elseif(-not[Nanaimo.Projectile.Bytes]::Equal($old,$before[$name])){throw 'Backup collision'};$backups+=$backup}}
    try{
        foreach($name in $changed){$path=Safe-ProjectilePath $name;if(-not[Nanaimo.Projectile.Bytes]::Equal((Read-ProjectileBytes $path),$before[$name])){throw "Concurrent edit: $name"};[Nanaimo.Projectile.Bytes]::AtomicWrite($path,$files[$name]);$applied.Add($name)}
        foreach($name in $files.Keys){if(-not[Nanaimo.Projectile.Bytes]::Equal((Read-ProjectileBytes (Safe-ProjectilePath $name)),$files[$name])){throw "Post-apply mismatch: $name"}}
        foreach($name in $inputs.Keys){if(-not[Nanaimo.Projectile.Bytes]::Equal((Read-ProjectileBytes (Safe-ProjectilePath $name)),$inputs[$name])){throw "Source changed during apply: $name"}}
    }catch{
        for($i=$applied.Count-1;$i-ge0;$i--){$name=$applied[$i];$path=Safe-ProjectilePath $name;if(-not[Nanaimo.Projectile.Bytes]::Equal((Read-ProjectileBytes $path),$files[$name])){throw "Rollback refused concurrent edit: $name"};if($null-eq$before[$name]){Remove-Item -LiteralPath $path -Force}else{[Nanaimo.Projectile.Bytes]::AtomicWrite($path,$before[$name])}}
        throw
    }
}
$report=[ordered]@{schema=1;action=if($Uninstall){'uninstall'}else{'install'};applied=[bool]$Apply;changed=$changed;client_sha256=[Nanaimo.Projectile.Bytes]::Hash($exe);backups=$backups;runtime_acceptance=$false}
if($Apply){$reportPath=Safe-ProjectilePath 'adapter_data/projectile_install_report.json';[Nanaimo.Projectile.Bytes]::AtomicWrite($reportPath,[Text.Encoding]::UTF8.GetBytes(($report|ConvertTo-Json -Depth 6)))}
$report|ConvertTo-Json -Depth 6
