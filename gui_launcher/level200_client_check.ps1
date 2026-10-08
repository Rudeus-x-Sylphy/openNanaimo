param([Parameter(Mandatory=$true)][string]$Client)
$ErrorActionPreference='Stop'
# The level-200 server must never talk the new town layout to an old client.
$data=[IO.File]::ReadAllBytes([IO.Path]::GetFullPath($Client))
function U16([int]$o){ if($o-lt0-or$o+2-gt$data.Length){throw 'Truncated client PE'};[BitConverter]::ToUInt16($data,$o) }
function U32([int]$o){ if($o-lt0-or$o+4-gt$data.Length){throw 'Truncated client PE'};[BitConverter]::ToUInt32($data,$o) }
if((U16 0)-ne0x5A4D){throw 'Client is not PE'}
$pe=[int](U32 0x3C)
if((U32 $pe)-ne0x4550-or(U16 ($pe+4))-ne0x14C-or(U16 ($pe+24))-ne0x10B){throw 'CN x86 PE32 required'}
$base=U32 ($pe+52);$count=U16 ($pe+6);$sections=$pe+24+(U16 ($pe+20))
if($base-ne0x400000){throw 'Unknown client image base'}
$sites=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'level200_client_sites.json') -Raw -Encoding UTF8|ConvertFrom-Json
foreach($site in $sites){
 $rva=[long]$site.va-$base;$size=$site.target.Length/2;$offset=-1
 for($i=0;$i-lt$count;$i++){
  $s=$sections+40*$i;$start=U32 ($s+12);$rawSize=U32 ($s+16);$raw=U32 ($s+20)
  if($rva-ge$start-and$rva+$size-le[long]$start+$rawSize){$offset=[long]$raw+$rva-$start;break}
 }
 if($offset-lt0-or$offset+$size-gt$data.Length){throw 'Client compatibility site outside mapped file'}
 for($i=0;$i-lt$size;$i++){
  if($data[$offset+$i]-ne[Convert]::ToByte($site.target.Substring($i*2,2),16)){throw "Level200 client compatibility mismatch: $($site.operation). Prepare matching client/server/worker before launch."}
 }
}
Write-Output 'LEVEL200_CLIENT_LAYOUT_PASS'
