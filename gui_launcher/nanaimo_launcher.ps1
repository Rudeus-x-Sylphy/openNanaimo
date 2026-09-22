param([switch]$ValidateOnly,[switch]$PreviewOnly,[switch]$SelfTestProfileIO,[switch]$SelfTestInventoryIO,[switch]$SelfTestLaunchModes,[switch]$SelfTestLayout,[switch]$SelfTestCatalogPreview,[switch]$SelfTestTitleIO,[string]$ProfileIniOverride,[string]$ProfileJsonOverride)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$Root=Split-Path $PSScriptRoot -Parent
$Client=Join-Path $Root 'game.exe'
$AdapterRuntimeRoot=Join-Path $Root 'adapter_runtime'
$Adapter=Join-Path $AdapterRuntimeRoot 'Nanaimo.Adapter.exe'
$AdapterBridge=Join-Path $AdapterRuntimeRoot 'nanaimo_gameplay_bridge.exe'
$AdapterManifest=Join-Path $AdapterRuntimeRoot 'adapter_manifest.json'
$LegacyAdapter=Join-Path $Root 'adapter\nanaimo_adapter.exe'
$AdapterData=Join-Path $Root 'adapter_data'
$AdapterLogs=Join-Path $AdapterData 'logs'
$AdapterStop=Join-Path $AdapterData 'stop.request'
$ProfileIni=if($ProfileIniOverride){[IO.Path]::GetFullPath($ProfileIniOverride)}else{Join-Path $Root 'nanaimo_launcher_profile.ini'}
$ProfileJson=if($ProfileJsonOverride){[IO.Path]::GetFullPath($ProfileJsonOverride)}else{Join-Path $Root 'nanaimo_launcher_profile.json'}
$ProfileStateRoot=if($ProfileIniOverride){Split-Path $ProfileIni -Parent}else{$Root}
$AdapterLog=Join-Path $Root 'adapter_nanaimo_launcher.log'
$AdapterErr=Join-Path $Root 'adapter_nanaimo_launcher_stderr.log'
$PetJson=Join-Path $Root 'gui_launcher\data\pets.json'
$EquipJson=Join-Path $Root 'gui_launcher\data\equipment.json'
$AttackJson=Join-Path $Root 'gui_launcher\data\pet_attack_modes.json'
$PreviewDir=Join-Path $Root 'gui_launcher\data\previews'
$PetPreviewPng=Join-Path $PreviewDir 'pet_icons.png';$PetPreviewJson=Join-Path $PreviewDir 'pet_icons.json'
$EquipPreviewPng=Join-Path $PreviewDir 'equipment_icons.png';$EquipPreviewJson=Join-Path $PreviewDir 'equipment_icons.json'
$InventoryAdminGui=Join-Path $Root 'gui_launcher\inventory_admin_gui.ps1'
if(-not(Test-Path -LiteralPath $InventoryAdminGui)){throw 'Inventory administration GUI module missing.'}
. $InventoryAdminGui
$LaunchModeDir=Join-Path $Root 'gui_launcher\launch_modes'
$NetworkOptionTemplate=Join-Path $LaunchModeDir 'gamestartoption.network.ini'
$ActiveGameOption=Join-Path $Root 'StateOption\gamestartoption.ini'
$ExpectedAdapterHash=''
$ExpectedAdapterSize=0
$ExpectedBridgeHash=''
$ExpectedBridgeSize=0
if(Test-Path -LiteralPath $AdapterManifest){
    $adapterContract=Get-Content -LiteralPath $AdapterManifest -Raw -Encoding UTF8|ConvertFrom-Json
    $adapterRow=@($adapterContract.files|Where-Object name -eq 'Nanaimo.Adapter.exe')[0]
    $bridgeRow=@($adapterContract.files|Where-Object name -eq 'nanaimo_gameplay_bridge.exe')[0]
    if($adapterRow){$ExpectedAdapterHash=[string]$adapterRow.sha256;$ExpectedAdapterSize=[long]$adapterRow.size}
    if($bridgeRow){$ExpectedBridgeHash=[string]$bridgeRow.sha256;$ExpectedBridgeSize=[long]$bridgeRow.size}
}
$GbK=[Text.Encoding]::GetEncoding(936)
$KnownDungeonTitles=@{0='修炼中的初级收集者';2='打败大机械熊偶的收集者';23='打败头脑胶囊的收集者'}
function Get-DungeonTitleIconResource([int]$grade){if($grade-lt0-or$grade-gt42){throw 'dungeon grade must be 0..42'};if($grade-le20){return 2038+$grade};if($grade-le38){return 2394+($grade-21)};return 2412}
function New-DungeonTitleChoices([int]$currentGrade=-1){
    $rows=New-Object Collections.ArrayList;$current=if($currentGrade-ge0-and$currentGrade-le42){"（当前进度 grade $currentGrade）"}else{''}
    [void]$rows.Add([pscustomobject]@{Grade=-1;ResourceId=$null;IconResource=$null;Rank='';Name='跟随进度档';Display="跟随已有地宫进度，不覆盖称号$current"})
    for($g=0;$g-le42;$g++){
        $resource=1243+$g;$icon=Get-DungeonTitleIconResource $g
        $rank=if($g-ge17-and$g-le39){'R'+($g-16)}elseif($g-ge40){'R23共享'}else{''}
        $iconText=if($rank){"$icon/$rank"}else{[string]$icon}
        $name=if($KnownDungeonTitles.ContainsKey($g)){[string]$KnownDungeonTitles[$g]}elseif($rank){"客户端图标$rank 测试档"}else{'中文名尚未逐档闭环'}
        $proof=if($g-eq23){'用户目视校准：原R4位置实际R7'}elseif($g-eq39){'用户目视校准：原R20位置实际R23'}elseif($g-ge40){'静态闭环：grade39..42共享图标2412/R23'}elseif($rank){'由grade23=R7、grade39=R23及连续资源序列推定'}elseif($KnownDungeonTitles.ContainsKey($g)){'已精确映射中文称号'}else{'仅grade/资源槽位已闭环'}
        [void]$rows.Add([pscustomobject]@{Grade=$g;ResourceId=$resource;IconResource=$icon;Rank=$rank;Name=$name;Display=("grade {0,2} | text {1} | icon {2} | {3}（{4}）"-f$g,$resource,$iconText,$name,$proof)})
    }
    return ,$rows
}
function Read-DungeonGradeState([string]$root,[string]$nameHex){if(-not$nameHex){return -1};$path=Join-Path $root ("dungeon_grade_state_v1_{0}.dat"-f$nameHex);if(-not(Test-Path -LiteralPath $path)){return -1};$state=Read-KeyValueFile $path;[int]$g=-1;if($state.ContainsKey('version')-and[string]$state.version-eq'1'-and$state.ContainsKey('grade')-and[int]::TryParse([string]$state.grade,[ref]$g)-and$g-ge0-and$g-le42){return $g};return -1}
function Write-DungeonGradeState([string]$root,[string]$nameHex,[int]$grade){
    if($grade-lt0-or$grade-gt42){throw '称号grade必须为0..42。'};if($nameHex-notmatch '^[0-9A-F]+$'){throw '角色名编码无效。'};$base=[IO.Path]::GetFullPath($root).TrimEnd('\')+'\';$path=[IO.Path]::GetFullPath((Join-Path $root ("dungeon_grade_state_v1_{0}.dat"-f$nameHex)));if(-not$path.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)){throw '称号状态路径越界。'};$new=$path+'.new';$bak=$path+'.bak';$text="version=1`ngrade=$grade`nfrontier_valid=0`nfrontier_hd=0`nfrontier_episode=0`nfrontier_dungeon=0`nfrontier_difficulty=0`nfrontier_stage=0`n";[IO.File]::WriteAllText($new,$text,(New-Object Text.ASCIIEncoding));try{Remove-Item -LiteralPath $bak -Force -ErrorAction SilentlyContinue;if(Test-Path -LiteralPath $path){Move-Item -LiteralPath $path -Destination $bak -Force};Move-Item -LiteralPath $new -Destination $path -Force;Remove-Item -LiteralPath $bak -Force -ErrorAction SilentlyContinue}catch{Remove-Item -LiteralPath $new -Force -ErrorAction SilentlyContinue;if((Test-Path -LiteralPath $bak)-and-not(Test-Path -LiteralPath $path)){Move-Item -LiteralPath $bak -Destination $path -Force};throw};return $path
}
$PartLabels=@{body='身体/脸型（仅模型）';hair='发型';top='上衣';bottom='下衣';accessory='饰品';effect='效果（特效道具）';other='其他/非穿戴'}
$SkillDefs=@(
    [pscustomobject]@{Index=0;Code=52000000;Name='炮弹型·基础射击Ⅰ';Tree='projectile';Route=0},
    [pscustomobject]@{Index=1;Code=52000001;Name='炮弹型·基础射击Ⅱ';Tree='projectile';Route=0},
    [pscustomobject]@{Index=2;Code=52000002;Name='炮弹型·上路技能Ⅰ';Tree='projectile';Route=1},
    [pscustomobject]@{Index=3;Code=52000003;Name='炮弹型·下路技能Ⅰ';Tree='projectile';Route=2},
    [pscustomobject]@{Index=4;Code=52000004;Name='炮弹型·上路技能Ⅱ';Tree='projectile';Route=1},
    [pscustomobject]@{Index=5;Code=52000005;Name='炮弹型·下路技能Ⅱ';Tree='projectile';Route=2},
    [pscustomobject]@{Index=6;Code=52000006;Name='炮弹型·上路技能Ⅲ';Tree='projectile';Route=1},
    [pscustomobject]@{Index=7;Code=52000007;Name='炮弹型·下路技能Ⅲ';Tree='projectile';Route=2},
    [pscustomobject]@{Index=8;Code=52000008;Name='肉弹型·基础冲撞Ⅰ';Tree='meat';Route=0},
    [pscustomobject]@{Index=9;Code=52000009;Name='肉弹型·基础冲撞Ⅱ';Tree='meat';Route=0},
    [pscustomobject]@{Index=10;Code=52000010;Name='肉弹型·上路技能Ⅰ';Tree='meat';Route=1},
    [pscustomobject]@{Index=11;Code=52000011;Name='肉弹型·下路技能Ⅰ';Tree='meat';Route=2},
    [pscustomobject]@{Index=12;Code=52000012;Name='肉弹型·上路技能Ⅱ';Tree='meat';Route=1},
    [pscustomobject]@{Index=13;Code=52000013;Name='肉弹型·下路技能Ⅱ';Tree='meat';Route=2},
    [pscustomobject]@{Index=14;Code=52000014;Name='肉弹型·上路技能Ⅲ';Tree='meat';Route=1},
    [pscustomobject]@{Index=15;Code=52000015;Name='肉弹型·下路技能Ⅲ';Tree='meat';Route=2}
)
function Read-KeyValueFile([string]$path){$h=@{};if(Test-Path -LiteralPath $path){foreach($line in Get-Content -LiteralPath $path){if($line-match'^\s*([^#;=]+)=(.*)$'){$h[$matches[1].Trim()]=$matches[2].Trim()}}};return $h}
function Infer-SkillRoute($grades,[int[]]$upper,[int[]]$lower){$u=@($upper|Where-Object{[int]$grades[$_]-gt0}).Count-gt0;$l=@($lower|Where-Object{[int]$grades[$_]-gt0}).Count-gt0;if($u-and$l){return -1};if($u){return 1};if($l){return 2};return 0}


function Read-IniProfile {
    $h=@{}
    if(Test-Path -LiteralPath $ProfileIni){
        foreach($line in Get-Content -LiteralPath $ProfileIni){
            if($line -match '^\s*([^#;=]+)=(.*)$'){$h[$matches[1].Trim()]=$matches[2].Trim()}
        }
    }
    return $h
}
function Read-ProfileUInt64($profile,[string]$key,[uint64]$default){
    if(-not$profile.ContainsKey($key)){return $default}
    [uint64]$v=0
    if(-not[uint64]::TryParse([string]$profile[$key],[Globalization.NumberStyles]::None,[Globalization.CultureInfo]::InvariantCulture,[ref]$v)){throw "配置键 $key 必须是0..18446744073709551615的十进制无符号整数。"}
    return $v
}
function Read-ProfileU16($profile,[string]$key,[uint16]$default,[switch]$AllowZero){
    [uint64]$v=Read-ProfileUInt64 $profile $key $default;$min=if($AllowZero){0}else{1}
    if($v-lt$min-or$v-gt65535){throw "配置键 $key 必须在 $min..65535。"}
    return [uint16]$v
}
function Decode-NameHex([string]$hex){
    if([string]::IsNullOrWhiteSpace($hex)-or($hex.Length%2)){return 'PLAYER'}
    try{$b=New-Object byte[] ($hex.Length/2);for($i=0;$i-lt$b.Length;$i++){$b[$i]=[Convert]::ToByte($hex.Substring($i*2,2),16)};return $GbK.GetString($b)}catch{return 'PLAYER'}
}
function Encode-NameHex([string]$name){
    $b=$GbK.GetBytes($name)
    if($b.Length-lt1-or$b.Length-gt15){throw "角色显示名按GBK编码必须为1到15字节，当前为 $($b.Length) 字节。"}
    return (($b|ForEach-Object{$_.ToString('X2')})-join '')
}
function New-ChoiceList($rows,[switch]$Pet){
    $list=New-Object Collections.ArrayList
    foreach($r in $rows){
        if($Pet){$display='{0} [{1}] | {2} | 初攻 {3} | 资源:{4} | 实际:{5} | 门:{6}'-f $r.name,$r.id,$r.attack_style,$r.attack_value,$r.static_charge_text,$r.charge_text,$r.charge_gate_value; $display += ' | 自动:{0}' -f $r.auto_unlock_class}
        else{$display='{0} [{1}] | {2}'-f $r.name,$r.id,$r.gender}
        [void]$list.Add([pscustomobject]@{Display=$display;Id=[uint32]$r.id;Data=$r})
    }
    return ,$list
}
function Bind-Combo($combo,$data,[uint32]$defaultId){
    $combo.DataSource=$data;$combo.DisplayMember='Display';$combo.ValueMember='Id';$combo.DropDownStyle='DropDownList';$combo.DropDownWidth=620;$combo.MaxDropDownItems=18
    for($i=0;$i-lt$data.Count;$i++){if([uint32]$data[$i].Id-eq$defaultId){$combo.SelectedIndex=$i;return}}
    if($data.Count){$combo.SelectedIndex=0}
}
function Select-ComboId($combo,[uint32]$id){
    $data=$combo.DataSource
    for($i=0;$i-lt$data.Count;$i++){if([uint32]$data[$i].Id-eq$id){$combo.SelectedIndex=$i;return $true}}
    return $false
}
function Get-SelectedData($combo){if($combo.SelectedItem){return $combo.SelectedItem.Data};return $null}
function New-GridTable([string]$kind,$rows){
    $t=New-Object Data.DataTable;$t.TableName=$kind
    if($kind-eq'pet'){
        foreach($c in '名称','ID','攻击方式','初攻定义','自动资源','自动解锁','Auto owner','跟踪资源','静态蓄力资源','实际蓄力','蓄力门值','原生初始槽','Power序列','MP条件','证据等级','等级需求','岁数元数据','寿命','BOO') {[void]$t.Columns.Add($c)}
        foreach($r in $rows){[void]$t.Rows.Add($r.name,[string]$r.id,$r.attack_style,[string]$r.attack_value,$r.static_auto_resource,$r.auto_unlock_class,($r.auto_owners -join '/'),$r.homing_resource,$r.static_charge_text,$r.charge_text,[string]$r.charge_gate_value,[string]$r.native_initial_slot,($r.power_unlock_sequence -join '/'),$r.mp_requirement,$r.charge_confidence,[string]$r.level_requirement,("{0}/{1}"-f$r.display_age,$r.max_age),$r.lifetime_text,$r.boo_file)}
    } else {
        foreach($c in '部位','名称','ID','性别','等级或天数','效果说明','限制','模型') {[void]$t.Columns.Add($c)}
        foreach($r in $rows){[void]$t.Rows.Add($PartLabels[$r.part],$r.name,[string]$r.id,$r.gender,[string]$r.level_or_days,$r.effect_description,$r.restriction,$r.model)}
    }
    return ,$t
}
function Escape-Filter([string]$s){return $s.Replace("'","''").Replace('[','[[]').Replace('%','[%]').Replace('*','[*]')}
function Get-CatalogPreviewAtlas([string]$kind){
    if($kind-eq'pet'){$path=$PetPreviewPng;if($script:petPreviewAtlas){return $script:petPreviewAtlas}}
    elseif($kind-eq'equip'){$path=$EquipPreviewPng;if($script:equipPreviewAtlas){return $script:equipPreviewAtlas}}
    else{throw "Unknown catalog preview kind: $kind"}
    if(-not(Test-Path -LiteralPath $path)){return $null}
    $loaded=[Drawing.Image]::FromFile($path)
    try{$copy=New-Object Drawing.Bitmap $loaded}finally{$loaded.Dispose()}
    if($kind-eq'pet'){$script:petPreviewAtlas=$copy}elseif($kind-eq'equip'){$script:equipPreviewAtlas=$copy}
    return $copy
}
function New-CatalogPreviewPane($parent,[string]$caption){
    $group=New-Object Windows.Forms.GroupBox;$group.Text=$caption;$group.Dock='Fill';$parent.Controls.Add($group)
    $picture=New-Object Windows.Forms.PictureBox;$picture.Location=New-Object Drawing.Point(14,28);$picture.Size=New-Object Drawing.Size(230,230);$picture.SizeMode='Zoom';$picture.BackColor=[Drawing.Color]::FromArgb(42,42,42);$picture.BorderStyle='FixedSingle';$group.Controls.Add($picture)
    $name=New-Object Windows.Forms.Label;$name.Location=New-Object Drawing.Point(14,272);$name.Size=New-Object Drawing.Size(230,50);$name.Font=New-Object Drawing.Font('Microsoft YaHei UI',11,[Drawing.FontStyle]::Bold);$group.Controls.Add($name)
    $source=New-Object Windows.Forms.TextBox;$source.Location=New-Object Drawing.Point(14,330);$source.Size=New-Object Drawing.Size(230,145);$source.Multiline=$true;$source.ReadOnly=$true;$source.ScrollBars='Vertical';$source.BackColor=[Drawing.SystemColors]::Window;$group.Controls.Add($source)
    $note=New-Object Windows.Forms.Label;$note.Text='预览来自本地资源提取，仅用于查找，不修改游戏数据。';$note.Location=New-Object Drawing.Point(14,488);$note.Size=New-Object Drawing.Size(230,55);$note.ForeColor=[Drawing.Color]::DimGray;$group.Controls.Add($note)
    return [pscustomobject]@{Group=$group;Picture=$picture;Name=$name;Source=$source;Note=$note}
}
function Set-CatalogSplitLayout($tab,$split,$pane){
    if(-not$tab-or-not$split-or-not$pane){return}
    $left=[Math]::Max(0,$split.Left);$top=[Math]::Max(0,$split.Top)
    $width=[Math]::Max(1,$tab.ClientSize.Width-($left*2));$height=[Math]::Max(1,$tab.ClientSize.Height-$top-$left)
    if($split.Left-ne$left-or$split.Top-ne$top-or$split.Width-ne$width-or$split.Height-ne$height){$split.SetBounds($left,$top,$width,$height)}
    $required=[Math]::Max($split.Panel2MinSize,$pane.Picture.Right+$pane.Picture.Left+[Windows.Forms.SystemInformation]::VerticalScrollBarWidth)
    $preferred=[Math]::Max($required,[int][Math]::Round($width*0.25))
    $maximum=$width-$split.Panel1MinSize-$split.SplitterWidth
    if($maximum-lt1){return}
    $panel2=[Math]::Min($preferred,$maximum);$distance=$width-$panel2-$split.SplitterWidth
    if($distance-lt$split.Panel1MinSize){$distance=$split.Panel1MinSize}
    if($split.SplitterDistance-ne$distance){$split.SplitterDistance=$distance}
}
function Test-CatalogSplitLayout($tab,$split,$pane,[string]$kind){
    Set-CatalogSplitLayout $tab $split $pane
    if($split.Right-gt$tab.ClientSize.Width-or$split.Bottom-gt$tab.ClientSize.Height){throw "$kind catalog split exceeds tab bounds: split=$($split.Bounds) tab=$($tab.ClientSize)"}
    if($pane.Group.Parent-ne$split.Panel2-or$split.Panel2.Width-lt$($pane.Picture.Right+$pane.Picture.Left)){throw "$kind preview panel is clipped: panel2=$($split.Panel2.Width) pictureRight=$($pane.Picture.Right)"}
    if(-not$pane.Group.Visible-or-not$pane.Picture.Visible){throw "$kind preview controls are not visible"}
}
function Set-CatalogPreviewImage($picture,$atlas,$entry){
    if($picture.Image){$old=$picture.Image;$picture.Image=$null;$old.Dispose()}
    if(-not$atlas-or-not$entry-or-not[bool]$entry.available){return $false}
    $w=[int]$entry.w;$h=[int]$entry.h;$bmp=New-Object Drawing.Bitmap $w,$h
    $g=[Drawing.Graphics]::FromImage($bmp)
    try{$g.Clear([Drawing.Color]::Transparent);$dst=New-Object Drawing.Rectangle 0,0,$w,$h;$src=New-Object Drawing.Rectangle ([int]$entry.x),([int]$entry.y),$w,$h;$g.DrawImage($atlas,$dst,$src,[Drawing.GraphicsUnit]::Pixel)}finally{$g.Dispose()}
    $picture.Image=$bmp;return $true
}
function Update-CatalogGridPreview([string]$kind,$grid,$pane){
    if(-not$grid.CurrentRow){return}
    [uint32]$id=0;if(-not[uint32]::TryParse([string]$grid.CurrentRow.Cells['ID'].Value,[ref]$id)){return}
    if($kind-eq'pet'){$row=$petById[$id];$entry=$petPreviewById[$id];$atlas=Get-CatalogPreviewAtlas 'pet'}else{$row=$equipById[$id];$entry=$equipPreviewById[$id];$atlas=Get-CatalogPreviewAtlas 'equip'}
    $ok=Set-CatalogPreviewImage $pane.Picture $atlas $entry
    if($row){$pane.Name.Text="$($row.name)`r`n[$id]";$modelText=if($kind-eq'pet'){$row.image}else{$row.model};$pane.Source.Text="图标: $($row.icon)`r`n模型: $modelText`r`n部位: $($row.part)`r`n性别: $($row.gender)"}else{$pane.Name.Text="[$id]";$pane.Source.Text='无资源目录记录'}
    $pane.Note.Text=if($ok){'已从本地客户端资源生成预览。'}else{'暂无预览：资源缺失或提取不支持。'}
}
function Normalize-NetworkIPv4([string]$value){
    $candidate=$value.Trim();$parsed=$null
    if($candidate-notmatch '^\d{1,3}(\.\d{1,3}){3}$'-or-not[Net.IPAddress]::TryParse($candidate,[ref]$parsed)-or$parsed.AddressFamily-ne[Net.Sockets.AddressFamily]::InterNetwork-or$parsed.ToString()-ne$candidate){throw "Invalid adapter configuration."}
    return $candidate
}
function Get-LaunchModeInfo([string]$mode='network',[string]$networkIp='127.0.0.1'){
    if($mode-ne'network'-or$networkIp-ne'127.0.0.1'){throw 'Unsupported launch configuration.'}
    return [pscustomobject]@{Key='network';Display='Network (-q)';Template=$NetworkOptionTemplate;Login='Network Login Game';AdapterIP='127.0.0.1';ClientArgs=[string[]]@('-q',':1:1:0:3:4:-i','5:-r','6:7:1:127.0.0.1:');StartLocalAdapter=$true}
}
function Test-LaunchModeConfig($info,[string]$path,[string]$expectedIp=$info.AdapterIP){
    if(-not(Test-Path -LiteralPath $path)){throw "Launch configuration is missing."}
    $text=Get-Content -LiteralPath $path -Raw
    if($text-notmatch ('(?m)^ServerIP='+[regex]::Escape($expectedIp)+'\s*$')-or$text-notmatch '(?m)^Port=12050\s*$'){throw "Launch configuration failed validation."}
    $loginPattern='(?m)^Login='+[regex]::Escape([string]$info.Login)+'\s*$'
    if($text-notmatch$loginPattern){throw "Launch configuration failed validation."}
    return $text
}
function Test-LaunchModeTemplate($info){
    return Test-LaunchModeConfig $info $info.Template '127.0.0.1'
}
function Install-LaunchModeConfig($info){
    $templateText=Test-LaunchModeTemplate $info
    $activeText=$templateText-replace '(?m)^ServerIP=.*$',("ServerIP={0}"-f$info.AdapterIP)
    $targetDir=Split-Path $ActiveGameOption -Parent
    if(-not(Test-Path -LiteralPath $targetDir)){[void](New-Item -ItemType Directory -Path $targetDir -Force)}
    [IO.File]::WriteAllText($ActiveGameOption,$activeText,(New-Object Text.ASCIIEncoding))
    [void](Test-LaunchModeConfig $info $ActiveGameOption)
}

if(-not(Test-Path -LiteralPath $PetJson)-or-not(Test-Path -LiteralPath $EquipJson)){[Windows.Forms.MessageBox]::Show('资源目录不存在，请先运行 gui_launcher\generate_catalog.py。','Nanaimo 启动器')|Out-Null;exit 2}
$pets=Get-Content -LiteralPath $PetJson -Raw -Encoding UTF8|ConvertFrom-Json
$equips=Get-Content -LiteralPath $EquipJson -Raw -Encoding UTF8|ConvertFrom-Json
$attackModes=Get-Content -LiteralPath $AttackJson -Raw -Encoding UTF8|ConvertFrom-Json
$petPreviewRows=if(Test-Path -LiteralPath $PetPreviewJson){@(Get-Content -LiteralPath $PetPreviewJson -Raw -Encoding UTF8|ConvertFrom-Json)}else{@()}
$equipPreviewRows=if(Test-Path -LiteralPath $EquipPreviewJson){@(Get-Content -LiteralPath $EquipPreviewJson -Raw -Encoding UTF8|ConvertFrom-Json)}else{@()}
$petPreviewById=@{};foreach($x in $petPreviewRows){$petPreviewById[[uint32]$x.id]=$x}
$equipPreviewById=@{};foreach($x in $equipPreviewRows){$equipPreviewById[[uint32]$x.id]=$x}
$script:petPreviewAtlas=$null;$script:equipPreviewAtlas=$null
$attackByPet=@{};foreach($x in $attackModes){$attackByPet[[uint32]$x.pet_id]=$x}
$petById=@{};foreach($x in $pets){$petById[[uint32]$x.id]=$x}
$equipById=@{};foreach($x in $equips){$equipById[[uint32]$x.id]=$x}
$ini=Read-IniProfile
# Ignore legacy saved mode/address: GUI always uses the loopback network protocol.
$defaultLaunchMode='network'
$defaultNetworkIp='127.0.0.1'
$defaultName=if($ini.name_hex){Decode-NameHex $ini.name_hex}else{'Greyrat'}
$currentDungeonGrade=if($ini.name_hex){Read-DungeonGradeState $ProfileStateRoot ([string]$ini.name_hex)}else{-1}
$defaultDungeonGrade=-1;if($ini.ContainsKey('dungeon_grade')){[int]$parsedGrade=-1;if([int]::TryParse([string]$ini.dungeon_grade,[ref]$parsedGrade)-and$parsedGrade-ge0-and$parsedGrade-le42){$defaultDungeonGrade=$parsedGrade}}
$titleChoices=New-DungeonTitleChoices $currentDungeonGrade
$defaultLevel=if($ini.level){[int]$ini.level}else{25}
$defaultPetAge=if($ini.pet_age_a){[int]$ini.pet_age_a}else{3}
$defaultAttackMode=if($ini.initial_attack_mode-ne$null){[int]$ini.initial_attack_mode}else{-1};$script:firstAttackModeLoad=$true
$defaultHpMax=Read-ProfileU16 $ini 'hp_max' 1500
$defaultHpCurrent=if($ini.ContainsKey('hp_current')){Read-ProfileU16 $ini 'hp_current' 1500 -AllowZero}else{$defaultHpMax}
$defaultMpMax=Read-ProfileU16 $ini 'mp_max' 500
$defaultMpCurrent=if($ini.ContainsKey('mp_current')){Read-ProfileU16 $ini 'mp_current' 500 -AllowZero}else{$defaultMpMax}
$defaultAttack=Read-ProfileUInt64 $ini 'attack' 0;if($defaultAttack-gt1000000){throw 'attack must be 0..1000000.'};$defaultAttack=[uint32]$defaultAttack
$defaultDefense=Read-ProfileUInt64 $ini 'defense' 0;if($defaultDefense-gt65535){throw 'defense must be 0..65535.'};$defaultDefense=[uint16]$defaultDefense
$defaultCoin=Read-ProfileUInt64 $ini 'coin' 0
$defaultNanaPoint=Read-ProfileUInt64 $ini 'nana_point' 0
$defaultCardKeyNormal=[uint16](Read-ProfileU16 $ini 'card_key_normal' 99 -AllowZero);if($defaultCardKeyNormal-gt255){throw 'card_key_normal must be 0..255.'}
$defaultCardKeyGold=[uint16](Read-ProfileU16 $ini 'card_key_gold' 99 -AllowZero);if($defaultCardKeyGold-gt255){throw 'card_key_gold must be 0..255.'}
$defaultCardKeyMystery=[uint16](Read-ProfileU16 $ini 'card_key_mystery' 99 -AllowZero);if($defaultCardKeyMystery-gt255){throw 'card_key_mystery must be 0..255.'}
$defaultCardKeySpecial=[uint16](Read-ProfileU16 $ini 'card_key_special' 99 -AllowZero);if($defaultCardKeySpecial-gt255){throw 'card_key_special must be 0..255.'}
$defaultFreeMagicKeyExpiry=Read-ProfileUInt64 $ini 'free_magic_key_expiry' 2099123123;if($defaultFreeMagicKeyExpiry-lt2000010100-or$defaultFreeMagicKeyExpiry-gt2100123123){throw 'free_magic_key_expiry must be YYYYMMDDHH in 2000010100..2100123123.'}
$defaultQuickbarExpiry=Read-ProfileUInt64 $ini 'quickbar_expiry' 2099123123;if($defaultQuickbarExpiry-ne0-and($defaultQuickbarExpiry-lt2000010100-or$defaultQuickbarExpiry-gt2100123123)){throw 'quickbar_expiry must be 0 or YYYYMMDDHH in 2000010100..2100123123.'}
$defaultSkipTutorial=$ini.ContainsKey('skip_tutorial')-and([string]$ini.skip_tutorial-eq'1')
$skillProfile=$ini
if(-not($ini.ContainsKey('skill_config')-and[string]$ini.skill_config-eq'1')-and$ini.name_hex){$skillPath=Join-Path $Root ("skill_progress_state_v2_{0}.dat"-f([string]$ini.name_hex));if(Test-Path -LiteralPath $skillPath){$skillProfile=Read-KeyValueFile $skillPath}}
$defaultSkillGrades=New-Object int[] 16;$haveSkillGrades=$false
for($i=0;$i-lt16;$i++){$k="skill_grade$i";if(-not$skillProfile.ContainsKey($k)){$k="grade$i"};if($skillProfile.ContainsKey($k)){[int]$v=0;if(-not[int]::TryParse([string]$skillProfile[$k],[ref]$v)-or$v-lt0-or$v-gt5){throw "技能等级 $k 必须为0..5。"};$defaultSkillGrades[$i]=$v;$haveSkillGrades=$true}}
if(-not$haveSkillGrades){$defaultSkillGrades[0]=$defaultSkillGrades[1]=$defaultSkillGrades[8]=$defaultSkillGrades[9]=5}
$defaultProjectileRoute=if($ini.ContainsKey('skill_projectile_route')){[int]$ini.skill_projectile_route}else{Infer-SkillRoute $defaultSkillGrades ([int[]](2,4,6)) ([int[]](3,5,7))}
$defaultMeatRoute=if($ini.ContainsKey('skill_meat_route')){[int]$ini.skill_meat_route}else{Infer-SkillRoute $defaultSkillGrades ([int[]](10,12,14)) ([int[]](11,13,15))}
$defaultSkillZ=if($ini.ContainsKey('skill_slot_z')){[uint32]$ini.skill_slot_z}elseif($skillProfile.ContainsKey('slot0')){[uint32]$skillProfile.slot0}else{[uint32]0}
$defaultSkillX=if($ini.ContainsKey('skill_slot_x')){[uint32]$ini.skill_slot_x}elseif($skillProfile.ContainsKey('slot1')){[uint32]$skillProfile.slot1}else{[uint32]0}
if($defaultHpCurrent-gt$defaultHpMax){throw 'hp_current不能大于hp_max'};if($defaultMpCurrent-gt$defaultMpMax){throw 'mp_current不能大于mp_max'}
$defaults=@{pet=15009205;hair=10130337;body=10100028;top=10110337;bottom=10120352;accessory=10150103;effect=10160017}
foreach($k in @($defaults.Keys)){if($ini.ContainsKey($(if($k-eq'pet'){'pet'}else{"equip_$k"}))){$defaults[$k]=[uint32]$ini[$(if($k-eq'pet'){'pet'}else{"equip_$k"})]}}
$defaultBody=$equipById[[uint32]$defaults.body];$defaultGender=if($ini.gender){[int]$ini.gender}elseif($defaultBody-and$defaultBody.gender-eq'M'){1}else{0}
if($SelfTestTitleIO){
    $tmp=Join-Path ([IO.Path]::GetTempPath()) ('nanaimo_title_'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Force -Path $tmp|Out-Null
    try{if($titleChoices.Count-ne44){throw 'title choice count'};$r1=@($titleChoices|Where-Object Grade -eq 17)[0];$r7=@($titleChoices|Where-Object Grade -eq 23)[0];$r23=@($titleChoices|Where-Object Grade -eq 39)[0];$shared=@($titleChoices|Where-Object Grade -eq 42)[0];if($r1.Rank-ne'R1'-or$r7.Rank-ne'R7'-or$r7.IconResource-ne2396-or$r23.Rank-ne'R23'-or$r23.ResourceId-ne1282-or$r23.IconResource-ne2412-or$shared.Rank-ne'R23共享'-or$shared.ResourceId-ne1285-or$shared.IconResource-ne2412){throw 'rank calibration mapping'};$path=Write-DungeonGradeState $tmp '5449544C4554455354' 42;$state=Read-KeyValueFile $path;if([int]$state.grade-ne42-or[int]$state.frontier_valid-ne0-or(Read-DungeonGradeState $tmp '5449544C4554455354')-ne42){throw 'grade42 state roundtrip'};Write-Output 'NETWORK_TITLE_IO_PASS choices=44 fixed_grades=43 grade_range=0..42 R1_grade=17 R7_grade=23 R23_grade=39 shared_R23_grades=40..42 text_resources=1243..1285 state_roundtrip=PASS'}finally{if(Test-Path -LiteralPath $tmp){[IO.Directory]::Delete($tmp,$true)}};exit 0
}
if($SelfTestInventoryIO){Write-Output (Test-InventoryAdminInstallation $Root);exit 0}
if($ValidateOnly){
    $inventoryValidation=Test-InventoryAdminInstallation $Root
    if($pets.Count-ne868){throw 'pet catalog count'};if($equips.Count-ne3885){throw 'equipment catalog count'};if($attackModes.Count-ne868){throw 'attack mode catalog count'};if($petPreviewRows.Count-ne868-or$equipPreviewRows.Count-ne3885-or-not(Test-Path -LiteralPath $PetPreviewPng)-or-not(Test-Path -LiteralPath $EquipPreviewPng)){throw 'catalog preview assets'}
    foreach($path in @($AdapterManifest,$Adapter,$AdapterBridge)){if(-not(Test-Path -LiteralPath $path)){throw "adapter artifact missing: $path"}};if((Get-Item -LiteralPath $Adapter).Length-ne$ExpectedAdapterSize-or(Get-FileHash -Algorithm SHA256 -LiteralPath $Adapter).Hash-ne$ExpectedAdapterHash){throw 'adapter validation'};if((Get-Item -LiteralPath $AdapterBridge).Length-ne$ExpectedBridgeSize-or(Get-FileHash -Algorithm SHA256 -LiteralPath $AdapterBridge).Hash-ne$ExpectedBridgeHash){throw 'bridge validation'}
    foreach($id in 15009205,10130337,10100028,10110337,10120352,10150103,10160017){if(-not($petById.ContainsKey([uint32]$id)-or$equipById.ContainsKey([uint32]$id))){throw "missing default id $id"}}
    $round=Decode-NameHex (Encode-NameHex '测试角色');if($round-ne'测试角色'){throw 'GBK name roundtrip'}
    $pt=New-GridTable 'pet' $pets;$et=New-GridTable 'equip' $equips;if($pt.Rows.Count-ne868-or$et.Rows.Count-ne3885){throw 'grid table count'}
    $pv=New-Object Data.DataView;$pv.Table=$pt;$pv.RowFilter="[ID] LIKE '%15009205%'";if($pv.Count-ne1){throw 'pet filter'}
    $ev=New-Object Data.DataView;$ev.Table=$et;$ev.RowFilter="[部位] = '发型'";if($ev.Count-ne1049){throw 'equipment hair filter'};$ev.RowFilter="[部位] = '效果（特效道具）'";if($ev.Count-ne438){throw 'equipment effect filter'}
    if(@($equips|Where-Object{$_.part-eq'other'}).Count-ne2){throw 'equipment other classification'}
    $networkInfo=Get-LaunchModeInfo 'network' '127.0.0.1'
    [void](Test-LaunchModeTemplate $networkInfo)
    if(($networkInfo.ClientArgs-join' ')-ne'-q :1:1:0:3:4:-i 5:-r 6:7:1:127.0.0.1:'){throw 'network client arguments'}
    if($defaultCardKeyNormal-ne99-or$defaultCardKeyGold-ne99-or$defaultCardKeyMystery-ne99-or$defaultCardKeySpecial-ne99-or$defaultFreeMagicKeyExpiry-ne2099123123){throw 'card key defaults'};if($titleChoices.Count-ne44-or@($titleChoices|Where-Object Grade -eq 23)[0].Rank-ne'R7'-or@($titleChoices|Where-Object Grade -eq 39)[0].Rank-ne'R23'-or@($titleChoices|Where-Object Grade -eq 42)[0].Rank-ne'R23共享'){throw 'title choices'}
    Write-Host ('NETWORK_VALIDATE_PASS titles=43+auto pets={0} equipment={1} profile={2}/{3} hp={4}/{5} mp={6}/{7} attack={8} defense={9} coin={10} nana_point={11} skip_tutorial={12} launch_mode={13} network_ip={14} templates=PASS filters=PASS inventory_admin=PASS'-f$pets.Count,$equips.Count,$defaultName,$defaultLevel,$defaultHpCurrent,$defaultHpMax,$defaultMpCurrent,$defaultMpMax,$defaultAttack,$defaultDefense,$defaultCoin,$defaultNanaPoint,[int]$defaultSkipTutorial,$defaultLaunchMode,$defaultNetworkIp);exit 0
}
if($SelfTestLaunchModes){
    $savedActive=$ActiveGameOption;$tempActive=Join-Path ([IO.Path]::GetTempPath()) ('nanaimo_gamestartoption_'+[guid]::NewGuid().ToString('N')+'.ini')
    try{
        $ActiveGameOption=$tempActive
        $info=Get-LaunchModeInfo;Install-LaunchModeConfig $info
        $text=Test-LaunchModeConfig $info $ActiveGameOption
        if(-not$info.StartLocalAdapter-or($info.ClientArgs-join' ')-ne'-q :1:1:0:3:4:-i 5:-r 6:7:1:127.0.0.1:'){throw 'loopback network self-test'}
        foreach($bad in @(@('standalone','127.0.0.1'),@('network','203.0.113.10'))){
            $rejected=$false;try{[void](Get-LaunchModeInfo $bad[0] $bad[1])}catch{$rejected=$true}
            if(-not$rejected){throw 'unsupported launch mode/address accepted'}
        }
        Write-Output 'GUI_LAUNCH_MODE_SELFTEST_PASS network_ip=127.0.0.1 local_adapter=true unsupported_modes_rejected=true'
    }finally{$ActiveGameOption=$savedActive;Remove-Item -LiteralPath $tempActive -Force -ErrorAction SilentlyContinue}
    exit 0
}

$form=New-Object Windows.Forms.Form
$form.Text='Nanaimo Korean Flight Shooter Launcher';$form.Size=New-Object Drawing.Size(1120,940);$form.StartPosition='CenterScreen';$form.MinimumSize=New-Object Drawing.Size(1000,870)
$tabs=New-Object Windows.Forms.TabControl;$tabs.Dock='Fill';$form.Controls.Add($tabs)
$tabStart=New-Object Windows.Forms.TabPage;$tabStart.Text='启动配置';$tabs.TabPages.Add($tabStart)
$tabLaunchInfo=New-Object Windows.Forms.TabPage;$tabLaunchInfo.Text='本次启动详情';$tabs.TabPages.Add($tabLaunchInfo)
$tabResources=New-Object Windows.Forms.TabPage;$tabResources.Text='数值与道具';$tabs.TabPages.Add($tabResources)
$tabPets=New-Object Windows.Forms.TabPage;$tabPets.Text='宠物查表';$tabs.TabPages.Add($tabPets)
$tabEquip=New-Object Windows.Forms.TabPage;$tabEquip.Text='装扮查表';$tabs.TabPages.Add($tabEquip)

$title=New-Object Windows.Forms.Label;$title.Text='韩国飞行射击游戏 Nanaimo 启动器';$title.Font=New-Object Drawing.Font('Microsoft YaHei UI',16,[Drawing.FontStyle]::Bold);$title.AutoSize=$true;$title.Location=New-Object Drawing.Point(28,24);$tabStart.Controls.Add($title)
$hint=New-Object Windows.Forms.Label;$hint.Text='等级现由通关结算推进：每次成功 CF88 结算 +100 EXP，下一级需要当前等级×100；此处等级仅在该角色没有进度档时作为初始种子。';$hint.AutoSize=$true;$hint.Location=New-Object Drawing.Point(30,62);$tabStart.Controls.Add($hint)

function Add-Label($parent,$text,$x,$y,$w=150){$l=New-Object Windows.Forms.Label;$l.Text=$text;$l.Location=New-Object Drawing.Point($x,$y);$l.Size=New-Object Drawing.Size($w,25);$parent.Controls.Add($l);return $l}


$resourceTitle=New-Object Windows.Forms.Label;$resourceTitle.Text='人物数值、账户货币与卡册道具';$resourceTitle.Font=New-Object Drawing.Font('Microsoft YaHei UI',15,[Drawing.FontStyle]::Bold);$resourceTitle.AutoSize=$true;$resourceTitle.Location=New-Object Drawing.Point(28,24);$tabResources.Controls.Add($resourceTitle)
$resourceHint=New-Object Windows.Forms.Label;$resourceHint.Text='左侧含HP/MP、攻击加算、防御平减与货币；右侧为C3E8卡册钥匙。攻击写CFEC原生槽并由协议适配器镜像；防御使用本地平减规则。';$resourceHint.AutoSize=$true;$resourceHint.Location=New-Object Drawing.Point(30,62);$tabResources.Controls.Add($resourceHint)
function New-ResourceNumeric($parent,[string]$label,[int]$x,[int]$y,[decimal]$min,[decimal]$max,[decimal]$value,[int]$width=260){
    Add-Label $parent $label $x $y 190|Out-Null;$n=New-Object Windows.Forms.NumericUpDown;$n.Location=New-Object Drawing.Point(($x+205),($y-4));$n.Size=New-Object Drawing.Size($width,30);$n.Minimum=$min;$n.Maximum=$max;$n.DecimalPlaces=0;$n.ThousandsSeparator=$true;$n.Value=$value;$parent.Controls.Add($n);return $n
}
$u64Max=[decimal]::Parse('18446744073709551615',[Globalization.CultureInfo]::InvariantCulture)
$hpMaxBox=New-ResourceNumeric $tabResources '最大HP  hp_max' 45 125 1 65535 ([decimal]$defaultHpMax)
$hpCurrentBox=New-ResourceNumeric $tabResources '当前HP  hp_current' 45 175 0 65535 ([decimal]$defaultHpCurrent)
$mpMaxBox=New-ResourceNumeric $tabResources '最大MP  mp_max' 45 225 1 65535 ([decimal]$defaultMpMax)
$mpCurrentBox=New-ResourceNumeric $tabResources '当前MP  mp_current' 45 275 0 65535 ([decimal]$defaultMpCurrent)
$attackBox=New-ResourceNumeric $tabResources '攻击加算  attack（CFEC）' 45 325 0 1000000 ([decimal]$defaultAttack)
$defenseBox=New-ResourceNumeric $tabResources '防御平减  defense（本地）' 45 375 0 65535 ([decimal]$defaultDefense)
$coinBox=New-ResourceNumeric $tabResources '金币  coin' 45 455 0 $u64Max ([decimal]$defaultCoin) 260
$nanaPointBox=New-ResourceNumeric $tabResources 'NANA点  nana_point' 45 505 0 $u64Max ([decimal]$defaultNanaPoint) 260
$cardKeyNormalBox=New-ResourceNumeric $tabResources '一般魔法钥匙  card_key_normal' 555 125 0 255 ([decimal]$defaultCardKeyNormal) 210
$cardKeyGoldBox=New-ResourceNumeric $tabResources '黄金魔法钥匙  card_key_gold' 555 175 0 255 ([decimal]$defaultCardKeyGold) 210
$cardKeyMysteryBox=New-ResourceNumeric $tabResources '神秘钥匙  card_key_mystery' 555 225 0 255 ([decimal]$defaultCardKeyMystery) 210
$cardKeySpecialBox=New-ResourceNumeric $tabResources '其他钥匙槽  card_key_special' 555 275 0 255 ([decimal]$defaultCardKeySpecial) 210
$freeMagicKeyExpiryBox=New-ResourceNumeric $tabResources '自由魔法钥匙到期  YYYYMMDDHH' 555 355 2000010100 2100123123 ([decimal]$defaultFreeMagicKeyExpiry) 210
$quickbarExpiryBox=New-ResourceNumeric $tabResources '快捷栏扩容券到期（0=未启用）' 555 455 0 2100123123 ([decimal]$defaultQuickbarExpiry) 210
$freeMagicKeyNote=New-Object Windows.Forms.Label;$freeMagicKeyNote.Text='自由钥匙物品：44000010/44000011；C3E8 +0x88启用；真实背包链 C473(mode1)→C474。';$freeMagicKeyNote.Location=New-Object Drawing.Point(555,405);$freeMagicKeyNote.Size=New-Object Drawing.Size(500,45);$freeMagicKeyNote.ForeColor=[Drawing.Color]::DarkGreen;$tabResources.Controls.Add($freeMagicKeyNote)
$resourceBoundary=New-Object Windows.Forms.Label;$resourceBoundary.Text='Attack is a u32 additive CFEC modifier (0..1000000). Defense uses the local rule max(1, raw-defense) before D010/D015; this is not a recovered original formula. HP/MP use u16, currencies use u64, and card counts use u8. Selected-PET type1 gems are not automatically added again.';$resourceBoundary.Location=New-Object Drawing.Point(45,570);$resourceBoundary.Size=New-Object Drawing.Size(980,72);$resourceBoundary.ForeColor=[Drawing.Color]::DarkOrange;$tabResources.Controls.Add($resourceBoundary)
$tabResources.AutoScroll=$true;$tabResources.AutoScrollMinSize=New-Object Drawing.Size(1080,1020)
$skillWarning=New-Object Windows.Forms.Label;$skillWarning.Location=New-Object Drawing.Point(45,650);$skillWarning.Size=New-Object Drawing.Size(1000,34);$skillWarning.Font=New-Object Drawing.Font('Microsoft YaHei UI',9,[Drawing.FontStyle]::Bold);$tabResources.Controls.Add($skillWarning)
function New-SkillGradeControl($parent,[int]$idx,[int]$x,[int]$y){$d=$SkillDefs[$idx];Add-Label $parent ("{0} [{1}]"-f$d.Name,$d.Code) $x $y 185|Out-Null;$n=New-Object Windows.Forms.NumericUpDown;$n.Location=New-Object Drawing.Point(($x+188),($y-3));$n.Size=New-Object Drawing.Size(46,25);$n.Minimum=0;$n.Maximum=5;$n.Value=[decimal]$defaultSkillGrades[$idx];$n.Tag=$idx;$parent.Controls.Add($n);return $n}
$skillGradeBoxes=New-Object object[] 16
$projectileSkillGroup=New-Object Windows.Forms.GroupBox;$projectileSkillGroup.Text='炮弹型技能树';$projectileSkillGroup.Location=New-Object Drawing.Point(25,700);$projectileSkillGroup.Size=New-Object Drawing.Size(510,190);$tabResources.Controls.Add($projectileSkillGroup)
$meatSkillGroup=New-Object Windows.Forms.GroupBox;$meatSkillGroup.Text='肉弹型技能树';$meatSkillGroup.Location=New-Object Drawing.Point(550,700);$meatSkillGroup.Size=New-Object Drawing.Size(510,190);$tabResources.Controls.Add($meatSkillGroup)
Add-Label $projectileSkillGroup '分支路线' 10 22 75|Out-Null;$projectileRouteCombo=New-Object Windows.Forms.ComboBox;$projectileRouteCombo.Location=New-Object Drawing.Point(88,18);$projectileRouteCombo.Size=New-Object Drawing.Size(220,26);$projectileRouteCombo.DropDownStyle='DropDownList';foreach($x in @('未选分支（分支等级全0）','上路线（只允许上路）','下路线（只允许下路）')){[void]$projectileRouteCombo.Items.Add($x)};$projectileRouteCombo.SelectedIndex=if($defaultProjectileRoute-ge0-and$defaultProjectileRoute-le2){$defaultProjectileRoute}else{0};$projectileSkillGroup.Controls.Add($projectileRouteCombo)
Add-Label $meatSkillGroup '分支路线' 10 22 75|Out-Null;$meatRouteCombo=New-Object Windows.Forms.ComboBox;$meatRouteCombo.Location=New-Object Drawing.Point(88,18);$meatRouteCombo.Size=New-Object Drawing.Size(220,26);$meatRouteCombo.DropDownStyle='DropDownList';foreach($x in @('未选分支（分支等级全0）','上路线（只允许上路）','下路线（只允许下路）')){[void]$meatRouteCombo.Items.Add($x)};$meatRouteCombo.SelectedIndex=if($defaultMeatRoute-ge0-and$defaultMeatRoute-le2){$defaultMeatRoute}else{0};$meatSkillGroup.Controls.Add($meatRouteCombo)
$skillGradeBoxes[0]=New-SkillGradeControl $projectileSkillGroup 0 10 55;$skillGradeBoxes[1]=New-SkillGradeControl $projectileSkillGroup 1 260 55
$skillGradeBoxes[2]=New-SkillGradeControl $projectileSkillGroup 2 10 88;$skillGradeBoxes[3]=New-SkillGradeControl $projectileSkillGroup 3 260 88
$skillGradeBoxes[4]=New-SkillGradeControl $projectileSkillGroup 4 10 119;$skillGradeBoxes[5]=New-SkillGradeControl $projectileSkillGroup 5 260 119
$skillGradeBoxes[6]=New-SkillGradeControl $projectileSkillGroup 6 10 150;$skillGradeBoxes[7]=New-SkillGradeControl $projectileSkillGroup 7 260 150
$skillGradeBoxes[8]=New-SkillGradeControl $meatSkillGroup 8 10 55;$skillGradeBoxes[9]=New-SkillGradeControl $meatSkillGroup 9 260 55
$skillGradeBoxes[10]=New-SkillGradeControl $meatSkillGroup 10 10 88;$skillGradeBoxes[11]=New-SkillGradeControl $meatSkillGroup 11 260 88
$skillGradeBoxes[12]=New-SkillGradeControl $meatSkillGroup 12 10 119;$skillGradeBoxes[13]=New-SkillGradeControl $meatSkillGroup 13 260 119
$skillGradeBoxes[14]=New-SkillGradeControl $meatSkillGroup 14 10 150;$skillGradeBoxes[15]=New-SkillGradeControl $meatSkillGroup 15 260 150
Add-Label $tabResources 'Z 实际装备技能' 45 915 135|Out-Null;$skillZCombo=New-Object Windows.Forms.ComboBox;$skillZCombo.Location=New-Object Drawing.Point(190,911);$skillZCombo.Size=New-Object Drawing.Size(350,28);$skillZCombo.DropDownStyle='DropDownList';$tabResources.Controls.Add($skillZCombo)
Add-Label $tabResources 'X 实际装备技能' 550 915 135|Out-Null;$skillXCombo=New-Object Windows.Forms.ComboBox;$skillXCombo.Location=New-Object Drawing.Point(695,911);$skillXCombo.Size=New-Object Drawing.Size(350,28);$skillXCombo.DropDownStyle='DropDownList';$tabResources.Controls.Add($skillXCombo)
$skillRouteNote=New-Object Windows.Forms.Label;$skillRouteNote.Text='互斥规则：炮弹型和肉弹型各自只能选择上/下其中一条路线。切换路线会把另一条路线的3项等级清零；Z/X只能装备等级>0的不同技能。';$skillRouteNote.Location=New-Object Drawing.Point(45,960);$skillRouteNote.Size=New-Object Drawing.Size(990,38);$skillRouteNote.ForeColor=[Drawing.Color]::DarkRed;$tabResources.Controls.Add($skillRouteNote)
function Read-SkillGradesFromControls{$g=New-Object int[] 16;for($i=0;$i-lt16;$i++){$g[$i]=[int]$skillGradeBoxes[$i].Value};return ,$g}
function Skill-CodeName([uint32]$code){if(-not$code){return '未装备'};$d=$SkillDefs|Where-Object{[uint32]$_.Code-eq$code}|Select-Object -First 1;if($d){return $d.Name};return "未知技能 $code"}
function Get-SkillComboCode($combo){if($combo.SelectedIndex-ge0-and$combo.Tag-and$combo.SelectedIndex-lt$combo.Tag.Count){return [uint32]$combo.Tag[$combo.SelectedIndex]};return [uint32]0}
function Set-SkillSlotChoices($wantZ,$wantX){if($script:skillSlotRefreshing){return};$script:skillSlotRefreshing=$true;try{$g=Read-SkillGradesFromControls;$z=if($null-ne$wantZ){[uint32]$wantZ}else{Get-SkillComboCode $skillZCombo};$x=if($null-ne$wantX){[uint32]$wantX}else{Get-SkillComboCode $skillXCombo};$codes=New-Object Collections.ArrayList;$texts=New-Object Collections.ArrayList;[void]$codes.Add([uint32]0);[void]$texts.Add('未装备 [0]');foreach($d in $SkillDefs){if($g[[int]$d.Index]-gt0){$route=if([int]$d.Route-eq1){'上路'}elseif([int]$d.Route-eq2){'下路'}else{'基础'};[void]$codes.Add([uint32]$d.Code);[void]$texts.Add(("{0} | {1} | 等级{2} [{3}]"-f$d.Name,$route,$g[[int]$d.Index],$d.Code))}};$skillZCombo.BeginUpdate();$skillXCombo.BeginUpdate();$skillZCombo.Items.Clear();$skillXCombo.Items.Clear();foreach($text in $texts){[void]$skillZCombo.Items.Add($text);[void]$skillXCombo.Items.Add($text)};$skillZCombo.Tag=@($codes);$skillXCombo.Tag=@($codes);$skillZCombo.EndUpdate();$skillXCombo.EndUpdate();$zi=[Array]::IndexOf([object[]]@($codes),[object][uint32]$z);$xi=[Array]::IndexOf([object[]]@($codes),[object][uint32]$x);$skillZCombo.SelectedIndex=if($zi-ge0){$zi}else{0};$skillXCombo.SelectedIndex=if($xi-ge0){$xi}else{0}}finally{$script:skillSlotRefreshing=$false}}
function Set-SkillRouteState([string]$tree,[switch]$ClearInactive){if($tree-eq'projectile'){$route=$projectileRouteCombo.SelectedIndex;$upper=[int[]](2,4,6);$lower=[int[]](3,5,7)}else{$route=$meatRouteCombo.SelectedIndex;$upper=[int[]](10,12,14);$lower=[int[]](11,13,15)};foreach($i in $upper){$skillGradeBoxes[$i].Enabled=($route-eq1);if($ClearInactive-and$route-ne1){$skillGradeBoxes[$i].Value=0}};foreach($i in $lower){$skillGradeBoxes[$i].Enabled=($route-eq2);if($ClearInactive-and$route-ne2){$skillGradeBoxes[$i].Value=0}}}
function Get-SkillSelection{$g=Read-SkillGradesFromControls;$pr=[int]$projectileRouteCombo.SelectedIndex;$mr=[int]$meatRouteCombo.SelectedIndex;$pu=@(2,4,6|Where-Object{$g[$_]-gt0}).Count-gt0;$pl=@(3,5,7|Where-Object{$g[$_]-gt0}).Count-gt0;$mu=@(10,12,14|Where-Object{$g[$_]-gt0}).Count-gt0;$ml=@(11,13,15|Where-Object{$g[$_]-gt0}).Count-gt0;if($pu-and$pl){throw '炮弹型上、下路线发生冲突，只能保留一条。'};if($mu-and$ml){throw '肉弹型上、下路线发生冲突，只能保留一条。'};if(($pr-eq1-and$pl)-or($pr-eq2-and$pu)-or($pr-eq0-and($pu-or$pl))){throw '炮弹型路线选择与分支加点不一致。'};if(($mr-eq1-and$ml)-or($mr-eq2-and$mu)-or($mr-eq0-and($mu-or$ml))){throw '肉弹型路线选择与分支加点不一致。'};$z=Get-SkillComboCode $skillZCombo;$x=Get-SkillComboCode $skillXCombo;foreach($code in @($z,$x)){if($code){$idx=[int]($code-52000000);if($idx-lt0-or$idx-ge16-or$g[$idx]-le0){throw "Z/X选择了未加点技能 $code。"}}};if($z-and$z-eq$x){throw 'Z和X不能装备同一个技能。'};return [pscustomobject]@{projectile_route=$pr;meat_route=$mr;grades=$g;slot_z=$z;slot_x=$x}}
function Update-SkillWarning{try{$s=Get-SkillSelection;$skillWarning.ForeColor=[Drawing.Color]::DarkGreen;$skillWarning.Text=("技能配置有效：炮弹={0}，肉弹={1}，Z={2}，X={3}"-f@('未选','上路','下路')[$s.projectile_route],@('未选','上路','下路')[$s.meat_route],(Skill-CodeName $s.slot_z),(Skill-CodeName $s.slot_x))}catch{$skillWarning.ForeColor=[Drawing.Color]::Red;$skillWarning.Text='技能配置冲突：'+$_.Exception.Message}}
Set-SkillRouteState projectile;Set-SkillRouteState meat;Set-SkillSlotChoices $defaultSkillZ $defaultSkillX;Update-SkillWarning
$projectileRouteCombo.add_SelectedIndexChanged({Set-SkillRouteState projectile -ClearInactive;Set-SkillSlotChoices $null $null;Update-SkillWarning});$meatRouteCombo.add_SelectedIndexChanged({Set-SkillRouteState meat -ClearInactive;Set-SkillSlotChoices $null $null;Update-SkillWarning})
foreach($b in $skillGradeBoxes){$b.add_ValueChanged({Set-SkillSlotChoices $null $null;Update-SkillWarning})};$skillZCombo.add_SelectedIndexChanged({if(-not$script:skillSlotRefreshing){Update-SkillWarning}});$skillXCombo.add_SelectedIndexChanged({if(-not$script:skillSlotRefreshing){Update-SkillWarning}})


Add-Label $tabStart '用户名/角色显示名' 35 108|Out-Null
$nameBox=New-Object Windows.Forms.TextBox;$nameBox.Location=New-Object Drawing.Point(190,104);$nameBox.Size=New-Object Drawing.Size(320,28);$nameBox.Text=$defaultName;$tabStart.Controls.Add($nameBox)
$skipTutorialBox=New-Object Windows.Forms.CheckBox;$skipTutorialBox.Text='跳过新手教程';$skipTutorialBox.Location=New-Object Drawing.Point(535,104);$skipTutorialBox.Size=New-Object Drawing.Size(300,28);$skipTutorialBox.Checked=$defaultSkipTutorial;$tabStart.Controls.Add($skipTutorialBox)
function Get-SelectedLaunchMode {return 'network'}
function Get-NetworkIpInput {return '127.0.0.1'}
function Get-SelectedLaunchModeInfo {return Get-LaunchModeInfo}
Add-Label $tabStart '初始等级（无进度档）' 35 148 155|Out-Null
$levelBox=New-Object Windows.Forms.NumericUpDown;$levelBox.Location=New-Object Drawing.Point(190,144);$levelBox.Minimum=1;$levelBox.Maximum=99;$levelBox.Value=[Math]::Min(99,[Math]::Max(1,$defaultLevel));$levelBox.Size=New-Object Drawing.Size(120,28);$tabStart.Controls.Add($levelBox)
$resetProgressBtn=New-Object Windows.Forms.Button;$resetProgressBtn.Text='Reset level/EXP/title';$resetProgressBtn.Location=New-Object Drawing.Point(900,190);$resetProgressBtn.Size=New-Object Drawing.Size(170,30);$tabStart.Controls.Add($resetProgressBtn)
Add-Label $tabStart '性别（模型基础）' 340 148 130|Out-Null
$genderCombo=New-Object Windows.Forms.ComboBox;$genderCombo.Location=New-Object Drawing.Point(475,144);$genderCombo.Size=New-Object Drawing.Size(170,30);$genderCombo.DropDownStyle='DropDownList';[void]$genderCombo.Items.Add('女（F资源）');[void]$genderCombo.Items.Add('男（M资源）');$genderCombo.SelectedIndex=[Math]::Min(1,[Math]::Max(0,$defaultGender));$tabStart.Controls.Add($genderCombo)
Add-Label $tabStart '地宫初始攻击（0～2）' 665 148 120|Out-Null
$attackCombo=New-Object Windows.Forms.ComboBox;$attackCombo.Enabled=$true;$attackCombo.Location=New-Object Drawing.Point(790,144);$attackCombo.Size=New-Object Drawing.Size(285,30);$attackCombo.DropDownStyle='DropDownList';$attackCombo.DropDownWidth=760;$tabStart.Controls.Add($attackCombo)

Add-Label $tabStart '称号选择（grade 0～42）' 35 194 155|Out-Null
$titleCombo=New-Object Windows.Forms.ComboBox;$titleCombo.Location=New-Object Drawing.Point(190,190);$titleCombo.Size=New-Object Drawing.Size(700,30);$titleCombo.DropDownStyle='DropDownList';$titleCombo.DropDownWidth=930
foreach($row in $titleChoices){[void]$titleCombo.Items.Add($row.Display)};$titleCombo.Tag=$titleChoices;$titleCombo.SelectedIndex=0;for($i=0;$i-lt$titleChoices.Count;$i++){if([int]$titleChoices[$i].Grade-eq$defaultDungeonGrade){$titleCombo.SelectedIndex=$i;break}};$tabStart.Controls.Add($titleCombo)
Add-Label $tabStart '宠物选择' 35 234|Out-Null
$petCombo=New-Object Windows.Forms.ComboBox;$petCombo.Location=New-Object Drawing.Point(190,230);$petCombo.Size=New-Object Drawing.Size(700,30);$tabStart.Controls.Add($petCombo)
$petChoices=New-ChoiceList $pets -Pet;Bind-Combo $petCombo $petChoices $defaults.pet
Add-Label $tabStart '宠物当前岁数' 35 274|Out-Null
$petAgeCombo=New-Object Windows.Forms.ComboBox;$petAgeCombo.Location=New-Object Drawing.Point(190,270);$petAgeCombo.Size=New-Object Drawing.Size(140,30);$petAgeCombo.DropDownStyle='DropDownList';$tabStart.Controls.Add($petAgeCombo)
$petDetail=New-Object Windows.Forms.Label;$petDetail.Location=New-Object Drawing.Point(350,268);$petDetail.Size=New-Object Drawing.Size(690,42);$tabStart.Controls.Add($petDetail)

$comboMap=@{}
$y=325
foreach($part in @('body','hair','top','bottom','accessory','effect')){
    Add-Label $tabStart ($PartLabels[$part]) 35 $y|Out-Null
    $c=New-Object Windows.Forms.ComboBox;$c.Location=New-Object Drawing.Point(190,($y-4));$c.Size=New-Object Drawing.Size(700,30);$tabStart.Controls.Add($c)
    $rows=@($equips|Where-Object part -eq $part);$choices=New-ChoiceList $rows;Bind-Combo $c $choices ([uint32]$defaults[$part]);$comboMap[$part]=$c;$y+=46
}
$comboMap.body.add_SelectedIndexChanged({$d=Get-SelectedData $comboMap.body;if($d-and$d.gender-eq'M'){$genderCombo.SelectedIndex=1}elseif($d-and$d.gender-eq'F'){$genderCombo.SelectedIndex=0}})
$warning=New-Object Windows.Forms.Label;$warning.Text='进入游戏时保存配置并重启本地协议适配器。';$warning.ForeColor=[Drawing.Color]::DarkOrange;$warning.AutoSize=$true;$warning.Location=New-Object Drawing.Point(190,608);$tabStart.Controls.Add($warning)

$saveBtn=New-Object Windows.Forms.Button;$saveBtn.Text='保存配置';$saveBtn.Size=New-Object Drawing.Size(125,40);$saveBtn.Location=New-Object Drawing.Point(75,650);$tabStart.Controls.Add($saveBtn)
$clientBtn=New-Object Windows.Forms.Button;$clientBtn.Text='一键进入 Nanaimo';$clientBtn.Size=New-Object Drawing.Size(230,40);$clientBtn.Location=New-Object Drawing.Point(215,650);$clientBtn.BackColor=[Drawing.Color]::LightGreen;$tabStart.Controls.Add($clientBtn)
$adapterBtn=New-Object Windows.Forms.Button;$adapterBtn.Text='仅启动适配器';$adapterBtn.Size=New-Object Drawing.Size(175,40);$adapterBtn.Location=New-Object Drawing.Point(460,650);$adapterBtn.BackColor=[Drawing.Color]::LightSkyBlue;$tabStart.Controls.Add($adapterBtn)
$folderBtn=New-Object Windows.Forms.Button;$folderBtn.Text='打开日志目录';$folderBtn.Size=New-Object Drawing.Size(140,40);$folderBtn.Location=New-Object Drawing.Point(650,650);$tabStart.Controls.Add($folderBtn)
$defaultBtn=New-Object Windows.Forms.Button;$defaultBtn.Text='恢复默认';$defaultBtn.Size=New-Object Drawing.Size(120,40);$defaultBtn.Location=New-Object Drawing.Point(805,650);$tabStart.Controls.Add($defaultBtn)
$status=New-Object Windows.Forms.Label;$status.Location=New-Object Drawing.Point(35,705);$status.Size=New-Object Drawing.Size(1000,60);$status.ForeColor=[Drawing.Color]::DarkBlue;$tabStart.Controls.Add($status)
function Update-LaunchModePresentation {
    $clientBtn.Text='一键进入 Nanaimo'
    $warning.Text='进入游戏时保存配置并重启本地协议适配器。'
}

# Launch-detail tab: show binary/profile diagnostics and pre-launch side effects.
# Connection metadata and command arguments stay internal, not in the visible preview.
$launchInfoTitle=New-Object Windows.Forms.Label;$launchInfoTitle.Text='协议适配器和客户端；以下显示两种独立动作';$launchInfoTitle.Font=New-Object Drawing.Font('Microsoft YaHei UI',13,[Drawing.FontStyle]::Bold);$launchInfoTitle.AutoSize=$true;$launchInfoTitle.Location=New-Object Drawing.Point(20,18);$tabLaunchInfo.Controls.Add($launchInfoTitle)
$launchInfoHint=New-Object Windows.Forms.Label;$launchInfoHint.Text='“刷新并校验Hash”会读取当前磁盘文件；真正启动时仍会再次严格校验大小、SHA-256和本地协议适配器配置。';$launchInfoHint.AutoSize=$true;$launchInfoHint.Location=New-Object Drawing.Point(22,52);$tabLaunchInfo.Controls.Add($launchInfoHint)
$refreshLaunchInfoBtn=New-Object Windows.Forms.Button;$refreshLaunchInfoBtn.Text='刷新并校验Hash';$refreshLaunchInfoBtn.Location=New-Object Drawing.Point(22,78);$refreshLaunchInfoBtn.Size=New-Object Drawing.Size(150,34);$tabLaunchInfo.Controls.Add($refreshLaunchInfoBtn)
$copyLaunchInfoBtn=New-Object Windows.Forms.Button;$copyLaunchInfoBtn.Text='复制全部信息';$copyLaunchInfoBtn.Location=New-Object Drawing.Point(184,78);$copyLaunchInfoBtn.Size=New-Object Drawing.Size(130,34);$tabLaunchInfo.Controls.Add($copyLaunchInfoBtn)
$launchInfoBox=New-Object Windows.Forms.RichTextBox;$launchInfoBox.Location=New-Object Drawing.Point(22,124);$launchInfoBox.Size=New-Object Drawing.Size(1048,548);$launchInfoBox.ReadOnly=$true;$launchInfoBox.WordWrap=$false;$launchInfoBox.ScrollBars='Both';$launchInfoBox.Font=New-Object Drawing.Font('Consolas',9);$launchInfoBox.BackColor=[Drawing.Color]::White;$tabLaunchInfo.Controls.Add($launchInfoBox)

function Quote-LaunchArg([string]$value){return '"'+$value.Replace('"','\"')+'"'}
function File-State-Line([string]$label,[string]$path,[long]$expectedSize,[string]$expectedHash,[switch]$ComputeHash){
    if(-not(Test-Path -LiteralPath $path)){return "$label : MISSING | $path"}
    $item=Get-Item -LiteralPath $path;$sizeState=if($item.Length-eq$expectedSize){'SIZE_OK'}else{"SIZE_BAD expected=$expectedSize"}
    $hashText=if($ComputeHash){$h=(Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash;if($h-eq$expectedHash){"SHA256_OK $h"}else{"SHA256_BAD expected=$expectedHash actual=$h"}}else{"SHA256 expected=$expectedHash（点击刷新后计算actual）"}
    return "$label : $sizeState actual=$($item.Length) | $hashText`r`n         $path"
}
function Client-State-Line {
    if(-not(Test-Path -LiteralPath $Client -PathType Leaf)){return "Client : MISSING | $Client"}
    $item=Get-Item -LiteralPath $Client
    return "Client : PRESENT actual_size=$($item.Length) | user-supplied unpacking; hash/size are not launch gates`r`n         $Client"
}
function Get-ResourceSelection {
    $hpMax=[uint16][decimal]$hpMaxBox.Value;$hpCurrent=[uint16][decimal]$hpCurrentBox.Value
    $mpMax=[uint16][decimal]$mpMaxBox.Value;$mpCurrent=[uint16][decimal]$mpCurrentBox.Value
    if($hpCurrent-gt$hpMax){throw 'HP当前值不能大于HP最大值。'}
    if($mpCurrent-gt$mpMax){throw 'MP当前值不能大于MP最大值。'}
    return [ordered]@{hp_max=$hpMax;hp_current=$hpCurrent;mp_max=$mpMax;mp_current=$mpCurrent;attack=[uint32][decimal]$attackBox.Value;defense=[uint16][decimal]$defenseBox.Value;coin=[uint64][decimal]$coinBox.Value;nana_point=[uint64][decimal]$nanaPointBox.Value;card_key_normal=[byte][decimal]$cardKeyNormalBox.Value;card_key_gold=[byte][decimal]$cardKeyGoldBox.Value;card_key_mystery=[byte][decimal]$cardKeyMysteryBox.Value;card_key_special=[byte][decimal]$cardKeySpecialBox.Value;free_magic_key_expiry=[uint32][decimal]$freeMagicKeyExpiryBox.Value;quickbar_expiry=[uint32][decimal]$quickbarExpiryBox.Value}
}
function Get-SelectedDungeonTitle {if($titleCombo.SelectedIndex-lt0-or-not$titleCombo.Tag-or$titleCombo.SelectedIndex-ge$titleCombo.Tag.Count){throw '请选择称号。'};return $titleCombo.Tag[$titleCombo.SelectedIndex]}
function Update-LaunchPreview([switch]$ComputeHashes){
    $pet=Get-SelectedData $petCombo;$mode=Selected-AttackMode;$resources=Get-ResourceSelection;$skills=Get-SkillSelection;$equipSummary=@();foreach($part in @('body','hair','top','bottom','accessory','effect')){$d=Get-SelectedData $comboMap[$part];if($d){$equipSummary+=("{0}={1}[{2}]"-f$PartLabels[$part],$d.name,$d.id)}}
    $processFilter={param($p)(@($Adapter,$AdapterBridge,$LegacyAdapter)-contains$p.Path)-or($p.ProcessName-eq'game'-and$p.Path-eq$Client)}
    $running=@(Get-Process -ErrorAction SilentlyContinue|Where-Object $processFilter|ForEach-Object{"$($_.ProcessName)(PID=$($_.Id))"});if(-not$running){$running=@('<none>')}
    $adapterState=File-State-Line 'Full adapter' $Adapter $ExpectedAdapterSize $ExpectedAdapterHash -ComputeHash:$ComputeHashes
    $bridgeState=File-State-Line 'Gameplay bridge' $AdapterBridge $ExpectedBridgeSize $ExpectedBridgeHash -ComputeHash:$ComputeHashes
    $lines=@(
        '=== Character and profile ===',
        "Character=$($nameBox.Text.Trim()) | Level=$([int]$levelBox.Value) | Gender=$(if($genderCombo.SelectedIndex-eq1){'M'}else{'F'}) | Title=$((Get-SelectedDungeonTitle).Display)",
        "Resources: HP=$($resources.hp_current)/$($resources.hp_max) MP=$($resources.mp_current)/$($resources.mp_max) attack_modifier=$($resources.attack) defense_flat=$($resources.defense) coin=$($resources.coin) nana_point=$($resources.nana_point)",
        ("Skills: projectile={0} meat={1} Z={2}[{3}] X={4}[{5}] grades={6}"-f@("none","upper","lower")[$skills.projectile_route],@("none","upper","lower")[$skills.meat_route],(Skill-CodeName $skills.slot_z),$skills.slot_z,(Skill-CodeName $skills.slot_x),$skills.slot_x,($skills.grades-join",")),
        "Card keys: normal=$($resources.card_key_normal) gold=$($resources.card_key_gold) mystery=$($resources.card_key_mystery) special=$($resources.card_key_special) free_expiry=$($resources.free_magic_key_expiry) quickbar_expiry=$($resources.quickbar_expiry)",
        "Pet=$(if($pet){$pet.name+'['+$pet.id+'] age='+$(Selected-PetAge)+'/'+$pet.max_age}else{'<none>'}) | initial_attack_mode=$mode",
        ('Equipment: '+($equipSummary-join '; ')),
        "Profile INI : $ProfileIni","Profile JSON: $ProfileJson",'',
        '=== Binary validation ===',$adapterState,$bridgeState,
        (Client-State-Line),
        'Derived client compatibility assets are not validated at launch; create them from your own game files with scripts/prepare_client_compatibility.py.',
        '=== Pre-launch actions ===',
        ('Processes to stop: '+($running-join ', ')),
        'Adapter button: save profile; start/stop the complete local adapter; never launch the game.',
        'One-click button: stop detected adapter processes; save profile; validate files; restart the complete adapter; register profile; launch game.','',
        ('Working directory: '+$Root),'',
        '这是韩国飞行射击游戏 Nanaimo 的启动器。'
    )
    $launchInfoBox.Text=$lines-join "`r`n"
}
$refreshLaunchInfoBtn.add_Click({try{Update-LaunchPreview -ComputeHashes}catch{[Windows.Forms.MessageBox]::Show($_.Exception.Message,'刷新启动信息失败')|Out-Null}})
$copyLaunchInfoBtn.add_Click({if($launchInfoBox.Text){[Windows.Forms.Clipboard]::SetText($launchInfoBox.Text);$status.Text='已复制“本次启动详情”到剪贴板。'}})
$tabs.add_SelectedIndexChanged({if($tabs.SelectedTab-eq$tabLaunchInfo){Update-LaunchPreview}})

function Update-PetAgeOptions([Nullable[int]]$desired){
$r=Get-SelectedData $petCombo;if(-not$r){return};$max=[Math]::Max(0,[int]$r.max_age);$min=if([uint32]$r.id-in[uint32[]](15000001,15000002,15000003)){1}else{0};if($min-gt$max){$min=$max};$want=if($null-ne$desired){[int]$desired}else{[int]$r.display_age};if($r.wire_age_status-eq'observed' -and $want-lt[int]$r.wire_current_age){$want=[int]$r.wire_current_age};$want=[Math]::Min($max,[Math]::Max($min,$want))
$petAgeCombo.BeginUpdate();$petAgeCombo.Items.Clear();for($i=$min;$i-le$max;$i++){[void]$petAgeCombo.Items.Add("$i 岁")};$petAgeCombo.Tag=$min;$petAgeCombo.EndUpdate();$petAgeCombo.SelectedIndex=$want-$min
}
function Selected-PetAge {$min=if($null-ne$petAgeCombo.Tag){[int]$petAgeCombo.Tag}else{0};if($petAgeCombo.SelectedIndex-ge0){return $min+[int]$petAgeCombo.SelectedIndex};return $min}
function Update-AttackModes {
    $pet=Get-SelectedData $petCombo;$attackCombo.Items.Clear();if(-not$pet){return}
    $m=$attackByPet[[uint32]$pet.id];if(-not$m){[void]$attackCombo.Items.Add('映射缺失');$attackCombo.SelectedIndex=0;return}
    foreach($st in $m.basic_stages){if([int]$st.slot-gt2){continue};$tag=if([int]$st.slot-eq0){'PROFILE_DEFAULTS最低档+auto'}else{"PROFILE_DEFAULTS初始档$($st.slot)"};[void]$attackCombo.Items.Add(("阶段{0} slot{1} owner={2} {3} [{4}]"-f$st.display_stage,$st.slot,$st.owner_key,$st.resource,$tag))}
    $want=if($script:firstAttackModeLoad-and$defaultAttackMode-ge0-and$defaultAttackMode-le2){[int]$defaultAttackMode}else{[int]$m.initial_slot};$attackIndex=[int]$want;if($attackIndex-lt0){$attackIndex=0};if($attackIndex-ge$attackCombo.Items.Count){$attackIndex=$attackCombo.Items.Count-1};$attackCombo.SelectedIndex=$attackIndex;$script:firstAttackModeLoad=$false
}
function Selected-AttackMode {if($attackCombo.SelectedIndex-ge0){return [int]$attackCombo.SelectedIndex};return 0}
function Update-PetDetail {$r=Get-SelectedData $petCombo;if($r){$petDetail.Text="攻击：$($r.attack_style) 初攻：$($r.attack_value) 原生槽：$($r.native_initial_slot) Power序列：$($r.power_unlock_sequence -join '/')；蓄力资源：$($r.static_charge_text)，实际：$($r.charge_text)，门值：$($r.charge_gate_value)，MP门：实机确认，候选需求=$($r.charge_gate_value)，精确比较链待闭合；自动资源：$($r.static_auto_resource)，解锁：$($r.auto_unlock_class)，owner：$($r.auto_owners -join '/')，跟踪资源：$($r.homing_resource)，MP：$($r.auto_mp_gate)，wire：$($r.auto_wire_status)；岁数：$(Selected-PetAge)/$($r.max_age) wire=$($r.wire_age_status)"}}
$petCombo.add_SelectedIndexChanged({$r=Get-SelectedData $petCombo;if($r){Update-PetAgeOptions ([int]$r.display_age)};Update-AttackModes;Update-PetDetail})
$petAgeCombo.add_SelectedIndexChanged({Update-PetDetail})
$nameBox.add_TextChanged({if($launchInfoBox){Update-LaunchPreview}})
$levelBox.add_ValueChanged({if($launchInfoBox){Update-LaunchPreview}})
$genderCombo.add_SelectedIndexChanged({if($launchInfoBox){Update-LaunchPreview}})
$titleCombo.add_SelectedIndexChanged({if($launchInfoBox){Update-LaunchPreview}})
$attackCombo.add_SelectedIndexChanged({if($launchInfoBox){Update-LaunchPreview}})
foreach($previewCombo in $comboMap.Values){$previewCombo.add_SelectedIndexChanged({if($launchInfoBox){Update-LaunchPreview}})}
foreach($resourceBox in @($hpMaxBox,$hpCurrentBox,$mpMaxBox,$mpCurrentBox,$coinBox,$nanaPointBox,$cardKeyNormalBox,$cardKeyGoldBox,$cardKeyMysteryBox,$cardKeySpecialBox,$freeMagicKeyExpiryBox)){$resourceBox.add_ValueChanged({if($launchInfoBox){try{Update-LaunchPreview}catch{$launchInfoBox.Text=$_.Exception.Message}}})}
Update-PetAgeOptions $defaultPetAge;Update-AttackModes;Update-PetDetail;Update-LaunchModePresentation;Update-LaunchPreview
if($PreviewOnly){Update-LaunchPreview -ComputeHashes;Write-Output $launchInfoBox.Text;exit 0}

function Test-AdapterBinary {
    foreach($path in @($AdapterManifest,$Adapter,$AdapterBridge)){if(-not(Test-Path -LiteralPath $path)){throw "完整适配器文件缺失: $path"}}
    $contract=Get-Content -LiteralPath $AdapterManifest -Raw -Encoding UTF8|ConvertFrom-Json
    foreach($spec in @(@('Nanaimo.Adapter.exe',$Adapter),@('nanaimo_gameplay_bridge.exe',$AdapterBridge))){
        $row=@($contract.files|Where-Object name -eq $spec[0])[0]
        if(-not$row){throw "适配器清单缺少: $($spec[0])"}
        $item=Get-Item -LiteralPath $spec[1]
        if($item.Length-ne[long]$row.size){throw "适配器文件大小不匹配: $($spec[0])"}
        if((Get-FileHash -Algorithm SHA256 -LiteralPath $spec[1]).Hash-ne[string]$row.sha256){throw "适配器文件 SHA-256 不匹配: $($spec[0])"}
    }
}
function Test-ClientBinary {
    if(-not(Test-Path -LiteralPath $Client -PathType Leaf)){throw "Client missing: $Client"}
}
function Register-ClientProfile([string]$ip){
    $tcp=New-Object Net.Sockets.TcpClient
    try{
        $tcp.Connect($ip,11999);$stream=$tcp.GetStream();$bytes=[IO.File]::ReadAllBytes($ProfileIni);$len=[BitConverter]::GetBytes([uint32]$bytes.Length)
        $stream.Write($len,0,4);$stream.Write($bytes,0,$bytes.Length);$stream.Flush();$ack=New-Object byte[] 3;$got=$stream.Read($ack,0,3)
        if($got-ne3-or[Text.Encoding]::ASCII.GetString($ack)-ne"OK`n"){throw 'profile registry rejected'}
    }catch{throw '适配器配置注册失败，请检查适配器状态及端口11999。'}finally{if($tcp){$tcp.Close()}}
}
function Get-LocalAdapters {
    $paths=@($Adapter,$AdapterBridge,$LegacyAdapter)
    return @(Get-Process -ErrorAction SilentlyContinue|Where-Object{try{$_.Path-and($paths-contains$_.Path)}catch{$false}})
}
function Stop-LocalAdapter {
    $running=Get-LocalAdapters
    if(-not$running.Count){return}
    if(@($running|Where-Object{$_.Path-eq$Adapter}).Count){
        if(-not(Test-Path -LiteralPath $AdapterData)){New-Item -ItemType Directory -Path $AdapterData -Force|Out-Null}
        New-Item -ItemType File -Path $AdapterStop -Force|Out-Null
        foreach($p in @($running|Where-Object{$_.Path-eq$Adapter})){Wait-Process -Id $p.Id -Timeout 8 -ErrorAction SilentlyContinue}
    }
    $remaining=Get-LocalAdapters
    if($remaining.Count){$remaining|Stop-Process -Force;foreach($p in $remaining){Wait-Process -Id $p.Id -Timeout 5 -ErrorAction SilentlyContinue}}
    Start-Sleep -Milliseconds 250
    if((Get-LocalAdapters).Count){throw '适配器进程未能退出，请在任务管理器中关闭后重试。'}
}
function Test-AdapterPort([int]$port){$tcp=New-Object Net.Sockets.TcpClient;try{$ar=$tcp.BeginConnect('127.0.0.1',$port,$null,$null);if(-not$ar.AsyncWaitHandle.WaitOne(150)){return $false};$tcp.EndConnect($ar);return $true}catch{return $false}finally{$tcp.Close()}}
function Start-LocalAdapter {
    Test-AdapterBinary
    foreach($path in @($AdapterData,$AdapterLogs)){if(-not(Test-Path -LiteralPath $path)){New-Item -ItemType Directory -Path $path -Force|Out-Null}}
    Remove-Item -LiteralPath $AdapterStop,$AdapterLog,$AdapterErr -Force -ErrorAction SilentlyContinue
    $args=@('--data',$AdapterData,'--native',$AdapterBridge,'--profile',$ProfileIni,'--login-port','11005','--world-port','12050','--profile-port','11999','--log-directory',$AdapterLogs)
    $proc=Start-Process -FilePath $Adapter -ArgumentList $args -WorkingDirectory $AdapterRuntimeRoot -WindowStyle Hidden -RedirectStandardOutput $AdapterLog -RedirectStandardError $AdapterErr -PassThru
    for($i=0;$i-lt100;$i++){
        Start-Sleep -Milliseconds 100
        if($proc.HasExited){$detail=if(Test-Path -LiteralPath $AdapterErr){Get-Content -LiteralPath $AdapterErr -Raw}else{'无错误日志。'};throw "适配器退出，代码 $($proc.ExitCode)：$detail"}
        if((Test-AdapterPort 11005)-and(Test-AdapterPort 11999)-and(Test-AdapterPort 12050)){return $proc}
    }
    Stop-LocalAdapter
    throw '适配器未在10秒内准备完成；请检查日志。'
}$inventoryAdmin=Initialize-InventoryAdmin $tabs $Root $(if($ini.name_hex){[string]$ini.name_hex}else{Encode-NameHex $defaultName})
function Save-Profile {
    if((Get-LocalAdapters).Count){throw 'Inventory files are live. Stop the local Nanaimo protocol adapter first, or use Save and Enter Game to stop-save-restart safely.'}
    $name=$nameBox.Text.Trim();$hex=Encode-NameHex $name;$pet=Get-SelectedData $petCombo;if(-not$pet){throw '请选择宠物。'}
    $selected=@{};foreach($part in $comboMap.Keys){$d=Get-SelectedData $comboMap[$part];if(-not$d){throw "请选择 $($PartLabels[$part])。"};$selected[$part]=$d}
    $age=Selected-PetAge;$resources=Get-ResourceSelection;$skills=Get-SkillSelection;$titleSelection=Get-SelectedDungeonTitle;$launchMode=Get-SelectedLaunchMode;$networkIp=Get-NetworkIpInput;$launchModeInfo=Get-SelectedLaunchModeInfo
    $lines=@('version=2',"launch_mode=$launchMode","network_ip=$networkIp","skip_tutorial=$([int]$skipTutorialBox.Checked)","gender=$($genderCombo.SelectedIndex)","name_hex=$hex","dungeon_grade=$(if([int]$titleSelection.Grade-ge0){[int]$titleSelection.Grade}else{'auto'})","level=$([int]$levelBox.Value)","pet=$($pet.id)","pet_age_a=$age","pet_age_b=$($pet.max_age)","initial_attack_mode=$(Selected-AttackMode)","equip_hair=$($selected.hair.id)","equip_body=$($selected.body.id)","equip_top=$($selected.top.id)","equip_bottom=$($selected.bottom.id)","equip_accessory=$($selected.accessory.id)","equip_effect=$($selected.effect.id)","hp_max=$($resources.hp_max)","hp_current=$($resources.hp_current)","mp_max=$($resources.mp_max)","mp_current=$($resources.mp_current)","attack=$($resources.attack)","defense=$($resources.defense)","coin=$($resources.coin)","nana_point=$($resources.nana_point)","card_key_normal=$($resources.card_key_normal)","card_key_gold=$($resources.card_key_gold)","card_key_mystery=$($resources.card_key_mystery)","card_key_special=$($resources.card_key_special)","free_magic_key_expiry=$($resources.free_magic_key_expiry)","quickbar_expiry=$($resources.quickbar_expiry)")
    $lines+=@('skill_config=1',"skill_projectile_route=$($skills.projectile_route)","skill_meat_route=$($skills.meat_route)","skill_slot_z=$($skills.slot_z)","skill_slot_x=$($skills.slot_x)")
    for($i=0;$i-lt16;$i++){$lines+="skill_grade$i=$($skills.grades[$i])"}
    [IO.File]::WriteAllLines($ProfileIni,$lines,(New-Object Text.ASCIIEncoding))
    $adminShop=[ordered]@{coin=[uint64]$resources.coin;nana=[uint64]$resources.nana_point;equipped=@([uint32]$selected.hair.id,[uint32]$selected.body.id,[uint32]$selected.top.id,[uint32]$selected.bottom.id,[uint32]$selected.accessory.id);effect=[uint32]$selected.effect.id;selected_pet=[uint32]$pet.id}
    $adminResult=Save-InventoryAdminState $inventoryAdmin $hex $adminShop
    $titleStatePath=$null;if([int]$titleSelection.Grade-ge0){$titleStatePath=Write-DungeonGradeState $ProfileStateRoot $hex ([int]$titleSelection.Grade)}
    $view=[ordered]@{launch_mode=$launchMode;network_ip=$networkIp;start_local_adapter=$launchModeInfo.StartLocalAdapter;skip_tutorial=[bool]$skipTutorialBox.Checked;name=$name;level=[int]$levelBox.Value;title=[ordered]@{mode=if([int]$titleSelection.Grade-ge0){'fixed'}else{'progress'};grade=[int]$titleSelection.Grade;rank=[string]$titleSelection.Rank;resource_id=$titleSelection.ResourceId;icon_resource=$titleSelection.IconResource;name=[string]$titleSelection.Name;state_file=$titleStatePath};gender=if($genderCombo.SelectedIndex-eq1){'M'}else{'F'};pet=$pet;pet_selected_age=$age;initial_attack_mode=Selected-AttackMode;equipment=[ordered]@{hair=$selected.hair;body=$selected.body;top=$selected.top;bottom=$selected.bottom;accessory=$selected.accessory;effect=$selected.effect};resources=[ordered]@{hp_max=$resources.hp_max;hp_current=$resources.hp_current;mp_max=$resources.mp_max;mp_current=$resources.mp_current;attack=$resources.attack;defense=$resources.defense;attack_carrier='CFEC+0x2E0';defense_policy='local max(1, raw-defense) before D010/D015';coin=$resources.coin;nana_point=$resources.nana_point;card_key_normal=$resources.card_key_normal;card_key_gold=$resources.card_key_gold;card_key_mystery=$resources.card_key_mystery;card_key_special=$resources.card_key_special;free_magic_key_expiry=$resources.free_magic_key_expiry;quickbar_expiry=$resources.quickbar_expiry;currency_carriers='C37B+C379';item_carriers='C3E8+C430+C474'};inventory_admin=[ordered]@{account_suffix=$adminResult.account_suffix;backup=$adminResult.backup;clothing=$inventoryAdmin.Clothing.Count;pets=$inventoryAdmin.Pets.Count;game_item_kinds=$inventoryAdmin.GameItems.Count;furniture=$inventoryAdmin.Furniture.Count;cards=$inventoryAdmin.Cards.Count};skills=[ordered]@{projectile_route=$skills.projectile_route;meat_route=$skills.meat_route;slot_z=$skills.slot_z;slot_x=$skills.slot_x;grades=@($skills.grades)};saved_at=(Get-Date).ToString('s')}
    [IO.File]::WriteAllText($ProfileJson,($view|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
    $status.Text="Saved profile + title selection + five inventory domains (backup: $($adminResult.backup)).`r`nHP $($resources.hp_current)/$($resources.hp_max), MP $($resources.mp_current)/$($resources.mp_max), attack +$($resources.attack), defense $($resources.defense); Title=$($titleSelection.Display); Z=$(Skill-CodeName $skills.slot_z), X=$(Skill-CodeName $skills.slot_x).";Update-LaunchPreview
}
if($SelfTestProfileIO){
    Save-Profile;$skillExpect=Get-SkillSelection;$roundIni=Read-IniProfile;$roundJson=Get-Content -LiteralPath $ProfileJson -Raw -Encoding UTF8|ConvertFrom-Json
    $expect=Get-ResourceSelection;$expectTitle=Get-SelectedDungeonTitle;$expectMode=Get-SelectedLaunchMode;$expectIp=Get-NetworkIpInput
    if([string]$roundIni.launch_mode-ne$expectMode-or[string]$roundJson.launch_mode-ne$expectMode){throw 'launch_mode roundtrip'}
    if([string]$roundIni.dungeon_grade-ne$(if([int]$expectTitle.Grade-ge0){[string][int]$expectTitle.Grade}else{'auto'})-or[int]$roundJson.title.grade-ne[int]$expectTitle.Grade){throw 'dungeon_grade roundtrip'}
    if([string]$roundIni.network_ip-ne$expectIp-or[string]$roundJson.network_ip-ne$expectIp){throw 'network_ip roundtrip'}
    $expectSkip=[int]$skipTutorialBox.Checked;if([int]$roundIni.skip_tutorial-ne$expectSkip-or[bool]$roundJson.skip_tutorial-ne[bool]$skipTutorialBox.Checked){throw 'skip_tutorial roundtrip'}
    foreach($k in 'hp_max','hp_current','mp_max','mp_current','attack','defense','coin','nana_point','card_key_normal','card_key_gold','card_key_mystery','card_key_special','free_magic_key_expiry','quickbar_expiry'){if([string]$roundIni[$k]-ne[string]$expect[$k]){throw "INI roundtrip $k"};if([string]$roundJson.resources.$k-ne[string]$expect[$k]){throw "JSON roundtrip $k"}}
    $skillExpectedValues=@{skill_projectile_route=$skillExpect.projectile_route;skill_meat_route=$skillExpect.meat_route;skill_slot_z=$skillExpect.slot_z;skill_slot_x=$skillExpect.slot_x}
    foreach($k in $skillExpectedValues.Keys){if([string]$roundIni[$k]-ne[string]$skillExpectedValues[$k]){throw "INI roundtrip $k"}}
    for($j=0;$j-lt16;$j++){if([int]$roundIni["skill_grade$j"]-ne[int]$skillExpect.grades[$j]){throw "INI roundtrip skill_grade$j"};if([int]$roundJson.skills.grades[$j]-ne[int]$skillExpect.grades[$j]){throw "JSON roundtrip skill_grade$j"}}
    if([uint32]$roundJson.skills.slot_z-ne[uint32]$skillExpect.slot_z-or[uint32]$roundJson.skills.slot_x-ne[uint32]$skillExpect.slot_x){throw 'JSON roundtrip skill slots'}
    Write-Output ("NETWORK_PROFILE_IO_PASS title_grade=$([int]$expectTitle.Grade) launch_mode={0} network_ip={1} hp={2}/{3} mp={4}/{5} attack={6} defense={7} coin={8} nana_point={9} skip_tutorial={10} skill_routes={11}/{12} Z={13} X={14}"-f$expectMode,$expectIp,$expect.hp_current,$expect.hp_max,$expect.mp_current,$expect.mp_max,$expect.attack,$expect.defense,$expect.coin,$expect.nana_point,$expectSkip,$skillExpect.projectile_route,$skillExpect.meat_route,$skillExpect.slot_z,$skillExpect.slot_x);exit 0
}
$saveBtn.add_Click({try{Save-Profile;[Windows.Forms.MessageBox]::Show('配置已保存。','Nanaimo 启动器')|Out-Null}catch{[Windows.Forms.MessageBox]::Show($_.Exception.Message,'配置错误')|Out-Null}})
$folderBtn.add_Click({$target=if(Test-Path -LiteralPath $AdapterLogs){$AdapterLogs}else{$Root};Start-Process explorer.exe -ArgumentList $target})
$resetProgressBtn.add_Click({try{if((Get-LocalAdapters).Count){throw 'Stop the local Nanaimo protocol adapter first.'};$name=$nameBox.Text.Trim();if(-not$name){throw 'Character name is required.'};$hex=Encode-NameHex $name;$paths=@((Join-Path $Root ("level_progress_state_v1_{0}.dat"-f$hex)),(Join-Path $Root ("level_progress_state_v1_{0}.bak"-f$hex)),(Join-Path $Root ("level_progress_state_v1_{0}.new"-f$hex)),(Join-Path $Root ("dungeon_grade_state_v1_{0}.dat"-f$hex)),(Join-Path $Root ("dungeon_grade_state_v1_{0}.bak"-f$hex)),(Join-Path $Root ("dungeon_grade_state_v1_{0}.new"-f$hex)));Remove-Item -LiteralPath $paths -Force -ErrorAction SilentlyContinue;$status.Text="Reset level, EXP, and dungeon-title progress for $name. Next start seeds level $([int]$levelBox.Value) and dungeon grade 0."}catch{[Windows.Forms.MessageBox]::Show($_.Exception.Message,'Reset failed')|Out-Null}})
$defaultBtn.add_Click({$skipTutorialBox.Checked=$false;$nameBox.Text='Greyrat';$titleCombo.SelectedIndex=0;$levelBox.Value=25;$hpMaxBox.Value=1500;$hpCurrentBox.Value=1500;$mpMaxBox.Value=500;$mpCurrentBox.Value=500;$attackBox.Value=0;$defenseBox.Value=0;$coinBox.Value=0;$nanaPointBox.Value=0;$cardKeyNormalBox.Value=99;$cardKeyGoldBox.Value=99;$cardKeyMysteryBox.Value=99;$cardKeySpecialBox.Value=99;$freeMagicKeyExpiryBox.Value=2099123123;$projectileRouteCombo.SelectedIndex=0;$meatRouteCombo.SelectedIndex=0;for($i=0;$i-lt16;$i++){$skillGradeBoxes[$i].Value=0};foreach($i in 0,1,8,9){$skillGradeBoxes[$i].Value=5};Set-SkillSlotChoices 0 0;Update-SkillWarning;$genderCombo.SelectedIndex=1;Select-ComboId $petCombo 15009205|Out-Null;Update-PetAgeOptions 3;Select-ComboId $comboMap.hair 10130337|Out-Null;Select-ComboId $comboMap.body 10100028|Out-Null;Select-ComboId $comboMap.top 10110337|Out-Null;Select-ComboId $comboMap.bottom 10120352|Out-Null;Select-ComboId $comboMap.accessory 10150103|Out-Null;Select-ComboId $comboMap.effect 10160017|Out-Null;Update-PetDetail;Update-LaunchModePresentation})
$adapterBtn.add_Click({
    try{
        if((Get-LocalAdapters).Count){Stop-LocalAdapter;$adapterBtn.Text='仅启动适配器';$status.Text='适配器已停止。';return}
        Save-Profile
        $proc=Start-LocalAdapter
        $adapterBtn.Text='停止适配器';$status.Text="完整适配器已启动 PID=$($proc.Id)。`r`n日志: $AdapterLogs"
    }catch{[Windows.Forms.MessageBox]::Show($_.Exception.Message,'适配器启动失败')|Out-Null}
})
$clientBtn.add_Click({
    try{
        $launchModeInfo=Get-SelectedLaunchModeInfo
        Stop-LocalAdapter
        Save-Profile;Test-ClientBinary;Install-LaunchModeConfig $launchModeInfo
        $proc=Start-LocalAdapter
        $adapterBtn.Text='停止适配器'
        if($launchModeInfo.Key-eq'network'){Register-ClientProfile $launchModeInfo.AdapterIP}
        Get-Process -Name game -ErrorAction SilentlyContinue|Where-Object{$_.Path-eq$Client}|Stop-Process -Force;Start-Sleep -Milliseconds 250
        if($launchModeInfo.ClientArgs.Count){Start-Process -FilePath $Client -ArgumentList ([string[]]$launchModeInfo.ClientArgs) -WorkingDirectory $Root|Out-Null}else{Start-Process -FilePath $Client -WorkingDirectory $Root|Out-Null}
        $status.Text='角色配置已保存；完整适配器已重启并完成注册，Nanaimo 客户端已启动。'
    }catch{[Windows.Forms.MessageBox]::Show($_.Exception.Message,'启动 Nanaimo 失败')|Out-Null}
})
# Pet lookup tab
$petSearch=New-Object Windows.Forms.TextBox;$petSearch.Location=New-Object Drawing.Point(18,18);$petSearch.Size=New-Object Drawing.Size(500,28);$tabPets.Controls.Add($petSearch)
$petSearchBtn=New-Object Windows.Forms.Button;$petSearchBtn.Text='筛选';$petSearchBtn.Location=New-Object Drawing.Point(530,16);$petSearchBtn.Size=New-Object Drawing.Size(90,32);$tabPets.Controls.Add($petSearchBtn)
$petClearBtn=New-Object Windows.Forms.Button;$petClearBtn.Text='清除';$petClearBtn.Location=New-Object Drawing.Point(630,16);$petClearBtn.Size=New-Object Drawing.Size(90,32);$tabPets.Controls.Add($petClearBtn)
$petSplit=New-Object Windows.Forms.SplitContainer;$petSplit.Location=New-Object Drawing.Point(18,58);$petSplit.Size=New-Object Drawing.Size(1060,710);$petSplit.Anchor='Top,Left';$petSplit.Orientation='Vertical';$petSplit.FixedPanel='Panel2';$petSplit.SplitterDistance=790;$petSplit.Panel1MinSize=520;$petSplit.Panel2MinSize=245;$tabPets.Controls.Add($petSplit)
$petGrid=New-Object Windows.Forms.DataGridView;$petGrid.Dock='Fill';$petGrid.ReadOnly=$true;$petGrid.AllowUserToAddRows=$false;$petGrid.SelectionMode='FullRowSelect';$petGrid.MultiSelect=$false;$petGrid.AutoSizeColumnsMode='DisplayedCells';$petSplit.Panel1.Controls.Add($petGrid)
$petPreviewPane=New-CatalogPreviewPane $petSplit.Panel2 '宠物资源预览'
$petTable=New-GridTable 'pet' $pets;$petView=New-Object Data.DataView;$petView.Table=$petTable;$petGrid.DataSource=$petView
$applyPetFilter={ $q=Escape-Filter $petSearch.Text.Trim();$petView.RowFilter=if($q){"[名称] LIKE '%$q%' OR [ID] LIKE '%$q%' OR [攻击方式] LIKE '%$q%' OR [初攻定义] LIKE '%$q%' OR [自动资源] LIKE '%$q%' OR [跟踪资源] LIKE '%$q%' OR [MP条件] LIKE '%$q%' OR [等级需求] LIKE '%$q%' OR [BOO] LIKE '%$q%'"}else{''};if($petGrid.Rows.Count){$petGrid.Rows[0].Selected=$true;Update-CatalogGridPreview 'pet' $petGrid $petPreviewPane} }
$petSearchBtn.add_Click($applyPetFilter);$petClearBtn.add_Click({$petSearch.Clear();$petView.RowFilter='';if($petGrid.Rows.Count){$petGrid.Rows[0].Selected=$true;Update-CatalogGridPreview 'pet' $petGrid $petPreviewPane}})
$petGrid.add_SelectionChanged({Update-CatalogGridPreview 'pet' $petGrid $petPreviewPane})
$petGrid.add_CellDoubleClick({param($sender,$e)if($e.RowIndex-ge0){$id=[uint32]$petGrid.Rows[$e.RowIndex].Cells['ID'].Value;if(Select-ComboId $petCombo $id){$tabs.SelectedTab=$tabStart;Update-PetDetail}}})

# Equipment lookup tab
$equipSearch=New-Object Windows.Forms.TextBox;$equipSearch.Location=New-Object Drawing.Point(18,18);$equipSearch.Size=New-Object Drawing.Size(420,28);$tabEquip.Controls.Add($equipSearch)
$partFilter=New-Object Windows.Forms.ComboBox;$partFilter.Location=New-Object Drawing.Point(450,16);$partFilter.Size=New-Object Drawing.Size(170,30);$partFilter.DropDownStyle='DropDownList';[void]$partFilter.Items.Add('全部部位');foreach($x in @('body','hair','top','bottom','accessory','effect','other')){[void]$partFilter.Items.Add($PartLabels[$x])};$partFilter.SelectedIndex=0;$tabEquip.Controls.Add($partFilter)
$equipSearchBtn=New-Object Windows.Forms.Button;$equipSearchBtn.Text='筛选';$equipSearchBtn.Location=New-Object Drawing.Point(635,16);$equipSearchBtn.Size=New-Object Drawing.Size(90,32);$tabEquip.Controls.Add($equipSearchBtn)
$equipClearBtn=New-Object Windows.Forms.Button;$equipClearBtn.Text='清除';$equipClearBtn.Location=New-Object Drawing.Point(735,16);$equipClearBtn.Size=New-Object Drawing.Size(90,32);$tabEquip.Controls.Add($equipClearBtn)
$equipSplit=New-Object Windows.Forms.SplitContainer;$equipSplit.Location=New-Object Drawing.Point(18,58);$equipSplit.Size=New-Object Drawing.Size(1060,710);$equipSplit.Anchor='Top,Left';$equipSplit.Orientation='Vertical';$equipSplit.FixedPanel='Panel2';$equipSplit.SplitterDistance=790;$equipSplit.Panel1MinSize=520;$equipSplit.Panel2MinSize=245;$tabEquip.Controls.Add($equipSplit)
$equipGrid=New-Object Windows.Forms.DataGridView;$equipGrid.Dock='Fill';$equipGrid.ReadOnly=$true;$equipGrid.AllowUserToAddRows=$false;$equipGrid.SelectionMode='FullRowSelect';$equipGrid.MultiSelect=$false;$equipGrid.AutoSizeColumnsMode='DisplayedCells';$equipSplit.Panel1.Controls.Add($equipGrid)
$equipPreviewPane=New-CatalogPreviewPane $equipSplit.Panel2 '装备资源预览'
$equipTable=New-GridTable 'equip' $equips;$equipView=New-Object Data.DataView;$equipView.Table=$equipTable;$equipGrid.DataSource=$equipView
$applyEquipFilter={ $parts=@{0='';1=$PartLabels.body;2=$PartLabels.hair;3=$PartLabels.top;4=$PartLabels.bottom;5=$PartLabels.accessory;6=$PartLabels.effect;7=$PartLabels.other};$conds=New-Object Collections.Generic.List[string];$q=Escape-Filter $equipSearch.Text.Trim();if($q){$conds.Add("([名称] LIKE '%$q%' OR [ID] LIKE '%$q%' OR [效果说明] LIKE '%$q%' OR [模型] LIKE '%$q%')")};if($partFilter.SelectedIndex-gt0){$v=Escape-Filter $parts[$partFilter.SelectedIndex];$conds.Add("[部位] = '$v'")};$equipView.RowFilter=($conds-join' AND ');if($equipGrid.Rows.Count){$equipGrid.Rows[0].Selected=$true;Update-CatalogGridPreview 'equip' $equipGrid $equipPreviewPane} }
$equipSearchBtn.add_Click($applyEquipFilter);$equipClearBtn.add_Click({$equipSearch.Clear();$partFilter.SelectedIndex=0;$equipView.RowFilter='';if($equipGrid.Rows.Count){$equipGrid.Rows[0].Selected=$true;Update-CatalogGridPreview 'equip' $equipGrid $equipPreviewPane}})
$equipGrid.add_SelectionChanged({Update-CatalogGridPreview 'equip' $equipGrid $equipPreviewPane})
$layoutPetCatalog={Set-CatalogSplitLayout $tabPets $petSplit $petPreviewPane}.GetNewClosure();$layoutEquipCatalog={Set-CatalogSplitLayout $tabEquip $equipSplit $equipPreviewPane}.GetNewClosure()
$tabPets.add_Resize($layoutPetCatalog);$tabEquip.add_Resize($layoutEquipCatalog);&$layoutPetCatalog;&$layoutEquipCatalog
$equipGrid.add_CellDoubleClick({param($sender,$e)if($e.RowIndex-ge0){$id=[uint32]$equipGrid.Rows[$e.RowIndex].Cells['ID'].Value;$partName=[string]$equipGrid.Rows[$e.RowIndex].Cells['部位'].Value;$part=($PartLabels.Keys|Where-Object{$PartLabels[$_]-eq$partName}|Select-Object -First 1);if($part-and$comboMap.ContainsKey($part)){Select-ComboId $comboMap[$part] $id|Out-Null;$tabs.SelectedTab=$tabStart}else{[Windows.Forms.MessageBox]::Show('该资源不属于当前六个可装备部位。','资源查表')|Out-Null}}})
$form.add_FormClosed({if($petPreviewPane.Picture.Image){$petPreviewPane.Picture.Image.Dispose()};if($equipPreviewPane.Picture.Image){$equipPreviewPane.Picture.Image.Dispose()};if($script:petPreviewAtlas){$script:petPreviewAtlas.Dispose()};if($script:equipPreviewAtlas){$script:equipPreviewAtlas.Dispose()}})
if($petGrid.Rows.Count){Update-CatalogGridPreview 'pet' $petGrid $petPreviewPane};if($equipGrid.Rows.Count){Update-CatalogGridPreview 'equip' $equipGrid $equipPreviewPane}
function Assert-NoVisibleConnectionText([Windows.Forms.Control]$control){
    if(-not$control.Visible){return}
    if($control.Text-match '(?i)连接方式|连接模式|启动模式|适配器地址|固定使用|\bNetwork\b|127\.0\.0\.1|ServerIP|network_ip|launch_mode|Selected launch mode|Mode template|\bMode=|\bLogin=|Standalone|Stand_Alone|(?<!\w)-q(?!\w)'){
        throw "visible connection wording: $($control.GetType().Name) $($control.Text)"
    }
    foreach($child in $control.Controls){Assert-NoVisibleConnectionText $child}
}
# Exercise real WinForms layout without launching a game or saving any player state.
if($SelfTestLayout){
    $form.ShowInTaskbar=$false;$form.Opacity=0;[void]$form.Show()
    try{
        $tabs.SelectedTab=$tabStart;[Windows.Forms.Application]::DoEvents();$defaultBtn.PerformClick()
        if($nameBox.Text-ne'Greyrat'){throw 'default name'}
        $expectedOutfit=@{hair=10130337;body=10100028;top=10110337;bottom=10120352;accessory=10150103;effect=10160017}
        foreach($part in $expectedOutfit.Keys){if((Get-SelectedData $comboMap[$part]).id-ne$expectedOutfit[$part]){throw "default outfit $part"}}
        if((Get-SelectedLaunchModeInfo).AdapterIP-ne'127.0.0.1'){throw 'fixed loopback mode'}
        foreach($page in $tabs.TabPages){
            $tabs.SelectedTab=$page;[Windows.Forms.Application]::DoEvents()
            Assert-NoVisibleConnectionText $form
        }
        $tabs.SelectedTab=$tabLaunchInfo;Update-LaunchPreview -ComputeHashes
        Assert-NoVisibleConnectionText $form
        $tabs.SelectedTab=$tabStart;[Windows.Forms.Application]::DoEvents()
        if($levelBox.Top-ne144-or$titleCombo.Top-ne190-or$comboMap.body.Top-ne321-or$saveBtn.Top-ne650-or$clientBtn.Left-ne215-or$adapterBtn.Left-ne460-or$status.Top-ne705){throw 'startup row compaction'}
        if($levelBox.Top-$nameBox.Top-ne40){throw 'empty startup connection row'}
        $tabs.SelectedTab=$tabResources
        $previousScale=1.0
        foreach($scale in @(1.0,1.25,1.5)){
            $ratio=[single]($scale/$previousScale);$form.Scale((New-Object Drawing.SizeF($ratio,$ratio)));$previousScale=$scale
            foreach($size in @((New-Object Drawing.Size(1000,870)),(New-Object Drawing.Size(1120,940)))){
                $form.Size=$size;$tabResources.AutoScrollPosition=New-Object Drawing.Point(0,0)
                $form.PerformLayout();$tabResources.PerformLayout();[Windows.Forms.Application]::DoEvents()
                $controls=@($tabResources.Controls)
                for($i=0;$i-lt$controls.Count;$i++){for($j=$i+1;$j-lt$controls.Count;$j++){
                    if($controls[$i].Bounds.IntersectsWith($controls[$j].Bounds)){throw "resource siblings overlap: $($controls[$i].Text) / $($controls[$j].Text) bounds=$($controls[$i].Bounds)/$($controls[$j].Bounds) scale=$scale"}
                }}
                foreach($group in @($projectileSkillGroup,$meatSkillGroup)){
                    foreach($child in $group.Controls){if(-not$group.ClientRectangle.Contains($child.Bounds)){throw "skill child clipped: $($child.Text)"}}
                }
                foreach($control in @($projectileRouteCombo,$meatRouteCombo)+$skillGradeBoxes+@($skillZCombo,$skillXCombo)){
                    $tabResources.ScrollControlIntoView($control);[Windows.Forms.Application]::DoEvents()
                    $viewport=$tabResources.RectangleToScreen($tabResources.ClientRectangle)
                    $rect=$control.RectangleToScreen($control.ClientRectangle)
                    if(-not$control.Visible-or$rect.Width-lt30-or$rect.Height-lt15-or-not$viewport.Contains($rect)){throw "skill control inaccessible: $($control.Name) $rect viewport=$viewport"}
                }
            }
        }
        Write-Output 'GUI_LAYOUT_SELFTEST_PASS sizes=1000x870,1120x940 scales=1,1.25,1.5 skill_grades=16 routes=2 slots=2 overlap=false defaults=PASS connection_text=absent startup_compaction=40 state_writes=0'
    }finally{$form.Close();$form.Dispose()}
    exit 0
}

if($SelfTestCatalogPreview){
    $form.ShowInTaskbar=$false;$form.Opacity=0;$form.WindowState='Maximized';[void]$form.Show()
    if($tabs.TabPages.Count-ne10){throw 'launcher page count'}
    $tabs.SelectedTab=$tabStart;[Windows.Forms.Application]::DoEvents();$nameBox.Text='UI_TEST';$defaultBtn.PerformClick()
    if($nameBox.Text-ne'Greyrat'){throw 'restore-defaults handler did not complete'}
    $tabs.SelectedTab=$tabPets;[Windows.Forms.Application]::DoEvents();Test-CatalogSplitLayout $tabPets $petSplit $petPreviewPane 'pet'
    $tabs.SelectedTab=$tabEquip;[Windows.Forms.Application]::DoEvents();Test-CatalogSplitLayout $tabEquip $equipSplit $equipPreviewPane 'equip'
    foreach($r in $petGrid.Rows){if([string]$r.Cells['ID'].Value-eq'15009205'){$petGrid.CurrentCell=$r.Cells['ID'];break}}
    foreach($r in $equipGrid.Rows){if([string]$r.Cells['ID'].Value-eq'10030458'){$equipGrid.CurrentCell=$r.Cells['ID'];break}}
    Update-CatalogGridPreview 'pet' $petGrid $petPreviewPane;Update-CatalogGridPreview 'equip' $equipGrid $equipPreviewPane
    if(-not$petPreviewPane.Picture.Image-or-not$equipPreviewPane.Picture.Image){throw 'catalog preview self-test image missing'}
    if($petPreviewPane.Picture.Image.Width-ne64-or$equipPreviewPane.Picture.Image.Width-ne64){throw 'catalog preview self-test crop size'}
    $petRect=$petPreviewPane.Group.RectangleToScreen($petPreviewPane.Group.ClientRectangle);$equipRect=$equipPreviewPane.Group.RectangleToScreen($equipPreviewPane.Group.ClientRectangle)
    Write-Output ('NETWORK_CATALOG_PREVIEW_SELFTEST_PASS pet={0} equip={1} maps={2}/{3} petPane={4} equipPane={5}'-f$petGrid.CurrentRow.Cells['ID'].Value,$equipGrid.CurrentRow.Cells['ID'].Value,$petPreviewRows.Count,$equipPreviewRows.Count,$petRect,$equipRect)
    $form.Close();$form.Dispose();exit 0
}


[void]$form.ShowDialog()

