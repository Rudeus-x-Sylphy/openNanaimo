function Get-LumineosTextHash([string]$Text){
    $hash=[Security.Cryptography.SHA256]::Create()
    try{return [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))).Replace('-','').ToLowerInvariant()}
    finally{$hash.Dispose()}
}
# Called under the existing social slot lock, before launching any client.
# Resources are usually directory links; a copied fallback must have exactly
# the same overlay, otherwise fail closed instead of running mixed versions.
function Sync-LumineosResourceIdentity([string]$SourceRoot,[string]$WorkRoot,[string]$CacheRoot){
    $name='openNanaimo-l7-l8-resources.json'
    $source=Join-Path $SourceRoot $name
    $target=Join-Path $WorkRoot $name
    Assert-ChildPath $CacheRoot $target
    # The shared ancestor guard accepts directories, not marker files.
    # Validate both directory chains, then reject linked/non-file markers.
    foreach($markerPath in @($source,$target)){
        Assert-NoReparseAncestors (Split-Path -Parent $markerPath)
        if(Test-ReparsePoint $markerPath){throw "L7/L8 resource identity cannot be a reparse point: $markerPath"}
        if((Test-Path -LiteralPath $markerPath)-and-not(Test-Path -LiteralPath $markerPath -PathType Leaf)){
            throw "L7/L8 resource identity is not a file: $markerPath"
        }
    }
    $present=Test-Path -LiteralPath $source -PathType Leaf
    if(-not $present){
        if(Test-Path -LiteralPath $target){throw 'L7/L8 overlay was removed from source; rebuild the isolated client before launch.'}
        return
    }
    $sourceHash=Get-Sha256 $source
    $manifest=Get-JsonFile $source
    if([string]$manifest.schema-ne'openNanaimo.l7-visual-l8-resources.v1'-or[string]$manifest.id-notmatch'^[0-9a-f]{64}$'){
        throw 'Unsupported L7/L8 resource identity.'
    }
    $text=[IO.File]::ReadAllText($source,[Text.UTF8Encoding]::new($false,$true))
    $idLine=[regex]::new('(?m)^[ \t]+"id": "([0-9a-f]{64})",\r?\n')
    $matches=$idLine.Matches($text)
    if($matches.Count-ne1-or$matches[0].Groups[1].Value-ne[string]$manifest.id-or(Get-LumineosTextHash ($idLine.Replace($text,'',1)))-ne[string]$manifest.id){
        throw 'L7/L8 resource manifest digest mismatch.'
    }
    if(@($manifest.files).Count-eq0){throw 'Empty L7/L8 resource manifest.'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($group in @('files','preserve_l7_combat')){
        $seen.Clear()
        foreach($row in @($manifest.$group)){
            if(-not$seen.Add([string]$row.path)){throw 'Duplicate L7/L8 resource identity entry.'}
        }
    }
    # A shared ep22 image may occur in both the overlay and preservation list.
    # Both digests are checked below; duplicate entries within either list fail.
    foreach($row in @($manifest.files)+@($manifest.preserve_l7_combat)){
        $relative=[string]$row.path
        if([string]::IsNullOrWhiteSpace($relative)-or$relative.Contains('\')-or$relative.Contains(':')-or$relative.StartsWith('/')-or$relative-match'(^|/)(\.|\.\.|)(/|$)'-or[string]$row.sha256-notmatch'^[0-9a-f]{64}$'){
            throw 'Unsafe L7/L8 manifest entry.'
        }
        $original=Join-Path $SourceRoot $relative
        $prepared=Join-Path $WorkRoot $relative
        Assert-ChildPath $SourceRoot $original
        Assert-ChildPath $WorkRoot $prepared
        foreach($path in @($original,$prepared)){
            if(-not(Test-Path -LiteralPath $path -PathType Leaf)-or(Get-Sha256 $path)-ne[string]$row.sha256){
                throw "L7/L8 resource version mismatch; rebuild copied client before launch: $path"
            }
        }
    }
    if((Get-Sha256 $source)-ne$sourceHash){throw 'L7/L8 resource identity changed during preparation.'}
    if((Get-OptionalFileHash $target)-ne$sourceHash){
        Write-AtomicSocialMetadata $target $null ([IO.File]::ReadAllBytes($source))
    }
}
