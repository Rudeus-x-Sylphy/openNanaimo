[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ClientPath,
    [Parameter(Mandatory=$true)][ValidateRange(1,999)][int]$SocialSlot,
    [string[]]$ClientArguments=@(),
    [string]$WorkingDirectory,
    [string]$CacheRoot,
    [string]$LaunchModeConfigText,
    [ValidateRange(500,30000)][int]$StartupTimeoutMilliseconds=8000,
    [switch]$PrepareOnly,
    [switch]$CleanupPreparedCopy,
    [scriptblock]$BeforeLaunch
)

$ErrorActionPreference='Stop'
Set-StrictMode -Version 2.0

# Proven against the active 2026-09-29 client. These signatures fail closed if
# a future compatibility overlay changes the single-instance boundary.
$MutexFileOffset=0x82AABC
$MutexCallsiteFileOffset=0x399A2
$MutexWrapperFileOffset=0x6D2350
$OriginalMutexName='NanaimoClient'
$CallsiteSignature=[byte[]](0x68,0xBC,0xC0,0xC2,0x00,0xE8,0x06,0x8B,0xFC,0xFF,0x83,0xC4,0x0C,0x85,0xC0,0x0F,0x84,0xE3,0x00,0x00,0x00)
$WrapperSignature=[byte[]](0x55,0x8B,0xEC,0x8B,0x45,0x08,0x50,0x8B,0x4D,0x0C,0x51,0x6A,0x00,0xFF,0x15,0xAC,0x1D,0xD9,0x00,0x8B,0x55,0x10,0x89,0x02,0xFF,0x15,0xF0,0x1F,0xD9,0x00,0x3D,0xB7,0x00,0x00,0x00)
# The image resolver's five read-only existence probes must coexist with the
# resource loader's FILE_SHARE_READ handles. Validate the entire function.
$ResourceResolverFileOffset=0x6BBC80
$ResourceResolverLength=0x368
$ResourceResolverOriginalSha256='F8318BFCF5FB82F07032EB9E679170E17BD3BA2C29FF9CDA62E31194D03A6B72'
$ResourceResolverPatchedSha256='476D3BD61BDC048B5EFAD4F8109518430A73FAEE8268170C7F763AC413371928'
$ResourceShareFileOffsets=[long[]](0x6BBC9F,0x6BBDD6,0x6BBE55,0x6BBF06,0x6BBF85)


function Get-FullPath([string]$Path){return [IO.Path]::GetFullPath($Path)}
function Get-Sha256([string]$Path){return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()}
function Read-BytesAt([string]$Path,[long]$Offset,[int]$Count){
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    try{
        if($Offset-lt0-or($Offset+$Count)-gt$stream.Length){throw "Read outside client image: offset=$Offset count=$Count length=$($stream.Length)"}
        [void]$stream.Seek($Offset,[IO.SeekOrigin]::Begin)
        $buffer=New-Object byte[] $Count
        $read=$stream.Read($buffer,0,$Count)
        if($read-ne$Count){throw "Short client read: expected=$Count actual=$read"}
        return $buffer
    }finally{$stream.Dispose()}
}
function Assert-Bytes([string]$Path,[long]$Offset,[byte[]]$Expected,[string]$Label){
    $actual=Read-BytesAt $Path $Offset $Expected.Length
    for($i=0;$i-lt$Expected.Length;$i++){
        if($actual[$i]-ne$Expected[$i]){throw "Unsupported Game.exe: $Label signature mismatch at file offset 0x$('{0:X}'-f($Offset+$i))."}
    }
}
function Assert-SupportedClient([string]$Path){
    $header=Read-BytesAt $Path 0 2
    if($header[0]-ne0x4D-or$header[1]-ne0x5A){throw 'Unsupported Game.exe: DOS MZ header is missing.'}
    $nameBytes=[Text.Encoding]::ASCII.GetBytes($OriginalMutexName)
    Assert-Bytes $Path $MutexFileOffset ($nameBytes+[byte]0) 'NanaimoClient mutex name and terminator'
    Assert-Bytes $Path $MutexCallsiteFileOffset $CallsiteSignature 'single-instance callsite'
    Assert-Bytes $Path $MutexWrapperFileOffset $WrapperSignature 'CreateMutex/GetLastError wrapper'
    Assert-ResourceResolver $Path $false
}
function Assert-ChildPath([string]$Parent,[string]$Child){
    $parentFull=(Get-FullPath $Parent).TrimEnd('\')
    $childFull=Get-FullPath $Child
    if($childFull.Equals($parentFull,[StringComparison]::OrdinalIgnoreCase)-or-not$childFull.StartsWith($parentFull+'\',[StringComparison]::OrdinalIgnoreCase)){throw "Refusing path outside cache root: $childFull"}
}
function Test-ReparsePoint([string]$Path){
    if(-not(Test-Path -LiteralPath $Path)){return $false}
    $item=Get-Item -LiteralPath $Path -Force
    return (($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0)
}
function Assert-NoReparseAncestors([string]$Path){
    $current=Get-FullPath $Path
    while($true){
        if(Test-Path -LiteralPath $current){
            $item=Get-Item -LiteralPath $current -Force
            if(($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){throw "Cache root cannot use a reparse point: $current"}
            if(-not$item.PSIsContainer){throw "Cache root is not a directory: $current"}
        }
        $parent=Split-Path -Parent $current
        if([string]::IsNullOrWhiteSpace($parent)-or$parent.Equals($current,[StringComparison]::OrdinalIgnoreCase)){break}
        $current=$parent
    }
}
function Assert-CacheRoot([string]$Path){
    Assert-NoReparseAncestors $Path
    if(Test-Path -LiteralPath $Path -PathType Leaf){throw "Cache root is a file: $Path"}
    if(-not(Test-Path -LiteralPath $Path -PathType Container)){New-Item -ItemType Directory -Path $Path -Force|Out-Null}
    Assert-NoReparseAncestors $Path
}
function Get-BytesSha256([byte[]]$Bytes){
    $sha=[Security.Cryptography.SHA256]::Create()
    try{return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-','').ToUpperInvariant()}finally{$sha.Dispose()}
}
function Assert-ResourceResolver([string]$Path,[bool]$Patched){
    [byte[]]$code=Read-BytesAt $Path $ResourceResolverFileOffset $ResourceResolverLength
    $expected=$(if($Patched){$ResourceResolverPatchedSha256}else{$ResourceResolverOriginalSha256})
    if((Get-BytesSha256 $code)-ne$expected){throw 'Unsupported Game.exe: image resource resolver signature mismatch.'}
}
function Set-ResourceReadSharing([string]$Path){
    Assert-ResourceResolver $Path $false
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try{
        foreach($offset in $ResourceShareFileOffsets){[void]$stream.Seek($offset,[IO.SeekOrigin]::Begin);$stream.WriteByte(1)}
        $stream.Flush($true)
    }finally{$stream.Dispose()}
    Assert-ResourceResolver $Path $true
}
function Get-ResourceIsolationMetadata(){
    return [pscustomobject][ordered]@{schema='openNanaimo.social-resource-isolation.v1';function_file_offset=('0x{0:X}'-f$ResourceResolverFileOffset);function_length=$ResourceResolverLength;original_function_sha256=$ResourceResolverOriginalSha256;patched_function_sha256=$ResourceResolverPatchedSha256;patches=@(foreach($offset in $ResourceShareFileOffsets){[pscustomobject][ordered]@{file_offset=('0x{0:X}'-f$offset);virtual_address=('0x{0:X}'-f($offset-$ResourceResolverFileOffset+0xABC880));before='00';after='01'}})}
}
function Assert-ResourceIsolationMetadata([object]$Metadata){
    if($null-eq$Metadata.PSObject.Properties['resource_isolation']){throw 'Prepared social client resource isolation metadata is missing.'}
    $expected=Get-ResourceIsolationMetadata
    if(($Metadata.resource_isolation|ConvertTo-Json -Depth 5 -Compress)-ne($expected|ConvertTo-Json -Depth 5 -Compress)){throw 'Prepared social client resource isolation metadata mismatch.'}
}
function Get-ExpectedSocialClientHash([string]$Source,[string]$SourceHash,[byte[]]$Replacement,[bool]$ResourceSharing){
    $image=[IO.File]::ReadAllBytes($Source)
    if((Get-BytesSha256 $image)-ne$SourceHash){throw 'Social client source changed during preparation.'}
    [Array]::Copy($Replacement,0,$image,$MutexFileOffset,$Replacement.Length)
    if($ResourceSharing){foreach($offset in $ResourceShareFileOffsets){$image[$offset]=1}}
    return Get-BytesSha256 $image
}
function Write-AtomicSocialMetadata([string]$Path,[object]$Metadata,[byte[]]$ExactBytes=$null){
    if(Test-ReparsePoint $Path){throw "Social metadata cannot be a reparse point: $Path"}
    Assert-NoReparseAncestors (Split-Path -Parent $Path)
    $temp=$Path+'.tmp.'+[guid]::NewGuid().ToString('N')
    try{
        if($null-ne$ExactBytes){[IO.File]::WriteAllBytes($temp,$ExactBytes)}else{[IO.File]::WriteAllText($temp,($Metadata|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))}
        if(Test-Path -LiteralPath $Path){[IO.File]::Replace($temp,$Path,[NullString]::Value)}else{[IO.File]::Move($temp,$Path)}
    }finally{if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Force}}
}
function Update-PreparedResourceIsolation([string]$Target,[string]$MetadataPath,[object]$State,[string]$Source,[string]$SourceHash,[byte[]]$Replacement,[string]$CacheRoot){
    if([string]$State.Metadata.schema-eq'openNanaimo.social-client.v5'){return $null}
    Assert-NoRunningSocialProcess $Target
    $slotRoot=Split-Path -Parent $MetadataPath
    Assert-ChildPath $CacheRoot $slotRoot
    Assert-NoReparseAncestors $slotRoot
    $backupRoot=Join-Path $slotRoot ('resource-isolation-backup-'+[guid]::NewGuid().ToString('N'))
    Assert-ChildPath $CacheRoot $backupRoot
    $temp=$Target+'.tmp.'+[guid]::NewGuid().ToString('N')
    $replaced=$false;$complete=$false
    $metadataHash=Get-Sha256 $MetadataPath
    $oldTargetHash=[string]$State.Metadata.target_sha256
    try{
        Copy-Item -LiteralPath $Target -Destination $temp
        Set-ResourceReadSharing $temp
        $newHash=Get-Sha256 $temp
        if($newHash-ne(Get-ExpectedSocialClientHash $Source $SourceHash $Replacement $true)){throw 'Prepared social client differs outside the registered compatibility regions.'}
        New-Item -ItemType Directory -Path $backupRoot|Out-Null
        $backupImage=Join-Path $backupRoot 'social-client.exe'
        $backupMetadata=Join-Path $backupRoot 'social-client.json'
        Copy-Item -LiteralPath $MetadataPath -Destination $backupMetadata
        Assert-NoRunningSocialProcess $Target
        if((Get-Sha256 $MetadataPath)-ne$metadataHash-or(Get-Sha256 $Target)-ne$oldTargetHash){throw 'Prepared social client changed during resource refresh.'}
        # Stage an exact backup first: Windows ReplaceFile requires its own
        # files on one volume, while a caller-selected cache may be elsewhere.
        Copy-Item -LiteralPath $Target -Destination $backupImage
        if((Get-Sha256 $backupImage)-ne$oldTargetHash){throw 'Social client changed while backing up resource refresh.'}
        Assert-NoRunningSocialProcess $Target
        if((Get-Sha256 $MetadataPath)-ne$metadataHash-or(Get-Sha256 $Target)-ne$oldTargetHash){throw 'Prepared social client changed during resource refresh.'}
        [IO.File]::Replace($temp,$Target,[NullString]::Value)
        $replaced=$true
        $newMetadata=Get-JsonFile $backupMetadata
        $newMetadata.schema='openNanaimo.social-client.v5'
        $newMetadata.target_sha256=$newHash
        $newMetadata|Add-Member -NotePropertyName resource_isolation -NotePropertyValue (Get-ResourceIsolationMetadata)
        Write-AtomicSocialMetadata $MetadataPath $newMetadata
        $complete=$true
        return [pscustomobject]@{FromSchema='openNanaimo.social-client.v4';Backup=$backupRoot}
    }finally{
        if($replaced-and-not$complete){
            Assert-NoRunningSocialProcess $Target
            Copy-Item -LiteralPath $backupImage -Destination $temp
            [IO.File]::Replace($temp,$Target,[NullString]::Value)
            Write-AtomicSocialMetadata $MetadataPath $null ([IO.File]::ReadAllBytes($backupMetadata))
        }
        if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Force}
    }
}
function Get-OptionalFileHash([string]$Path){
    if(-not(Test-Path -LiteralPath $Path -PathType Leaf)){return $null}
    return Get-Sha256 $Path
}
function Get-ConfigHash([string]$ConfigText){
    if([string]::IsNullOrWhiteSpace($ConfigText)){return $null}
    return Get-BytesSha256 ([Text.Encoding]::ASCII.GetBytes($ConfigText))
}
function Get-JsonFile([string]$Path){
    try{return Get-Content -LiteralPath $Path -Raw -Encoding UTF8|ConvertFrom-Json}catch{throw "Invalid social client metadata: $Path"}
}
function Assert-WorktreeState([string]$MarkerPath,[string]$SourceRoot,[string]$WorkRoot,[string]$CacheRoot){
    Assert-ChildPath $CacheRoot $WorkRoot
    Assert-NoReparseAncestors $WorkRoot
    if(-not(Test-Path -LiteralPath $MarkerPath -PathType Leaf)){throw "Missing social worktree metadata: $MarkerPath"}
    if(Test-ReparsePoint $MarkerPath){throw "Social worktree metadata cannot be a reparse point: $MarkerPath"}
    $marker=Get-JsonFile $MarkerPath
    if([string]$marker.schema-ne'openNanaimo.social-worktree.v2'){throw "Unsupported social worktree schema: $MarkerPath"}
    if([string]$marker.source_root-ne(Get-FullPath $SourceRoot)){throw "Social worktree source root mismatch: $MarkerPath"}
    if([string]$marker.work_root-ne(Get-FullPath $WorkRoot)){throw "Social worktree path mismatch: $MarkerPath"}
    if([string]$marker.cache_root-ne(Get-FullPath $CacheRoot)){throw "Social worktree cache root mismatch: $MarkerPath"}
    return $marker
}
function Assert-PreparedState([string]$Target,[string]$MetadataPath,[string]$Source,[string]$SourceRoot,[string]$CacheRoot,[string]$WorkRoot,[string]$RuntimeRoot,[string]$SourceHash,[byte[]]$Replacement,[bool]$VerifyContents=$true,[bool]$PreviousSource=$false){
    if(-not(Test-Path -LiteralPath $Target -PathType Leaf)-or-not(Test-Path -LiteralPath $MetadataPath -PathType Leaf)){throw 'Prepared social client state is incomplete; cleanup is required.'}
    if(Test-ReparsePoint $MetadataPath){throw "Prepared metadata cannot be a reparse point: $MetadataPath"}
    if(Test-ReparsePoint $Target){throw "Prepared client cannot be a reparse point: $Target"}
    $meta=Get-JsonFile $MetadataPath
    if([string]$meta.schema-notin@('openNanaimo.social-client.v4','openNanaimo.social-client.v5')){throw "Unsupported prepared social client schema: $MetadataPath"}
    $resourceSharing=([string]$meta.schema-eq'openNanaimo.social-client.v5')
    if($resourceSharing){Assert-ResourceIsolationMetadata $meta}
    if([string]$meta.placement-ne'source-directory-adjacent'){throw "Prepared social client placement mismatch: $MetadataPath"}
    if([string]$meta.source_path-ne(Get-FullPath $Source)){throw "Prepared social client source path mismatch: $MetadataPath"}
    if([string]$meta.source_sha256-ne$SourceHash){throw "Prepared social client source hash mismatch: $MetadataPath"}
    $expectedLength=if($PreviousSource){(Get-Item -LiteralPath $Target).Length}else{(Get-Item -LiteralPath $Source).Length}
    if([long]$meta.source_length-ne$expectedLength){throw "Prepared social client source length mismatch: $MetadataPath"}
    if([string]$meta.prepared_client-ne(Get-FullPath $Target)){throw "Prepared social client target mismatch: $MetadataPath"}
    if([string]$meta.static_root-ne(Get-FullPath $SourceRoot)){throw "Prepared social client source root mismatch: $MetadataPath"}
    if([string]$meta.cache_root-ne(Get-FullPath $CacheRoot)){throw "Prepared social client cache root mismatch: $MetadataPath"}
    if([string]$meta.work_directory-ne(Get-FullPath $WorkRoot)){throw "Prepared social client work directory mismatch: $MetadataPath"}
    if([string]$meta.runtime_directory-ne(Get-FullPath $RuntimeRoot)){throw "Prepared social client runtime directory mismatch: $MetadataPath"}
    if([string]$meta.mutex_file_offset-ne('0x{0:X}'-f$MutexFileOffset)){throw "Prepared social client mutex offset mismatch: $MetadataPath"}
    if([string]$meta.original_mutex-ne$OriginalMutexName){throw "Prepared social client original mutex mismatch: $MetadataPath"}
    if([string]$meta.mutex_name-ne[Text.Encoding]::ASCII.GetString($Replacement)){throw "Prepared social client mutex name mismatch: $MetadataPath"}
    $marker=Assert-WorktreeState (Join-Path $WorkRoot 'social-worktree.json') $SourceRoot $WorkRoot $CacheRoot
    if([string]$meta.config_sha256-ne[string]$marker.config_sha256){throw "Prepared social client configuration metadata mismatch: $MetadataPath"}
    if($VerifyContents){
        if([string]$meta.target_sha256-ne(Get-Sha256 $Target)){throw "Prepared social client target hash mismatch: $MetadataPath"}
        Assert-Bytes $Target $MutexFileOffset $Replacement 'prepared social mutex name'
        Assert-ResourceResolver $Target $resourceSharing
        if($PreviousSource){
            # The old source may no longer exist. Undo only our registered edits,
            # then require the exact source hash recorded by the old generation.
            $image=[IO.File]::ReadAllBytes($Target)
            $original=[Text.Encoding]::ASCII.GetBytes($OriginalMutexName)
            [Array]::Copy($original,0,$image,$MutexFileOffset,$original.Length)
            if($resourceSharing){foreach($offset in $ResourceShareFileOffsets){$image[$offset]=0}}
            if((Get-BytesSha256 $image)-ne$SourceHash){throw 'Previous social client differs outside the registered compatibility regions.'}
        }elseif([string]$meta.target_sha256-ne(Get-ExpectedSocialClientHash $Source $SourceHash $Replacement $resourceSharing)){throw 'Prepared social client differs outside the registered compatibility regions.'}
        foreach($directoryName in @('StateOption','config','configs','Working','runtime')){
            Assert-NoReparseAncestors (Join-Path $WorkRoot $directoryName)
        }
        $configPath=Join-Path (Join-Path $WorkRoot 'StateOption') 'gamestartoption.ini'
        if(Test-ReparsePoint $configPath){throw "Social worktree configuration cannot be a reparse point: $configPath"}
        if([string]$marker.config_sha256-ne[string](Get-OptionalFileHash $configPath)){throw "Social worktree configuration was modified: $configPath"}
    }
    return [pscustomobject]@{Metadata=$meta;Marker=$marker}
}
function Find-PreviousSourcePreparedState([string]$Target,[string]$Source,[string]$SourceRoot,[string]$CacheRoot,[string]$SourceHash,[byte[]]$Replacement,[int]$Slot){
    if(Test-ReparsePoint $Target){throw 'Prepared client cannot be a reparse point.'}
    $targetHash=Get-Sha256 $Target
    $candidates=@()
    foreach($generation in @(Get-ChildItem -LiteralPath $CacheRoot -Directory -Force)){
        if($generation.Name-notmatch'^[0-9A-Fa-f]{64}$'-or$generation.Name-eq$SourceHash){continue}
        $oldSlot=Join-Path $generation.FullName ('slot-{0:D3}'-f$Slot)
        $oldMetadata=Join-Path $oldSlot 'social-client.json'
        if(-not(Test-Path -LiteralPath $oldMetadata -PathType Leaf)){continue}
        Assert-ChildPath $CacheRoot $oldSlot
        Assert-NoReparseAncestors $oldSlot
        if(Test-ReparsePoint $oldMetadata){throw 'Prepared metadata cannot be a reparse point.'}
        $old=Get-JsonFile $oldMetadata
        if([string]$old.schema-notin@('openNanaimo.social-client.v4','openNanaimo.social-client.v5')){continue}
        if([string]$old.prepared_client-ne(Get-FullPath $Target)-or[string]$old.target_sha256-ne$targetHash){continue}
        $oldWork=Join-Path $oldSlot 'work'
        $checked=Assert-PreparedState $Target $oldMetadata $Source $SourceRoot $CacheRoot $oldWork (Join-Path $oldWork 'runtime') $generation.Name.ToUpperInvariant() $Replacement $true $true
        $candidates+=[pscustomobject]@{SlotDirectory=$oldSlot;MetadataPath=$oldMetadata;Metadata=$checked.Metadata;TargetHash=$targetHash;SourceHash=$generation.Name.ToUpperInvariant()}
    }
    if($candidates.Count-gt1){throw 'Ambiguous previous social client registrations; refusing automatic rebuild.'}
    if($candidates.Count-eq1){return $candidates[0]}
    return $null
}
function Backup-PreviousSourceClient($Previous,[string]$Target,[string]$CacheRoot){
    Assert-NoRunningSocialProcess $Target
    Assert-ChildPath $CacheRoot $Previous.SlotDirectory
    Assert-NoReparseAncestors $Previous.SlotDirectory
    if(Test-ReparsePoint $Target){throw 'Prepared client cannot be a reparse point.'}
    if((Get-Sha256 $Target)-ne$Previous.TargetHash){throw 'Previous social client changed before backup.'}
    $backup=Join-Path $Previous.SlotDirectory ('source-update-backup-'+[guid]::NewGuid().ToString('N'))
    Assert-ChildPath $CacheRoot $backup
    Assert-NoReparseAncestors $backup
    New-Item -ItemType Directory -Path $backup -ErrorAction Stop|Out-Null
    Copy-Item -LiteralPath $Previous.MetadataPath -Destination (Join-Path $backup 'social-client.json')
    $backupClient=Join-Path $backup 'social-client.exe'
    # Move one verified derived executable, never a directory or account data.
    Assert-ChildPath (Split-Path -Parent $Target) $Target
    Assert-ChildPath $CacheRoot $backupClient
    Move-Item -LiteralPath $Target -Destination $backupClient -ErrorAction Stop
    return [pscustomobject]@{Backup=$backup;BackupClient=$backupClient;PreviousSourceHash=$Previous.SourceHash;PreviousTargetHash=$Previous.TargetHash}
}
function Assert-LegacyPreparedState([string]$Target,[string]$MetadataPath,[string]$Source,[string]$SourceRoot,[string]$LegacyCacheRoot,[string]$SourceHash,[byte[]]$Replacement,[int]$Slot){
    $legacySlot=Join-Path (Join-Path $LegacyCacheRoot $SourceHash) ('slot-{0:D3}'-f$Slot)
    Assert-ChildPath $LegacyCacheRoot $legacySlot
    Assert-NoReparseAncestors $legacySlot
    foreach($sourceDirectory in @($SourceRoot,(Split-Path -Parent $Source))){
        if($sourceDirectory.TrimEnd('\').Equals($LegacyCacheRoot.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)-or$sourceDirectory.StartsWith($LegacyCacheRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Legacy cache root must not contain a source directory.'}
    }
    if($MetadataPath-ne(Join-Path $legacySlot 'social-client.json')){throw 'Legacy metadata path is outside its registered slot.'}
    if(-not(Test-Path -LiteralPath $MetadataPath -PathType Leaf)-or-not(Test-Path -LiteralPath $Target -PathType Leaf)){throw 'Prepared social client state is incomplete; cleanup is required.'}
    if((Test-ReparsePoint $MetadataPath)-or(Test-ReparsePoint $Target)){throw 'Legacy prepared client and metadata must be regular files.'}
    $meta=Get-JsonFile $MetadataPath
    if([string]$meta.schema-notin@('openNanaimo.social-client.v2','openNanaimo.social-client.v3')){throw "Unsupported legacy social client schema: $MetadataPath"}
    if($null-ne$meta.PSObject.Properties['cache_root']-or$null-ne$meta.PSObject.Properties['config_sha256']){throw "Invalid legacy social client fields: $MetadataPath"}
    if([string]$meta.source_path-ne$Source-or[string]$meta.source_sha256-ne$SourceHash-or[long]$meta.source_length-ne(Get-Item -LiteralPath $Source).Length){throw "Legacy social client source identity mismatch: $MetadataPath"}
    if([string]$meta.placement-ne'source-directory-adjacent'-or[string]$meta.prepared_client-ne$Target){throw "Legacy social client target identity mismatch: $MetadataPath"}
    if([string]$meta.mutex_file_offset-ne('0x{0:X}'-f$MutexFileOffset)-or[string]$meta.original_mutex-ne$OriginalMutexName-or[string]$meta.mutex_name-ne[Text.Encoding]::ASCII.GetString($Replacement)){throw "Legacy social client mutex identity mismatch: $MetadataPath"}
    $targetHash=Get-Sha256 $Target
    if([string]$meta.target_sha256-ne$targetHash-or(Get-Item -LiteralPath $Target).Length-ne[long]$meta.source_length){throw "Legacy social client target hash or length mismatch: $MetadataPath"}
    [byte[]]$expectedMutex=$Replacement+[byte]0
    Assert-Bytes $Target $MutexFileOffset $expectedMutex 'legacy social mutex name and terminator'
    $image=[IO.File]::ReadAllBytes($Source)
    if((Get-BytesSha256 $image)-ne$SourceHash-or$image[$MutexFileOffset+$Replacement.Length]-ne0){throw 'Legacy social client source image changed or has an unsupported mutex terminator.'}
    [Array]::Copy($Replacement,0,$image,$MutexFileOffset,$Replacement.Length)
    if((Get-BytesSha256 $image)-ne$targetHash){throw 'Legacy social client differs from the source outside the 13-byte mutex name region.'}
    $legacyWork=Join-Path $legacySlot 'work'
    $markerPath=Join-Path $legacyWork 'social-worktree.json'
    if([string]$meta.schema-eq'openNanaimo.social-client.v3'){
        if([string]$meta.static_root-ne$SourceRoot-or[string]$meta.work_directory-ne$legacyWork-or[string]$meta.runtime_directory-ne(Join-Path $legacyWork 'runtime')){throw "Legacy social worktree identity mismatch: $MetadataPath"}
        Assert-NoReparseAncestors $legacyWork
        if(-not(Test-Path -LiteralPath $markerPath -PathType Leaf)){throw "Missing legacy social worktree metadata: $markerPath"}
        if(Test-ReparsePoint $markerPath){throw 'Legacy social worktree metadata must be a regular file.'}
        $marker=Get-JsonFile $markerPath
        if([string]$marker.schema-ne'openNanaimo.social-worktree.v1'-or[string]$marker.source_root-ne$SourceRoot-or[string]$marker.work_root-ne$legacyWork){throw "Invalid legacy social worktree metadata: $markerPath"}
        if($null-ne$marker.PSObject.Properties['cache_root']-or$null-ne$marker.PSObject.Properties['config_sha256']){throw "Invalid legacy social worktree fields: $markerPath"}
    }else{
        if($SourceRoot-ne(Split-Path -Parent $Source)-or$null-ne$meta.PSObject.Properties['work_directory']-or$null-ne$meta.PSObject.Properties['static_root']-or$null-ne$meta.PSObject.Properties['runtime_directory']-or(Test-Path -LiteralPath $legacyWork)-or(Test-Path -LiteralPath (Join-Path $legacySlot 'social-worktree.json'))){throw "Invalid legacy v2 social worktree state: $MetadataPath"}
        $legacyWork=$legacySlot
        $markerPath=$null
    }
    foreach($directoryName in @('StateOption','config','configs')){
        $directory=Join-Path $legacyWork $directoryName
        Assert-NoReparseAncestors $directory
    }
    return [pscustomobject]@{Metadata=$meta;MetadataPath=$MetadataPath;MetadataHash=(Get-Sha256 $MetadataPath);CacheRoot=$LegacyCacheRoot;SlotDirectory=$legacySlot;WorkDirectory=$legacyWork;MarkerPath=$markerPath}
}
function Find-LegacyPreparedState([string]$Target,[string]$Source,[string]$SourceRoot,[string]$CurrentCacheRoot,[string]$SourceHash,[byte[]]$Replacement,[int]$Slot){
    $roots=@((Join-Path $SourceRoot '.openNanaimo-social'))
    if(-not[string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)){$roots+=Join-Path $env:LOCALAPPDATA 'openNanaimo\social-clients'}
    foreach($root in ($roots|Select-Object -Unique)){
        $legacyRoot=Get-FullPath $root
        if($legacyRoot.TrimEnd('\')-eq$CurrentCacheRoot.TrimEnd('\')){continue}
        Assert-NoReparseAncestors $legacyRoot
        $legacySlot=Join-Path (Join-Path $legacyRoot $SourceHash) ('slot-{0:D3}'-f$Slot)
        Assert-NoReparseAncestors $legacySlot
        $legacyMetadata=Join-Path $legacySlot 'social-client.json'
        if(-not(Test-Path -LiteralPath $legacyMetadata -PathType Leaf)){
            if(Test-Path -LiteralPath $legacySlot){throw 'Legacy prepared social client state is incomplete.'}
            continue
        }
        Assert-LegacyPreparedState $Target $legacyMetadata $Source $SourceRoot $legacyRoot $SourceHash $Replacement $Slot
    }
}
function Convert-LegacyPreparedState([object[]]$Records,[string]$Target,[string]$Source,[string]$SourceRoot,[string]$CacheRoot,[string]$SourceHash,[byte[]]$Replacement,[int]$Slot,[string]$ConfigText){
    $finalSlot=Join-Path (Join-Path $CacheRoot $SourceHash) ('slot-{0:D3}'-f$Slot)
    $finalWork=Join-Path $finalSlot 'work'
    $stage=Join-Path (Split-Path -Parent $finalSlot) ('slot-{0:D3}.migrate-{1}'-f$Slot,[guid]::NewGuid().ToString('N'))
    $stageWork=Join-Path $stage 'work'
    Assert-ChildPath $CacheRoot $stage
    Assert-NoReparseAncestors $stage
    $backup=Join-Path $stage 'legacy-backup'
    $currentRemoved=$false;$committed=$false
    try{
        New-Item -ItemType Directory -Path $backup -Force|Out-Null
        New-IsolatedClientWorkTree $SourceRoot $stageWork $CacheRoot ''
        $recordIndex=0
        foreach($record in $Records){
            $recordBackup=Join-Path $backup ('registration-{0:D3}'-f$recordIndex)
            New-Item -ItemType Directory -Path $recordBackup -Force|Out-Null
            Copy-Item -LiteralPath $record.MetadataPath -Destination (Join-Path $recordBackup 'social-client.json')
            if($record.MarkerPath){Copy-Item -LiteralPath $record.MarkerPath -Destination (Join-Path $recordBackup 'social-worktree.json')}
            foreach($directoryName in @('StateOption','config','configs')){
                $directory=Join-Path $record.WorkDirectory $directoryName
                if(Test-Path -LiteralPath $directory -PathType Container){Copy-IsolatedDirectoryContents $directory (Join-Path $recordBackup $directoryName) $CacheRoot}
            }
            $recordIndex++
        }
        $preferred=@($Records|Where-Object{$_.Metadata.schema-eq'openNanaimo.social-client.v3'})
        if($preferred.Count-gt1){throw 'Multiple registered legacy worktrees cannot be merged automatically.'}
        if($preferred.Count-eq0){$preferred=@($Records[0])}
        $oldConfig=Join-Path $preferred[0].WorkDirectory 'StateOption\gamestartoption.ini'
        $newConfig=Join-Path $stageWork 'StateOption\gamestartoption.ini'
        if(-not[string]::IsNullOrWhiteSpace($ConfigText)){[IO.File]::WriteAllText($newConfig,$ConfigText,(New-Object Text.ASCIIEncoding))}
        elseif(Test-Path -LiteralPath $oldConfig -PathType Leaf){Copy-Item -LiteralPath $oldConfig -Destination $newConfig -Force}
        $configHash=Get-OptionalFileHash $newConfig
        $markerPath=Join-Path $stageWork 'social-worktree.json'
        $marker=Get-JsonFile $markerPath
        $marker.work_root=$finalWork;$marker.config_sha256=$configHash
        [IO.File]::WriteAllText($markerPath,($marker|ConvertTo-Json -Depth 4),(New-Object Text.UTF8Encoding($false)))
        $meta=[ordered]@{schema='openNanaimo.social-client.v4';source_path=$Source;source_sha256=$SourceHash;source_length=(Get-Item -LiteralPath $Source).Length;placement='source-directory-adjacent';prepared_client=$Target;static_root=$SourceRoot;cache_root=$CacheRoot;work_directory=$finalWork;runtime_directory=(Join-Path $finalWork 'runtime');config_sha256=$configHash;mutex_file_offset=('0x{0:X}'-f$MutexFileOffset);original_mutex=$OriginalMutexName;mutex_name=[Text.Encoding]::ASCII.GetString($Replacement);target_sha256=(Get-Sha256 $Target);generated_utc=[DateTime]::UtcNow.ToString('o')}
        [IO.File]::WriteAllText((Join-Path $stage 'social-client.json'),($meta|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
        Assert-NoRunningSocialProcess $Target
        foreach($record in $Records){
            if((Get-Sha256 $record.MetadataPath)-ne$record.MetadataHash){throw 'Legacy social client registration changed during migration.'}
            [void](Assert-LegacyPreparedState $Target $record.MetadataPath $Source $SourceRoot $record.CacheRoot $SourceHash $Replacement $Slot)
        }
        foreach($record in $Records){
            if($record.SlotDirectory-eq$finalSlot){$currentRemoved=$true;Remove-CacheTree $record.SlotDirectory $record.CacheRoot}
        }
        Assert-ChildPath $CacheRoot $finalSlot
        Assert-NoReparseAncestors (Split-Path -Parent $finalSlot)
        Assert-ChildPath $CacheRoot $stage
        Assert-NoReparseAncestors $stage
        [IO.Directory]::Move((Get-FullPath $stage),(Get-FullPath $finalSlot))
        $committed=$true
        foreach($record in $Records){
            if($record.SlotDirectory-ne$finalSlot){Remove-CacheTree $record.SlotDirectory $record.CacheRoot}
        }
        return [pscustomobject]@{Schemas=@($Records|ForEach-Object{$_.Metadata.schema});Backup=(Join-Path $finalSlot 'legacy-backup')}
    }finally{
        if(-not$committed-and-not$currentRemoved-and(Test-Path -LiteralPath $stage)){Remove-CacheTree $stage $CacheRoot}
    }
}

$WritableDirectoryNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($name in @('cache','caches','logs','log','temp','tmp','userdata','user','save','saves','screenshots','crash','crashes','dumps','dump','download','downloads','patch','patches','Working','runtime')){
    [void]$WritableDirectoryNames.Add($name)
}
$CopiedDirectoryNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($name in @('StateOption','config','configs')){[void]$CopiedDirectoryNames.Add($name)}
$NonRuntimeFileExtensions = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($extension in @('.zip','.7z','.rar','.tar','.log','.pdb','.i64','.idb','.bak','.pyc')){[void]$NonRuntimeFileExtensions.Add($extension)}

function Copy-IsolatedDirectoryContents([string]$SourcePath,[string]$TargetPath,[string]$CacheRoot){
    Assert-ChildPath $CacheRoot $TargetPath
    if(Test-ReparsePoint $SourcePath){throw "Cannot copy a reparse point into an isolated worktree: $SourcePath"}
    Assert-NoReparseAncestors $TargetPath
    if(-not(Test-Path -LiteralPath $TargetPath -PathType Container)){New-Item -ItemType Directory -Path $TargetPath -Force|Out-Null}
    foreach($item in @(Get-ChildItem -LiteralPath $SourcePath -Force)){
        if(($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){throw "Cannot copy a reparse point into an isolated worktree: $($item.FullName)"}
        $destination=Join-Path $TargetPath $item.Name
        Assert-ChildPath $CacheRoot $destination
        if($item.PSIsContainer){Copy-IsolatedDirectoryContents $item.FullName $destination $CacheRoot}else{Copy-Item -LiteralPath $item.FullName -Destination $destination}
    }
}
# Working contains authored apartment assets as well as private session files.
# Copy only the five asset trees, never share them or replace an existing file.
function Add-ApartmentResourceCopyPlan([string]$SourcePath,[string]$TargetPath,[string]$CacheRoot,[Collections.Generic.List[object]]$Plan){
    Assert-ChildPath $CacheRoot $TargetPath
    Assert-NoReparseAncestors $SourcePath
    Assert-NoReparseAncestors $TargetPath
    foreach($item in @(Get-ChildItem -LiteralPath $SourcePath -Force)){
        if(($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){throw "Apartment resource cannot be a reparse point: $($item.FullName)"}
        $destination=Join-Path $TargetPath $item.Name
        Assert-ChildPath $CacheRoot $destination
        if($item.PSIsContainer){
            Add-ApartmentResourceCopyPlan $item.FullName $destination $CacheRoot $Plan
        }else{
            $existing=Get-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
            if($null-ne$existing){
                if($existing.PSIsContainer-or($existing.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){throw "Apartment resource destination must be a regular file: $destination"}
            }else{
                [void]$Plan.Add([pscustomobject]@{Source=$item.FullName;Target=$destination})
            }
        }
    }
}
function Sync-IsolatedApartmentResources([string]$SourceRoot,[string]$WorkRoot,[string]$CacheRoot){
    $sourceWorking=Join-Path $SourceRoot 'Working'
    $targetWorking=Join-Path $WorkRoot 'Working'
    Assert-ChildPath $CacheRoot $targetWorking
    Assert-NoReparseAncestors $sourceWorking
    Assert-NoReparseAncestors $targetWorking
    $plan=[Collections.Generic.List[object]]::new()
    # Validate all source and destination branches before publishing any asset.
    foreach($name in @('floor','Wall','Obj','Carpet','Frame')){
        $sourceTree=Join-Path $sourceWorking $name
        Assert-NoReparseAncestors $sourceTree
        if(Test-Path -LiteralPath $sourceTree -PathType Container){
            Add-ApartmentResourceCopyPlan $sourceTree (Join-Path $targetWorking $name) $CacheRoot $plan
        }
    }
    foreach($entry in $plan){
        $parent=Split-Path -Parent $entry.Target
        Assert-ChildPath $CacheRoot $parent
        Assert-NoReparseAncestors $parent
        Assert-NoReparseAncestors (Split-Path -Parent $entry.Source)
        $sourceFile=Get-Item -LiteralPath $entry.Source -Force
        if($sourceFile.PSIsContainer-or($sourceFile.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){throw "Apartment resource must be a regular file: $($entry.Source)"}
        New-Item -ItemType Directory -Path $parent -Force|Out-Null
        $temporary=Join-Path $parent ('.apartment-resource.'+[guid]::NewGuid().ToString('N')+'.tmp')
        Assert-ChildPath $CacheRoot $temporary
        [IO.File]::Copy($entry.Source,$temporary,$false)
        # The non-replacing move publishes only complete files. Failed staging
        # remains private and does not replace the destination on a later retry.
        [IO.File]::Move($temporary,$entry.Target)
    }
}
function New-IsolatedDirectoryLink([string]$SourcePath,[string]$TargetPath,[string]$CacheRoot){
    Assert-ChildPath $CacheRoot $TargetPath
    if(Test-Path -LiteralPath $TargetPath){throw "Social worktree target already exists: $TargetPath"}
    try{
        New-Item -ItemType Junction -Path $TargetPath -Target $SourcePath -Force|Out-Null
    }catch{
        if(Test-Path -LiteralPath $TargetPath){throw}
        Copy-IsolatedDirectoryContents $SourcePath $TargetPath $CacheRoot
    }
}

function Quote-NativeArgument([string]$Argument){
    if($null -eq $Argument-or$Argument.Length-eq0){return '""'}
    if($Argument -notmatch '[\s"]'){return $Argument}
    $builder=[Text.StringBuilder]::new();[void]$builder.Append('"');$slashes=0
    foreach($character in $Argument.ToCharArray()){
        if($character-eq'\'){$slashes++;continue}
        if($character-eq'"'){
            for($slashIndex=0;$slashIndex-lt(2*$slashes+1);$slashIndex++){[void]$builder.Append('\')}
            [void]$builder.Append('"');$slashes=0;continue
        }
        for($slashIndex=0;$slashIndex-lt$slashes;$slashIndex++){[void]$builder.Append('\')}
        [void]$builder.Append($character);$slashes=0
    }
    for($slashIndex=0;$slashIndex-lt(2*$slashes);$slashIndex++){[void]$builder.Append('\')}
    [void]$builder.Append('"');return $builder.ToString()
}

function Sync-IsolatedPetCatalog([string]$SourceRoot,[string]$WorkRoot,[string]$CacheRoot){
    $source=Join-Path $SourceRoot 'pi._D7'
    if(-not(Test-Path -LiteralPath $source -PathType Leaf)){return}
    $target=Join-Path $WorkRoot 'pi._D7'
    Assert-ChildPath $CacheRoot $target
    Assert-NoReparseAncestors (Split-Path $target -Parent)
    if(Test-ReparsePoint $target){throw 'Prepared pet catalog cannot be a reparse point.'}
    $sourceHash=Get-OptionalFileHash $source
    $oldHash=Get-OptionalFileHash $target
    if($oldHash-eq$sourceHash){return}
    if($oldHash){
        $backupDir=Join-Path $CacheRoot ("pet-catalog-backups\{0}"-f$oldHash)
        Assert-ChildPath $CacheRoot $backupDir
        Assert-NoReparseAncestors $backupDir
        New-Item -ItemType Directory -Path $backupDir -Force|Out-Null
        $backup=Join-Path $backupDir 'pi._D7'
        if(Test-Path -LiteralPath $backup){if((Get-OptionalFileHash $backup)-ne$oldHash){throw 'Pet catalog backup mismatch.'}}
        else{Copy-Item -LiteralPath $target -Destination $backup}
    }
    # Caller holds the slot lock and has rejected an active client process.
    # A failed copy/hash check aborts preparation, never starts that client.
    Copy-Item -LiteralPath $source -Destination $target -Force
    if((Get-OptionalFileHash $target)-ne$sourceHash){throw 'Prepared pet catalog refresh failed.'}
}

. (Join-Path $PSScriptRoot 'lumineos_resource_identity.ps1')
. (Join-Path $PSScriptRoot 'korean_pet_resource_identity.ps1')

function New-IsolatedClientWorkTree([string]$SourceRoot,[string]$WorkRoot,[string]$CacheRoot,[string]$ConfigText){
    Assert-ChildPath $CacheRoot $WorkRoot
    Assert-NoReparseAncestors $WorkRoot
    New-Item -ItemType Directory -Path $WorkRoot -Force|Out-Null
    $cacheFull=(Get-FullPath $CacheRoot).TrimEnd('\')
    foreach($sourceDirectory in @(Get-ChildItem -LiteralPath $SourceRoot -Directory -Force)){
        if($sourceDirectory.Name-in@('.openNanaimo-social','.openNanaimo-resource-backups')){continue}
        $sourceFull=Get-FullPath $sourceDirectory.FullName
        if($cacheFull.Equals($sourceFull,[StringComparison]::OrdinalIgnoreCase)-or$cacheFull.StartsWith($sourceFull.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){continue}
        $targetPath=Join-Path $WorkRoot $sourceDirectory.Name
        if($CopiedDirectoryNames.Contains($sourceDirectory.Name)){
            Copy-IsolatedDirectoryContents $sourceFull $targetPath $CacheRoot
        }elseif($WritableDirectoryNames.Contains($sourceDirectory.Name)){
            New-Item -ItemType Directory -Path $targetPath -Force|Out-Null
        }else{
            New-IsolatedDirectoryLink $sourceFull $targetPath $CacheRoot
        }
    }
    foreach($sourceFile in @(Get-ChildItem -LiteralPath $SourceRoot -File -Force)){
        if($sourceFile.Name -eq 'Game.exe' -or $sourceFile.Name -like 'Game.openNanaimo-social-*.exe*' -or $sourceFile.Name -eq 'social-worktree.json'){continue}
        if($NonRuntimeFileExtensions.Contains($sourceFile.Extension)){continue}
        $targetPath=Join-Path $WorkRoot $sourceFile.Name
        Assert-ChildPath $CacheRoot $targetPath
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $targetPath
    }
    foreach($directoryName in @('Working','runtime')){New-Item -ItemType Directory -Path (Join-Path $WorkRoot $directoryName) -Force|Out-Null}
    Sync-IsolatedApartmentResources $SourceRoot $WorkRoot $CacheRoot
    Sync-LumineosResourceIdentity $SourceRoot $WorkRoot $CacheRoot
    Sync-IsolatedPetCatalog $SourceRoot $WorkRoot $CacheRoot
    Sync-KoreanPetResourceIdentity $SourceRoot $WorkRoot $CacheRoot
    $stateDirectory=Join-Path $WorkRoot 'StateOption'
    if(-not(Test-Path -LiteralPath $stateDirectory -PathType Container)){New-Item -ItemType Directory -Path $stateDirectory -Force|Out-Null}
    if(-not[string]::IsNullOrWhiteSpace($ConfigText)){
        [IO.File]::WriteAllText((Join-Path $stateDirectory 'gamestartoption.ini'),$ConfigText,(New-Object Text.ASCIIEncoding))
    }
    $configHash=Get-OptionalFileHash (Join-Path $stateDirectory 'gamestartoption.ini')
    $marker=[ordered]@{schema='openNanaimo.social-worktree.v2';source_root=(Get-FullPath $SourceRoot);cache_root=(Get-FullPath $CacheRoot);work_root=(Get-FullPath $WorkRoot);config_sha256=$configHash;generated_utc=[DateTime]::UtcNow.ToString('o')}
    [IO.File]::WriteAllText((Join-Path $WorkRoot 'social-worktree.json'),($marker|ConvertTo-Json -Depth 4),(New-Object Text.UTF8Encoding($false)))
}
function Remove-CacheTree([string]$Path,[string]$CacheRoot){
    $pathFull=Get-FullPath $Path
    Assert-ChildPath $CacheRoot $pathFull
    Assert-NoReparseAncestors (Split-Path -Parent $pathFull)
    $item=Get-Item -LiteralPath $pathFull -Force -ErrorAction SilentlyContinue
    if($null-eq$item){return}
    if(($item.Attributes-band[IO.FileAttributes]::ReparsePoint)-ne0){
        if($item.PSIsContainer){[IO.Directory]::Delete($pathFull,$false)}else{Remove-Item -LiteralPath $pathFull -Force}
        return
    }
    if($item.PSIsContainer){
        foreach($child in @(Get-ChildItem -LiteralPath $pathFull -Force)){
            $childFull=Get-FullPath $child.FullName
            Assert-ChildPath $CacheRoot $childFull
            Remove-CacheTree $childFull $CacheRoot
        }
        [IO.Directory]::Delete($pathFull,$false)
    }else{Remove-Item -LiteralPath $pathFull -Force}
}
function Get-RunningProcessImagePath([object]$Process){
    $imagePath=[string]$Process.ExecutablePath
    if(-not[string]::IsNullOrWhiteSpace($imagePath)){
        try{return Get-FullPath $imagePath}catch{return $null}
    }
    # Win32_Process.ExecutablePath is empty when the caller cannot query the
    # process token (for example, a client launched elevated). Try the local
    # process API before classifying the same-name process as this slot.
    try{
        $owned=[Diagnostics.Process]::GetProcessById([int]$Process.ProcessId)
        try{
            if($owned.HasExited){return $null}
            $module=$owned.MainModule
            if($null-ne$module-and-not[string]::IsNullOrWhiteSpace([string]$module.FileName)){return Get-FullPath ([string]$module.FileName)}
        }finally{$owned.Dispose()}
    }catch{}
    return $null
}
function Get-SocialSlotMutexName([string]$TargetName){
    if($TargetName-match '^Game\.openNanaimo-social-(\d{3})\.exe$'){return 'NanaimoSoc'+$matches[1]}
    return $null
}
function Test-SocialSlotMutex([string]$Name){
    if([string]::IsNullOrWhiteSpace($Name)){return $false}
    $mutex=$null
    try{$mutex=[Threading.Mutex]::OpenExisting($Name);return $true}catch{return $false}finally{if($mutex){$mutex.Dispose()}}
}
function Assert-NoRunningSocialProcess([string]$Executable){
    $targetFull=Get-FullPath $Executable
    $targetName=Split-Path -Leaf $targetFull
    $slotMutex=Get-SocialSlotMutexName $targetName
    $mutexExists=Test-SocialSlotMutex $slotMutex
    $running=@(Get-CimInstance Win32_Process -Filter "Name='$targetName'" -ErrorAction Stop|Where-Object{
        $imagePath=Get-RunningProcessImagePath $_
        if($null-ne$imagePath){return $imagePath.Equals($targetFull,[StringComparison]::OrdinalIgnoreCase)}
        # An inaccessible process image is only attributed to this slot when
        # the patched client has also created this slot's named mutex. A dead
        # or failed-start same-name process otherwise must not poison P1/P2/P3
        # preparation and retry.
        return $mutexExists
    })
    if($running.Count-gt0){throw "Social slot is running; prepare, configuration changes and cleanup are refused: $targetFull"}
}

function Start-IsolatedSocialProcess([string]$Executable,[string]$WorkRoot,[string[]]$Arguments){
    $environmentRoot=Join-Path $WorkRoot 'runtime'
    foreach($directory in @($environmentRoot,(Join-Path $environmentRoot 'appdata'),(Join-Path $environmentRoot 'localappdata'),(Join-Path $environmentRoot 'temp'))){
        Assert-NoReparseAncestors $directory
        if(-not(Test-Path -LiteralPath $directory -PathType Container)){New-Item -ItemType Directory -Path $directory -Force|Out-Null}
    }
    $startInfo=[Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName=$Executable
    $startInfo.WorkingDirectory=$WorkRoot
    $startInfo.UseShellExecute=$false
    $startInfo.Arguments=( @($Arguments|ForEach-Object{Quote-NativeArgument ([string]$_)}) -join ' ' )
    $startInfo.EnvironmentVariables['APPDATA']=Join-Path $environmentRoot 'appdata'
    $startInfo.EnvironmentVariables['LOCALAPPDATA']=Join-Path $environmentRoot 'localappdata'
    $startInfo.EnvironmentVariables['TEMP']=Join-Path $environmentRoot 'temp'
    $startInfo.EnvironmentVariables['TMP']=Join-Path $environmentRoot 'temp'
    $process=[Diagnostics.Process]::new();$process.StartInfo=$startInfo
    if(-not$process.Start()){throw "Unable to start isolated social client: $Executable"}
    return $process
}

$source=Get-FullPath $ClientPath
if(-not(Test-Path -LiteralPath $source -PathType Leaf)){throw "Game.exe not found: $source"}
if([string]::IsNullOrWhiteSpace($WorkingDirectory)){$WorkingDirectory=Split-Path -Parent $source}
$WorkingDirectory=Get-FullPath $WorkingDirectory
if(-not(Test-Path -LiteralPath $WorkingDirectory -PathType Container)){throw "Static resource root not found: $WorkingDirectory"}
if([string]::IsNullOrWhiteSpace($CacheRoot)){
    $CacheRoot=Join-Path $WorkingDirectory '.openNanaimo-social'
}
$CacheRoot=Get-FullPath $CacheRoot
Assert-SupportedClient $source
$sourceHash=Get-Sha256 $source
$mutexName=('NanaimoSoc{0:D3}'-f$SocialSlot)
if($mutexName.Length-ne$OriginalMutexName.Length){throw "Internal mutex name length mismatch: $mutexName"}
$replacement=[Text.Encoding]::ASCII.GetBytes($mutexName)
$slotDir=Join-Path (Join-Path $CacheRoot $sourceHash) ('slot-{0:D3}'-f$SocialSlot)
Assert-ChildPath $CacheRoot $slotDir
$workDir=Join-Path $slotDir 'work'
$runtimeDir=Join-Path $workDir 'runtime'
$target=Join-Path (Split-Path -Parent $source) ('Game.openNanaimo-social-{0:D3}.exe'-f$SocialSlot)
$metadata=Join-Path $slotDir 'social-client.json'

if($PrepareOnly-and$CleanupPreparedCopy){throw 'PrepareOnly and CleanupPreparedCopy are mutually exclusive.'}
foreach($sourceDirectory in @($WorkingDirectory,(Split-Path -Parent $source))){
    if($sourceDirectory.TrimEnd('\').Equals($CacheRoot.TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)-or$sourceDirectory.StartsWith($CacheRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Cache root must not contain a source directory.'}
}
Assert-NoReparseAncestors (Split-Path -Parent $source)
Assert-NoReparseAncestors $WorkingDirectory
$lockKey=Get-BytesSha256 ([Text.Encoding]::UTF8.GetBytes($target.ToUpperInvariant()))
$slotLock=[Threading.Mutex]::new($false,('Local\openNanaimo.social-client.'+$lockKey))
$lockAcquired=$false
try{
    try{$lockAcquired=$slotLock.WaitOne(0)}catch [Threading.AbandonedMutexException]{$lockAcquired=$true}
    if(-not$lockAcquired){throw 'Social slot is already being prepared, launched or cleaned.'}
    Assert-NoRunningSocialProcess $target
    Assert-CacheRoot $CacheRoot
    Assert-NoReparseAncestors $slotDir
    $hasState=(Test-Path -LiteralPath $target)-or(Test-Path -LiteralPath $slotDir)
    $state=$null
    $legacyRecords=@();$migration=$null;$sourceUpdate=$null
    if($hasState){
        if(Test-Path -LiteralPath $metadata -PathType Leaf){
            if(Test-ReparsePoint $metadata){throw 'Prepared metadata cannot be a reparse point.'}
            $registered=Get-JsonFile $metadata
            if([string]$registered.schema-in@('openNanaimo.social-client.v2','openNanaimo.social-client.v3')){
                $legacyRecords=@(Assert-LegacyPreparedState $target $metadata $source $WorkingDirectory $CacheRoot $sourceHash $replacement $SocialSlot)
            }else{$state=Assert-PreparedState $target $metadata $source $WorkingDirectory $CacheRoot $workDir $runtimeDir $sourceHash $replacement (-not$CleanupPreparedCopy)}
        }elseif((Test-Path -LiteralPath $target -PathType Leaf)-and-not(Test-Path -LiteralPath $slotDir)){
            $previous=Find-PreviousSourcePreparedState $target $source $WorkingDirectory $CacheRoot $sourceHash $replacement $SocialSlot
            if($previous){
                $sourceUpdate=Backup-PreviousSourceClient $previous $target $CacheRoot
                $hasState=$false
            }else{
                $legacyRecords=@(Find-LegacyPreparedState $target $source $WorkingDirectory $CacheRoot $sourceHash $replacement $SocialSlot)
                if($legacyRecords.Count-eq0){throw 'Prepared social client state is incomplete: no verifiable registration for the current or previous source; refusing automatic cleanup.'}
            }
        }else{throw 'Prepared social client state is incomplete; cleanup is required.'}
    }

    if($CleanupPreparedCopy){
        if($hasState){
            Assert-NoRunningSocialProcess $target
            if($legacyRecords.Count){foreach($record in $legacyRecords){Remove-CacheTree $record.SlotDirectory $record.CacheRoot}}
            else{Remove-CacheTree $slotDir $CacheRoot}
            Remove-Item -LiteralPath $target -Force
        }
        [pscustomobject]@{Action='cleanup';SocialSlot=$SocialSlot;SourceSha256=$sourceHash;SourceUpdateBackup=$(if($sourceUpdate){$sourceUpdate.Backup}else{$null});PreparedClient=$target;MutexName=$mutexName;Removed=(-not(Test-Path -LiteralPath $target)-and-not(Test-Path -LiteralPath $slotDir))}
        return
    }
    if($legacyRecords.Count){
        $migration=Convert-LegacyPreparedState $legacyRecords $target $source $WorkingDirectory $CacheRoot $sourceHash $replacement $SocialSlot $LaunchModeConfigText
        $state=Assert-PreparedState $target $metadata $source $WorkingDirectory $CacheRoot $workDir $runtimeDir $sourceHash $replacement
    }

$resourceMigration=$null
if($hasState){
    Sync-IsolatedApartmentResources $WorkingDirectory $workDir $CacheRoot
    Sync-LumineosResourceIdentity $WorkingDirectory $workDir $CacheRoot
    Sync-IsolatedPetCatalog $WorkingDirectory $workDir $CacheRoot
    Sync-KoreanPetResourceIdentity $WorkingDirectory $workDir $CacheRoot
    $entertainmentState=Join-Path $WorkingDirectory 'animalstate.st'
    if(Test-Path -LiteralPath $entertainmentState -PathType Leaf){
        $isolatedState=Join-Path $workDir 'animalstate.st'
        Assert-ChildPath $CacheRoot $isolatedState
        Assert-NoReparseAncestors $workDir
        if((Test-Path -LiteralPath $isolatedState)-and((Get-Item -LiteralPath $isolatedState -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Entertainment state resource must be a regular file.'}
        if(-not(Test-Path -LiteralPath $isolatedState)){
            if((Get-Item -LiteralPath $entertainmentState).Length-ne868){throw 'Invalid entertainment state resource.'}
            Copy-Item -LiteralPath $entertainmentState -Destination $isolatedState
        }
    }
    $resourceMigration=Update-PreparedResourceIsolation $target $metadata $state $source $sourceHash $replacement $CacheRoot
    if($resourceMigration){$state=Assert-PreparedState $target $metadata $source $WorkingDirectory $CacheRoot $workDir $runtimeDir $sourceHash $replacement}
}

$reused=$hasState
if(-not$reused){
    [void](New-Item -ItemType Directory -Path $slotDir -Force)
    $temp=$target+'.tmp.'+[guid]::NewGuid().ToString('N')
    try{
        Copy-Item -LiteralPath $source -Destination $temp -Force
        $stream=[IO.File]::Open($temp,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        try{[void]$stream.Seek($MutexFileOffset,[IO.SeekOrigin]::Begin);$stream.Write($replacement,0,$replacement.Length);$stream.Flush($true)}finally{$stream.Dispose()}
        Assert-Bytes $temp $MutexFileOffset $replacement 'prepared social mutex name'
        Set-ResourceReadSharing $temp
        if((Get-Sha256 $temp)-ne(Get-ExpectedSocialClientHash $source $sourceHash $replacement $true)){throw 'Social client source changed during preparation.'}
        New-IsolatedClientWorkTree $WorkingDirectory $workDir $CacheRoot $LaunchModeConfigText
        Assert-NoRunningSocialProcess $target
        Move-Item -LiteralPath $temp -Destination $target
        $targetHash=Get-Sha256 $target
        $configHash=Get-OptionalFileHash (Join-Path (Join-Path $workDir 'StateOption') 'gamestartoption.ini')
        $meta=[ordered]@{schema='openNanaimo.social-client.v5';resource_isolation=(Get-ResourceIsolationMetadata);source_path=$source;source_sha256=$sourceHash;source_length=(Get-Item -LiteralPath $source).Length;placement='source-directory-adjacent';prepared_client=$target;static_root=$WorkingDirectory;cache_root=$CacheRoot;work_directory=$workDir;runtime_directory=$runtimeDir;config_sha256=$configHash;mutex_file_offset=('0x{0:X}'-f$MutexFileOffset);original_mutex=$OriginalMutexName;mutex_name=$mutexName;target_sha256=$targetHash;generated_utc=[DateTime]::UtcNow.ToString('o')}
        [IO.File]::WriteAllText($metadata,($meta|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
    }catch{
        if($sourceUpdate){
            Assert-NoRunningSocialProcess $target
            if(Test-Path -LiteralPath $target){
                if((Test-ReparsePoint $target)-or(Get-Sha256 $target)-ne(Get-ExpectedSocialClientHash $source $sourceHash $replacement $true)){throw 'Source rollover failed and target changed; verified backup retained for manual recovery.'}
            }
            Assert-ChildPath $CacheRoot $slotDir
            Remove-CacheTree $slotDir $CacheRoot
            Copy-Item -LiteralPath $sourceUpdate.BackupClient -Destination $target -Force
        }
        throw
    }finally{if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Force}}
}elseif(-not[string]::IsNullOrWhiteSpace($LaunchModeConfigText)){
    $requestedHash=Get-ConfigHash $LaunchModeConfigText
    if([string]$state.Marker.config_sha256-ne$requestedHash){
        Assert-NoRunningSocialProcess $target
        [IO.File]::WriteAllText((Join-Path (Join-Path $workDir 'StateOption') 'gamestartoption.ini'),$LaunchModeConfigText,(New-Object Text.ASCIIEncoding))
        $state.Marker.config_sha256=$requestedHash
        $state.Metadata.config_sha256=$requestedHash
        [IO.File]::WriteAllText((Join-Path $workDir 'social-worktree.json'),($state.Marker|ConvertTo-Json -Depth 4),(New-Object Text.UTF8Encoding($false)))
        [IO.File]::WriteAllText($metadata,($state.Metadata|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
    }
}

$process=$null;$mainWindowHandle=[IntPtr]::Zero;$startupVerified=$false
try{
    if(-not$PrepareOnly){
        Assert-NoRunningSocialProcess $target
        # Register only after preparation and the final running-process guard.
        # One invocation keeps the slot lock held across preparation and launch.
        if($BeforeLaunch){& $BeforeLaunch | Out-Null}
        $process=Start-IsolatedSocialProcess $target $workDir $ClientArguments
        $deadline=[DateTime]::UtcNow.AddMilliseconds($StartupTimeoutMilliseconds)
        while([DateTime]::UtcNow-lt$deadline){
            Start-Sleep -Milliseconds 100;$process.Refresh()
            if($process.HasExited){throw "Social client exited during startup: slot=$SocialSlot pid=$($process.Id) exit_code=$($process.ExitCode) prepared_client=$target"}
            if($process.MainWindowHandle-ne[IntPtr]::Zero){$mainWindowHandle=$process.MainWindowHandle;break}
        }
        if($mainWindowHandle-eq[IntPtr]::Zero){throw "Social client did not create a visible window within $StartupTimeoutMilliseconds ms: slot=$SocialSlot pid=$($process.Id) prepared_client=$target"}
        $startupVerified=$true
    }
    [pscustomobject]@{Action=$(if($PrepareOnly){'prepare'}else{'launch'});SocialSlot=$SocialSlot;SourceSha256=$sourceHash;StaticRoot=$WorkingDirectory;PreparedClient=$target;Metadata=$metadata;WorkDirectory=$workDir;RuntimeDirectory=$runtimeDir;Placement='source-directory-adjacent';MutexName=$mutexName;Reused=$reused;SourceRefreshed=($null-ne$sourceUpdate);PreviousSourceSha256=$(if($sourceUpdate){$sourceUpdate.PreviousSourceHash}else{$null});SourceUpdateBackup=$(if($sourceUpdate){$sourceUpdate.Backup}else{$null});MigratedFrom=@(if($migration){$migration.Schemas});MigrationBackup=$(if($migration){$migration.Backup}else{$null});ResourceIsolation=(Get-ResourceIsolationMetadata);ResourceIsolationRefreshed=($null-ne$resourceMigration);ResourceMigrationBackup=$(if($resourceMigration){$resourceMigration.Backup}else{$null});ProcessId=$(if($process){$process.Id}else{$null});StartupVerified=$startupVerified;MainWindowHandle=$(if($process){$mainWindowHandle.ToInt64()}else{$null})}
}catch{
    if($process){
        try{
            $process.Refresh()
            if(-not$process.HasExited){$process.Kill();[void]$process.WaitForExit(5000)}
        }catch{}
    }
    throw
}
}finally{
    if($lockAcquired){$slotLock.ReleaseMutex()}
    $slotLock.Dispose()
}
