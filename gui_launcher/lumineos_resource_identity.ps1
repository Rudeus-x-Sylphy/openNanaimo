function Get-LumineosTextHash([string]$Text){
    $hash=[Security.Cryptography.SHA256]::Create()
    try{return [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))).Replace('-','').ToLowerInvariant()}
    finally{$hash.Dispose()}
}
function Get-DungeonCinematicContract([string]$SourceRoot){
    $tool=Join-Path (Split-Path -Parent $PSScriptRoot) 'adapter_runtime/Nanaimo.Adapter.exe'
    $text=& $tool --tools verify-resources --kind cinematics --client-root $SourceRoot 2>&1
    if($LASTEXITCODE-ne0){throw ('Cinematic source verification failed: '+($text -join ' '))}
    $contract=($text -join "`n")|ConvertFrom-Json
    if([string]$contract.status-ne'CINEMATIC_RESOURCES_PASS'){throw 'Invalid cinematic verifier result.'}
    return $contract
}
function Sync-DungeonCinematicIdentity([string]$SourceRoot,[string]$WorkRoot,[string]$CacheRoot){
    $name='.openNanaimo-dungeon-cinematics.json'
    $source=Join-Path $SourceRoot $name
    $target=Join-Path $WorkRoot $name
    Assert-ChildPath $CacheRoot $target
    foreach($path in @($source,$target)){
        Assert-NoReparseAncestors (Split-Path -Parent $path)
        if(Test-ReparsePoint $path){throw 'Cinematic identity cannot be a reparse point.'}
        if((Test-Path -LiteralPath $path)-and-not(Test-Path -LiteralPath $path -PathType Leaf)){throw 'Cinematic identity must be a regular file.'}
    }
    if(-not(Test-Path -LiteralPath $source -PathType Leaf)){
        if(Test-Path -LiteralPath $target){throw 'Cinematic release changed; rebuild the isolated client.'}
        return @{}
    }
    $sourceHash=Get-Sha256 $source
    $contract=Get-DungeonCinematicContract $SourceRoot
    $files=@{}
    foreach($row in @($contract.files)){
        if($files.ContainsKey([string]$row.path)){throw 'Duplicate cinematic resource.'}
        Assert-LumineosResourcePair $SourceRoot $WorkRoot $row
        $files[[string]$row.path]=$row
    }
    if((Get-Sha256 $source)-ne$sourceHash){throw 'Cinematic identity changed during preparation.'}
    if((Get-OptionalFileHash $target)-ne$sourceHash){Write-AtomicSocialMetadata $target $null ([IO.File]::ReadAllBytes($source))}
    return $files
}
function Assert-LumineosInstalledOverlay([string]$SourceRoot){
    # Use normal launch's embedded recipes, not a receipt-only waiver.
    $tool=Join-Path (Split-Path -Parent $PSScriptRoot) 'adapter_runtime/Nanaimo.Adapter.exe'
    if(-not(Test-Path -LiteralPath $tool -PathType Leaf)){throw 'L7/L8 overlay verifier is missing.'}
    try{$result=& $tool --tools verify-resources --kind lumineos --client-root $SourceRoot 2>&1}
    catch{throw ('L7/L8 source overlay verification failed: '+$_.Exception.Message)}
    if($LASTEXITCODE-ne0){throw ('L7/L8 source overlay verification failed: '+($result -join ' '))}
}
function Assert-LumineosResourcePair([string]$SourceRoot,[string]$WorkRoot,[object]$Row){
    $relative=[string]$Row.path
    if([string]::IsNullOrWhiteSpace($relative)-or$relative.Contains('\')-or$relative.Contains(':')-or$relative.StartsWith('/')-or$relative-match'(^|/)(\.|\.\.|)(/|$)'-or[string]$Row.sha256-notmatch'^[0-9a-f]{64}$'){
        throw 'Unsafe L7/L8 manifest entry.'
    }
    foreach($root in @($SourceRoot,$WorkRoot)){
        $path=Join-Path $root $relative
        Assert-ChildPath $root $path
        if(-not(Test-Path -LiteralPath $path -PathType Leaf)-or(Get-Sha256 $path)-ne[string]$Row.sha256){
            throw ('L7/L8 resource version mismatch; rebuild copied client before launch: '+$path)
        }
    }
}
# Called under the existing social slot lock, before launching any client.
# Resources are usually directory links; a copied fallback must have exactly
# the same overlay, otherwise fail closed instead of running mixed versions.
function Sync-LumineosResourceIdentity([string]$SourceRoot,[string]$WorkRoot,[string]$CacheRoot){
    $cinematics=Sync-DungeonCinematicIdentity $SourceRoot $WorkRoot $CacheRoot
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
    $overlay=$null
    $overlayMarkers=@()
    foreach($row in @($manifest.files)+@($manifest.preserve_l7_combat)){
        $relative=[string]$row.path
        if([string]::IsNullOrWhiteSpace($relative)-or$relative.Contains('\')-or$relative.Contains(':')-or$relative.StartsWith('/')-or$relative-match'(^|/)(\.|\.\.|)(/|$)'-or[string]$row.sha256-notmatch'^[0-9a-f]{64}$'){
            throw 'Unsafe L7/L8 manifest entry.'
        }
        $original=Join-Path $SourceRoot $relative
        $prepared=Join-Path $WorkRoot $relative
        Assert-ChildPath $SourceRoot $original
        Assert-ChildPath $WorkRoot $prepared
        $expected=$row
        if($cinematics.ContainsKey($relative)){
            $candidate=$cinematics[$relative]
            $parentMatches=@($candidate.accepted_before|Where-Object{[string]$_.sha256-eq[string]$row.sha256-and($row.PSObject.Properties.Name-notcontains'size'-or[long]$_.size-eq[long]$row.size)}).Count-gt0
            if($parentMatches){$expected=$candidate}
        }
        if((Get-OptionalFileHash $original)-ne[string]$row.sha256-and(Test-Path -LiteralPath (Join-Path $SourceRoot '.openNanaimo-monk-dialogue.json') -PathType Leaf)){
            if($null-eq$overlay){
                $overlay=@{}
                foreach($markerName in @('.openNanaimo-monk-dialogue.json','.openNanaimo-monk-challenges.json')){
                    $markerSource=Join-Path $SourceRoot $markerName
                    $markerTarget=Join-Path $WorkRoot $markerName
                    Assert-ChildPath $CacheRoot $markerTarget
                    foreach($markerPath in @($markerSource,$markerTarget)){
                        Assert-NoReparseAncestors (Split-Path -Parent $markerPath)
                        if(Test-ReparsePoint $markerPath){throw ('L7/L8 overlay receipt cannot be a reparse point: '+$markerPath)}
                        if((Test-Path -LiteralPath $markerPath)-and-not(Test-Path -LiteralPath $markerPath -PathType Leaf)){throw ('L7/L8 overlay receipt is not a file: '+$markerPath)}
                    }
                    $bytes=[IO.File]::ReadAllBytes($markerSource)
                    $receipt=[Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json
                    $overlayMarkers+=@{Source=$markerSource;Target=$markerTarget;Bytes=$bytes;Hash=(Get-Sha256 $markerSource)}
                    foreach($resource in @($receipt.files)){
                        if($overlay.ContainsKey([string]$resource.path)-and$overlay[[string]$resource.path].sha256-ne$resource.sha256){throw 'Conflicting L7/L8 overlay resources.'}
                        $overlay[[string]$resource.path]=$resource
                    }
                }
                Assert-LumineosInstalledOverlay $SourceRoot
                # Includes root-level copied catalogs, not only linked village images.
                foreach($resource in $overlay.Values){Assert-LumineosResourcePair $SourceRoot $WorkRoot $resource}
            }
            if($overlay.ContainsKey($relative)){$expected=$overlay[$relative]}
        }
        Assert-LumineosResourcePair $SourceRoot $WorkRoot $expected
    }
    if((Get-Sha256 $source)-ne$sourceHash){throw 'L7/L8 resource identity changed during preparation.'}
    foreach($marker in $overlayMarkers){
        if((Get-Sha256 $marker.Source)-ne$marker.Hash){throw 'L7/L8 overlay receipt changed during preparation.'}
    }
    foreach($marker in $overlayMarkers){
        if((Get-OptionalFileHash $marker.Target)-ne$marker.Hash){Write-AtomicSocialMetadata $marker.Target $null $marker.Bytes}
    }
    if((Get-OptionalFileHash $target)-ne$sourceHash){
        Write-AtomicSocialMetadata $target $null ([IO.File]::ReadAllBytes($source))
    }
}
