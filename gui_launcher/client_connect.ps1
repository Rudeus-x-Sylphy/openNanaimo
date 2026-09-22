param([Parameter(Position=0)][string]$AdapterIP,[switch]$PreviewOnly,[switch]$RegisterOnly,[switch]$ConfigureP2PFirewall,[switch]$FirewallOnly,[string]$ProfileIniOverride)
$ErrorActionPreference='Stop'
$Root=Split-Path $PSScriptRoot -Parent
$Client=Join-Path $Root 'game.exe'
$Template=Join-Path $Root 'gui_launcher\launch_modes\gamestartoption.network.ini'
$Active=Join-Path $Root 'StateOption\gamestartoption.ini'
$SavedIP=Join-Path $Root 'adapter_ip.txt'
$ProfileIni=if($ProfileIniOverride){[IO.Path]::GetFullPath($ProfileIniOverride)}else{Join-Path $Root 'nanaimo_launcher_profile.ini'}
function Test-Admin{try{$id=[Security.Principal.WindowsIdentity]::GetCurrent();$p=New-Object Security.Principal.WindowsPrincipal($id);return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)}catch{return $false}}
function Ensure-P2PFirewall{
 if(-not(Test-Admin)){throw 'Run PowerShell as Administrator to configure the Nanaimo P2P firewall.'}
 $spec=@(@{Name='Nanaimo-P2P-UDP-3534';Protocol='UDP'},@{Name='Nanaimo-P2P-TCP-3534';Protocol='TCP'})
 foreach($x in $spec){$r=Get-NetFirewallRule -DisplayName $x.Name -ErrorAction SilentlyContinue;if(-not$r){New-NetFirewallRule -DisplayName $x.Name -Direction Inbound -Action Allow -Enabled True -Profile Any -Program $Client -Protocol $x.Protocol -LocalPort 3534|Out-Null}else{$r|Set-NetFirewallRule -Enabled True -Action Allow -Profile Any|Out-Null}}
 Write-Host 'PROTOCOL_EXTENSIONS_P2P_FIREWALL_PASS UDP/TCP 3534 inbound allowed for client executable'
}
function Show-P2PFirewall{
 $ok=@(Get-NetFirewallRule -ErrorAction SilentlyContinue|Where-Object{$_.DisplayName-in @('Nanaimo-P2P-UDP-3534','Nanaimo-P2P-TCP-3534')-and$_.Enabled-eq'True'-and$_.Action-eq'Allow'}).Count-ge2
 if($ok){Write-Host 'P2P firewall: configured (UDP/TCP 3534).'}else{Write-Warning 'Nanaimo P2P inbound rules are missing. Ready-room/game chat may require an elevated one-time run with -ConfigureP2PFirewall.'}
}
if($ConfigureP2PFirewall){Ensure-P2PFirewall}
if($FirewallOnly){Show-P2PFirewall;exit 0}
if(-not$AdapterIP-and(Test-Path -LiteralPath $SavedIP)){$AdapterIP=(Get-Content -LiteralPath $SavedIP -First 1).Trim()}
if(-not$AdapterIP){$AdapterIP=(Read-Host '请输入协议适配器 IPv4 地址，例如 127.0.0.1').Trim()}
$parsed=$null
if($AdapterIP-notmatch '^\d{1,3}(\.\d{1,3}){3}$'-or-not[Net.IPAddress]::TryParse($AdapterIP,[ref]$parsed)-or$parsed.AddressFamily-ne[Net.Sockets.AddressFamily]::InterNetwork-or$parsed.ToString()-ne$AdapterIP){throw "无效的 IPv4 地址: $AdapterIP"}
if(-not(Test-Path -LiteralPath $ProfileIni)){throw "缺少客户端资源配置: $ProfileIni"}
if(-not(Test-Path -LiteralPath $Client -PathType Leaf)){throw "缺少客户端: $Client"}
if(-not(Test-Path -LiteralPath $Template)){throw "缺少 Network 模板: $Template"}
$text=Get-Content -LiteralPath $Template -Raw;$text=$text-replace '(?m)^ServerIP=.*$',("ServerIP={0}"-f$AdapterIP)
$args=[string[]]@('-q',':1:1:0:3:4:-i','5:-r',("6:7:1:{0}:"-f$AdapterIP))
Write-Host "NANAIMO CLIENT CONNECT adapter=$AdapterIP profile=$ProfileIni"
Write-Host ('Client: "'+$Client+'" '+(($args|ForEach-Object{'"'+$_+'"'})-join' '))
if($PreviewOnly){Write-Host 'PROTOCOL_EXTENSIONS_CLIENT_CONNECT_PREVIEW_PASS';exit 0}
$dir=Split-Path $Active -Parent;if(-not(Test-Path -LiteralPath $dir)){[void](New-Item -ItemType Directory -Path $dir -Force)}
[IO.File]::WriteAllText($Active,$text,(New-Object Text.ASCIIEncoding));[IO.File]::WriteAllText($SavedIP,$AdapterIP+"`r`n",(New-Object Text.ASCIIEncoding))
$tcp=New-Object Net.Sockets.TcpClient
try{$tcp.Connect($AdapterIP,11999);$stream=$tcp.GetStream();$bytes=[IO.File]::ReadAllBytes($ProfileIni);$len=[BitConverter]::GetBytes([uint32]$bytes.Length);$stream.Write($len,0,4);$stream.Write($bytes,0,$bytes.Length);$stream.Flush();$ack=New-Object byte[] 3;$got=$stream.Read($ack,0,3);if($got-ne3-or[Text.Encoding]::ASCII.GetString($ack)-ne"OK`n"){throw '协议适配器拒绝资源配置'}}finally{if($tcp){$tcp.Close()}}
Write-Host 'PROTOCOL_EXTENSIONS_CLIENT_PROFILE_REGISTER_PASS'
if($RegisterOnly){exit 0}
Get-Process -Name game -ErrorAction SilentlyContinue|Where-Object{$_.Path-eq$Client}|Stop-Process -Force;Start-Sleep -Milliseconds 250
Start-Process -FilePath $Client -ArgumentList $args -WorkingDirectory $Root|Out-Null
Write-Host '客户端已启动；本机不会启动 local Nanaimo protocol adapter。'
