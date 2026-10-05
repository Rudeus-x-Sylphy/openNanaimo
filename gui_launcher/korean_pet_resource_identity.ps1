# Optional Korean pet assets are a catalog + resource contract, not just pi._D7.
# Caller owns the social-slot lock and has rejected a running slot client.
function Sync-KoreanPetResourceIdentity([string]$SourceRoot,[string]$WorkRoot,[string]$CacheRoot){
    $markerName='.openNanaimo-korean-pets.json'
    $source=Join-Path $SourceRoot $markerName
    $target=Join-Path $WorkRoot $markerName
    Assert-ChildPath $CacheRoot $target
    Assert-NoReparseAncestors (Split-Path $target -Parent)
    if(Test-ReparsePoint $target){throw 'Korean pet receipt cannot be a reparse point.'}
    if(-not(Test-Path -LiteralPath $source -PathType Leaf)){
        if(Test-Path -LiteralPath $target){throw 'Korean pet source installation removed; rebuild isolated client.'}
        return
    }
    $recipePath=Join-Path $SourceRoot 'manifest\korean_pet_resources.json'
    if(-not(Test-Path -LiteralPath $recipePath -PathType Leaf)){throw 'Korean pet recipe missing.'}
    $receipt=Get-Content -Raw -Encoding UTF8 -LiteralPath $source|ConvertFrom-Json
    $recipe=Get-Content -Raw -Encoding UTF8 -LiteralPath $recipePath|ConvertFrom-Json
    if($receipt.schema-ne'openNanaimo.korean-pets-install.v1'-or$receipt.recipe_sha256-ne(Get-Sha256 $recipePath)-or$receipt.catalog_count-ne$recipe.catalog_count){throw 'Korean pet receipt/recipe mismatch.'}
    $rows=@([pscustomobject]@{path='pi._D7';sha256=$recipe.installed_catalog.sha256;size=$recipe.installed_catalog.size})
    $rows+=@($recipe.shared_resources)
    foreach($row in @($recipe.resources)){$rows+=[pscustomobject]@{path=$row.path;sha256=$row.installed_sha256;size=$row.installed_size}}
    foreach($row in $rows){
        $relative=[string]$row.path
        if([string]::IsNullOrWhiteSpace($relative)-or$relative.Contains('\')-or$relative.Contains(':')-or$relative.StartsWith('/')-or$relative-match'(^|/)(\.|\.\.)(/|$)'-or[string]$row.sha256-notmatch'^[0-9a-f]{64}$'){throw 'Unsafe Korean pet resource entry.'}
        $original=Join-Path $SourceRoot $relative;$prepared=Join-Path $WorkRoot $relative
        Assert-ChildPath $SourceRoot $original;Assert-ChildPath $WorkRoot $prepared
        foreach($path in @($original,$prepared)){
            if(-not(Test-Path -LiteralPath $path -PathType Leaf)-or(Get-Item -LiteralPath $path).Length-ne[long]$row.size-or(Get-Sha256 $path)-ne[string]$row.sha256){throw "Korean pet resource mismatch; rebuild copied slot before launch: $path"}
        }
    }
    # The hero is part of this catalog, but has a separate verifier/receipt.
    $heroSource=Join-Path $SourceRoot '.openNanaimo-hero-dragon.json'
    $heroTarget=Join-Path $WorkRoot '.openNanaimo-hero-dragon.json'
    Assert-ChildPath $CacheRoot $heroTarget
    if(Test-ReparsePoint $heroTarget){throw 'Hero pet receipt cannot be a reparse point.'}
    if(Test-Path -LiteralPath $heroSource -PathType Leaf){
        $heroRecipePath=Join-Path $SourceRoot 'manifest\hero_dragon_resources.json'
        $heroRecipe=Get-Content -Raw -Encoding UTF8 -LiteralPath $heroRecipePath|ConvertFrom-Json
        $hero=Get-Content -Raw -Encoding UTF8 -LiteralPath $heroSource|ConvertFrom-Json
        if($hero.schema-ne'openNanaimo.hero-dragon-install.v1'-or$hero.item_code-ne15003361-or$hero.model_stage-ne3-or$hero.catalog_count-ne$recipe.catalog_count-or$hero.required_level-ne$heroRecipe.required_level-or$hero.source_required_level-ne$heroRecipe.source_required_level-or$hero.pet_catalog.sha256-ne$recipe.installed_catalog.sha256-or$hero.pet_catalog.size-ne$recipe.installed_catalog.size){throw 'Hero receipt/catalog policy mismatch.'}
        if((Get-OptionalFileHash $heroTarget)-ne(Get-Sha256 $heroSource)){Write-AtomicSocialMetadata $heroTarget $null ([IO.File]::ReadAllBytes($heroSource))}
    }elseif(Test-Path -LiteralPath $heroTarget){throw 'Hero source receipt removed; rebuild isolated client.'}
    $sourceHash=Get-Sha256 $source
    if((Get-OptionalFileHash $target)-ne$sourceHash){Write-AtomicSocialMetadata $target $null ([IO.File]::ReadAllBytes($source))}
}
