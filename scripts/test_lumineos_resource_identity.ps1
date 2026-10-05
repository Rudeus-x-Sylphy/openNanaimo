$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../gui_launcher/lumineos_resource_identity.ps1')
# Import production path guards without executing the launcher's main body.
# A permissive mock previously hid the file-versus-directory regression.
$tokens=$null;$parseErrors=$null
$helper=Join-Path $PSScriptRoot '../gui_launcher/start_social_client.ps1'
$ast=[Management.Automation.Language.Parser]::ParseFile($helper,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw ($parseErrors|Out-String)}
foreach($functionName in @('Get-FullPath','Assert-ChildPath','Test-ReparsePoint','Assert-NoReparseAncestors')){
    $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $functionName},$true)
    if($null-eq$node){throw "Missing production guard: $functionName"}
    . ([scriptblock]::Create($node.Extent.Text))
}
function Get-Sha256([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Get-OptionalFileHash([string]$Path){if(Test-Path -LiteralPath $Path -PathType Leaf){Get-Sha256 $Path}else{''}}
function Get-JsonFile([string]$Path){Get-Content -LiteralPath $Path -Raw -Encoding UTF8|ConvertFrom-Json}
function Write-AtomicSocialMetadata([string]$Path,[object]$Metadata,[byte[]]$ExactBytes){[IO.File]::WriteAllBytes($Path,$ExactBytes)}
function Write-FixtureManifest([string]$Path,[object]$Manifest){
    $body=[ordered]@{schema=$Manifest.schema;files=$Manifest.files;preserve_l7_combat=$Manifest.preserve_l7_combat}
    $text=$body|ConvertTo-Json -Depth 5
    $id=Get-LumineosTextHash $text
    $line='  "id": "'+$id+'",'+"`n"
    $withId=[regex]::Replace($text,'\A\{\r?\n',("`$0"+$line))
    [IO.File]::WriteAllText($Path,$withId,[Text.UTF8Encoding]::new($false))
}
$checks=0
function Check([bool]$Ok,[string]$Message){if(-not$Ok){throw $Message};$script:checks++}
function Reject([scriptblock]$Action,[string]$Expected=''){$refused=$false;try{& $Action}catch{if($Expected-and$_.Exception.Message-notlike$Expected){throw};$refused=$true};Check $refused 'Expected resource version refusal'}
$root=Join-Path ([IO.Path]::GetTempPath()) ('nanaimo-lumineos-identity-'+[guid]::NewGuid().ToString('N'))
$source=Join-Path $root 'source';$cache=Join-Path $root 'cache';$work=Join-Path $cache 'work'
New-Item -ItemType Directory -Path $source,$work|Out-Null
$name='openNanaimo-l7-l8-resources.json'
$sourceMarker=Join-Path $source $name;$targetMarker=Join-Path $work $name
try{
    Sync-LumineosResourceIdentity $source $work $cache
    Check (-not(Test-Path -LiteralPath $targetMarker)) 'No overlay is a no-op'
    foreach($dir in @($source,$work)){[IO.File]::WriteAllText((Join-Path $dir 'scene.im3'),'first')}
    $row=[ordered]@{path='scene.im3';sha256=(Get-Sha256 (Join-Path $source 'scene.im3')).ToLowerInvariant()}
    $m=[ordered]@{schema='openNanaimo.l7-visual-l8-resources.v1';id=('a'*64);files=@($row);preserve_l7_combat=@()}
    Write-FixtureManifest $sourceMarker $m
    Sync-LumineosResourceIdentity $source $work $cache
    Check ((Get-Sha256 $targetMarker)-eq(Get-Sha256 $sourceMarker)) 'Exact initial identity copied'
    Sync-LumineosResourceIdentity $source $work $cache
    Check ((Get-Sha256 $targetMarker)-eq(Get-Sha256 $sourceMarker)) 'Idempotence'
    # A real directory guard must still reject file inputs and linked ancestors.
    Reject {Assert-NoReparseAncestors $sourceMarker} 'Cache root is not a directory:*'
    foreach($markerPath in @($sourceMarker,$targetMarker)){
        $saved=[IO.File]::ReadAllBytes($markerPath)
        [IO.File]::Delete($markerPath)
        New-Item -ItemType Directory -Path $markerPath|Out-Null
        try{Reject {Sync-LumineosResourceIdentity $source $work $cache} 'L7/L8 resource identity is not a file:*'}
        finally{[IO.Directory]::Delete($markerPath);[IO.File]::WriteAllBytes($markerPath,$saved)}
        [IO.File]::Delete($markerPath)
        New-Item -ItemType Junction -Path $markerPath -Target $root|Out-Null
        try{Reject {Sync-LumineosResourceIdentity $source $work $cache} 'L7/L8 resource identity cannot be a reparse point:*'}
        finally{[IO.Directory]::Delete($markerPath);[IO.File]::WriteAllBytes($markerPath,$saved)}
    }
    $sourceAlias=Join-Path $root 'source-link';$cacheAlias=Join-Path $root 'cache-link'
    New-Item -ItemType Junction -Path $sourceAlias -Target $source|Out-Null
    try{Reject {Sync-LumineosResourceIdentity $sourceAlias $work $cache} 'Cache root cannot use a reparse point:*'}
    finally{[IO.Directory]::Delete($sourceAlias)}
    New-Item -ItemType Junction -Path $cacheAlias -Target $cache|Out-Null
    try{Reject {Sync-LumineosResourceIdentity $source (Join-Path $cacheAlias 'work') $cacheAlias} 'Cache root cannot use a reparse point:*'}
    finally{[IO.Directory]::Delete($cacheAlias)}
    Sync-LumineosResourceIdentity $source $work $cache
    Check ((Get-Sha256 $targetMarker)-eq(Get-Sha256 $sourceMarker)) 'Rejected paths leave original markers intact'
    $m.preserve_l7_combat=@($row)
    Write-FixtureManifest $sourceMarker $m
    Sync-LumineosResourceIdentity $source $work $cache
    Check ((Get-Sha256 $targetMarker)-eq(Get-Sha256 $sourceMarker)) 'Shared resource may occur once in each consistent list'
    $m.preserve_l7_combat=@()
    Write-FixtureManifest $sourceMarker $m
    Sync-LumineosResourceIdentity $source $work $cache
    $old=Get-Sha256 $targetMarker
    [IO.File]::WriteAllText((Join-Path $source 'scene.im3'),'second')
    $row.sha256=(Get-Sha256 (Join-Path $source 'scene.im3')).ToLowerInvariant();$m.id='b'*64
    Write-FixtureManifest $sourceMarker $m
    Reject {Sync-LumineosResourceIdentity $source $work $cache}
    Check ((Get-Sha256 $targetMarker)-eq$old) 'Stale copied resource must not get fresh identity'
    [IO.File]::WriteAllText((Join-Path $work 'scene.im3'),'second')
    Sync-LumineosResourceIdentity $source $work $cache
    Check ((Get-Sha256 $targetMarker)-eq(Get-Sha256 $sourceMarker)) 'Changed overlay identity propagated only after content equality'
    [IO.File]::Delete($sourceMarker)
    Reject {Sync-LumineosResourceIdentity $source $work $cache}
    Write-FixtureManifest $sourceMarker $m
    $bad=[IO.File]::ReadAllText($sourceMarker).Replace([string]$row.sha256,('f'*64))
    [IO.File]::WriteAllText($sourceMarker,$bad)
    Reject {Sync-LumineosResourceIdentity $source $work $cache}
    $row.path='../escape'
    Write-FixtureManifest $sourceMarker $m
    Reject {Sync-LumineosResourceIdentity $source $work $cache}
    $row.path='scene.im3';$m.files=@($row,$row)
    Write-FixtureManifest $sourceMarker $m
    Reject {Sync-LumineosResourceIdentity $source $work $cache}
    $m.files=@($row)
    Write-FixtureManifest $sourceMarker $m
    [IO.File]::Delete((Join-Path $work 'scene.im3'))
    Reject {Sync-LumineosResourceIdentity $source $work $cache}
    Write-Host "LUMINEOS_SOCIAL_IDENTITY_PASS checks=$checks"
}finally{
    $resolved=[IO.Path]::GetFullPath($root)
    $temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if(-not$resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)-or-not[IO.Path]::GetFileName($resolved).StartsWith('nanaimo-lumineos-identity-')){throw 'Unsafe test cleanup'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
