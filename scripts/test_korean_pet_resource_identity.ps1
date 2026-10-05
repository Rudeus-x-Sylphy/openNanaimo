$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../gui_launcher/korean_pet_resource_identity.ps1')
function Assert-ChildPath([string]$Root,[string]$Path){$prefix=[IO.Path]::GetFullPath($Root).TrimEnd('\')+'\';if(-not[IO.Path]::GetFullPath($Path).StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'outside root'}}
function Assert-NoReparseAncestors([string]$Path){$p=[IO.Path]::GetFullPath($Path);while($p){if(Test-ReparsePoint $p){throw 'reparse'};$p=Split-Path -Parent $p}}
function Test-ReparsePoint([string]$Path){$item=Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue;return $null-ne$item-and($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0}
function Get-Sha256([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Get-OptionalFileHash([string]$Path){if(Test-Path -LiteralPath $Path -PathType Leaf){Get-Sha256 $Path}else{''}}
function Write-AtomicSocialMetadata([string]$Path,[object]$Metadata,[byte[]]$ExactBytes){[IO.File]::WriteAllBytes($Path,$ExactBytes)}
function Write-Json([string]$Path,[object]$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))}
$checks=0
function Check([bool]$ok){if(-not$ok){throw 'check failed'};$script:checks++}
function Reject([scriptblock]$action){$refused=$false;try{&$action}catch{$refused=$true};Check $refused}
$root=Join-Path ([IO.Path]::GetTempPath()) ('nanaimo-korean-identity-'+[guid]::NewGuid().ToString('N'))
$source=Join-Path $root 'source';$cache=Join-Path $root 'cache';$work=Join-Path $cache 'work';$manifest=Join-Path $source 'manifest'
New-Item -ItemType Directory -Path $source,$work,$manifest|Out-Null
$marker='.openNanaimo-korean-pets.json';$recipePath=Join-Path $manifest 'korean_pet_resources.json'
try{
    Sync-KoreanPetResourceIdentity $source $work $cache
    Check (-not(Test-Path (Join-Path $work $marker)))
    foreach($dir in @($source,$work)){[IO.File]::WriteAllText((Join-Path $dir 'pi._D7'),'catalog');[IO.File]::WriteAllText((Join-Path $dir 'pet.im3'),'pet')}
    $row=[ordered]@{path='pet.im3';installed_size=3;installed_sha256=(Get-Sha256 (Join-Path $source 'pet.im3'))}
    $recipe=[ordered]@{catalog_count=990;installed_catalog=@{size=7;sha256=(Get-Sha256 (Join-Path $source 'pi._D7'))};resources=@($row);shared_resources=@()}
    Write-Json $recipePath $recipe
    $receipt=@{schema='openNanaimo.korean-pets-install.v1';catalog_count=990;recipe_sha256=(Get-Sha256 $recipePath)}
    Write-Json (Join-Path $source $marker) $receipt
    Sync-KoreanPetResourceIdentity $source $work $cache
    Check ((Get-Sha256 (Join-Path $work $marker))-eq(Get-Sha256 (Join-Path $source $marker)))
    Sync-KoreanPetResourceIdentity $source $work $cache
    Check ((Get-Sha256 (Join-Path $work $marker))-eq(Get-Sha256 (Join-Path $source $marker)))
    $heroMarker='.openNanaimo-hero-dragon.json'
    Write-Json (Join-Path $source 'manifest/hero_dragon_resources.json') @{required_level=99;source_required_level=120}
    $hero=@{schema='openNanaimo.hero-dragon-install.v1';item_code=15003361;model_stage=3;catalog_count=990;required_level=99;source_required_level=120;pet_catalog=$recipe.installed_catalog}
    Write-Json (Join-Path $source $heroMarker) $hero
    [IO.File]::WriteAllText((Join-Path $work $heroMarker),'old-receipt')
    Sync-KoreanPetResourceIdentity $source $work $cache
    Check ((Get-Sha256 (Join-Path $work $heroMarker))-eq(Get-Sha256 (Join-Path $source $heroMarker)))
    $hero.required_level=120;Write-Json (Join-Path $source $heroMarker) $hero
    Reject {Sync-KoreanPetResourceIdentity $source $work $cache}
    $hero.required_level=99;Write-Json (Join-Path $source $heroMarker) $hero
    [IO.File]::WriteAllText((Join-Path $work 'pet.im3'),'bad')
    Reject {Sync-KoreanPetResourceIdentity $source $work $cache}
    [IO.File]::WriteAllText((Join-Path $work 'pet.im3'),'pet')
    [IO.File]::WriteAllText((Join-Path $work 'pi._D7'),'old')
    Reject {Sync-KoreanPetResourceIdentity $source $work $cache}
    [IO.File]::WriteAllText((Join-Path $work 'pi._D7'),'catalog')
    $row.path='../escape';Write-Json $recipePath $recipe;$receipt.recipe_sha256=Get-Sha256 $recipePath;Write-Json (Join-Path $source $marker) $receipt
    Reject {Sync-KoreanPetResourceIdentity $source $work $cache}
    [IO.File]::Delete((Join-Path $source $marker))
    Reject {Sync-KoreanPetResourceIdentity $source $work $cache}
    Write-Output "KOREAN_PETS_SOCIAL_IDENTITY_PASS checks=$checks"
}finally{
    $resolved=[IO.Path]::GetFullPath($root);$temp=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if(-not$resolved.StartsWith($temp,[StringComparison]::OrdinalIgnoreCase)-or-not[IO.Path]::GetFileName($resolved).StartsWith('nanaimo-korean-identity-')){throw 'Unsafe cleanup'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
