param(
    [Parameter(Mandatory=$true)][string]$ClientRoot,
    [string]$RecipePath
)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($ClientRoot).TrimEnd('\')
if(-not $RecipePath){$RecipePath=Join-Path $root 'manifest\korean_wardrobe_resources.json'}
if(-not(Test-Path -LiteralPath $RecipePath -PathType Leaf)){throw 'Wardrobe resource recipe missing.'}
$recipe=Get-Content -LiteralPath $RecipePath -Raw -Encoding UTF8 | ConvertFrom-Json
if($recipe.schema-ne'openNanaimo.korean-wardrobe.v1'-or$recipe.counts.clothing-ne4790-or$recipe.counts.furniture-ne2008){throw 'Wardrobe resource version mismatch.'}
$kobePath=Join-Path (Split-Path -Parent ([IO.Path]::GetFullPath($RecipePath))) 'kobe_wardrobe.json'
if($recipe.custom_addons.kobe.schema-ne'openNanaimo.kobe-wardrobe.v1'-or$recipe.custom_addons.kobe.count-ne5-or-not(Test-Path -LiteralPath $kobePath)-or(Get-FileHash -LiteralPath $kobePath -Algorithm SHA256).Hash-ne[string]$recipe.custom_addons.kobe.recipe_sha256){throw 'Kobe wardrobe resource version mismatch.'}
foreach($entry in $recipe.files.PSObject.Properties){
    $relative=[string]$entry.Name
    if($relative.Contains(':')-or$relative.StartsWith('/')-or$relative.Contains('\')-or$relative-match'(^|/)(\.|\.\.)(/|$)'){throw 'Invalid wardrobe resource path.'}
    $path=[IO.Path]::GetFullPath((Join-Path $root $relative))
    if(-not $path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Wardrobe resource path escapes root.'}
    $expected=$entry.Value.after
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)-or(Get-Item -LiteralPath $path).Length-ne[long]$expected.size-or(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash-ne[string]$expected.sha256){
        throw "Wardrobe resource missing or changed; launch refused: $relative"
    }
}
Write-Output 'KOREAN_WARDROBE_RESOURCE_IDENTITY_PASS'
