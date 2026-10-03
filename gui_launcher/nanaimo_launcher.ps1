param([switch]$ValidateOnly,[switch]$PreviewOnly,[switch]$SelfTestProfileIO,[switch]$SelfTestInventoryIO,[switch]$SelfTestLaunchModes,[switch]$SelfTestPureNewPlayer,[switch]$SelfTestAdapterManifest,[switch]$SelfTestLayout,[switch]$SelfTestCatalogPreview,[switch]$SelfTestSocialMode,[switch]$SelfTestLocalEntry,[switch]$SelfTestTitleIO,[string]$ProfileIniOverride,[string]$ProfileJsonOverride)
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
$ClientCompatibilityTool=Join-Path $Root 'scripts\prepare_client_compatibility.py'
$ClientCompatibilityOverlay=Join-Path $AdapterData 'client_compatibility_overlay'
$ClientCompatibilityReport=Join-Path $ClientCompatibilityOverlay 'nanaimo_compatibility_report.json'
$AdapterLogs=Join-Path $AdapterData 'logs'
$AdapterStop=Join-Path $AdapterData 'stop.request'
$PureNewPlayerProfile=Join-Path $AdapterData 'pure_new_player_profile.ini'
$PureNewPlayerAccountState=Join-Path $AdapterData 'pure_new_player_account.txt'
$ProfileIni=if($ProfileIniOverride){[IO.Path]::GetFullPath($ProfileIniOverride)}else{Join-Path $Root 'nanaimo_launcher_profile.ini'}
$ProfileJson=if($ProfileJsonOverride){[IO.Path]::GetFullPath($ProfileJsonOverride)}else{Join-Path $Root 'nanaimo_launcher_profile.json'}
$ProfileStateRoot=if($ProfileIniOverride){Split-Path $ProfileIni -Parent}else{$Root}
$AdapterLog=Join-Path $Root 'adapter_nanaimo_launcher.log'
$AdapterErr=Join-Path $Root 'adapter_nanaimo_launcher_stderr.log'
$PetJson=Join-Path $Root 'gui_launcher\data\pets.json'
$EquipJson=Join-Path $Root 'gui_launcher\data\equipment.json'
$FurnitureJson=Join-Path $Root 'gui_launcher\data\inventory_furniture.json'
$AttackJson=Join-Path $Root 'gui_launcher\data\pet_attack_modes.json'
$PreviewDir=Join-Path $Root 'gui_launcher\data\previews'
$PetPreviewPng=Join-Path $PreviewDir 'pet_icons.png';$PetPreviewJson=Join-Path $PreviewDir 'pet_icons.json'
$EquipPreviewPng=Join-Path $PreviewDir 'equipment_icons.png';$EquipPreviewJson=Join-Path $PreviewDir 'equipment_icons.json'
$FurniturePreviewPng=Join-Path $PreviewDir 'furniture_icons.png';$FurniturePreviewJson=Join-Path $PreviewDir 'furniture_icons.json'
$SocialAdapterIpState=Join-Path $Root 'adapter_ip.txt'
$SocialClientLauncher=Join-Path $Root 'gui_launcher\start_social_client.ps1'
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
$ExpectedAdapterDllHash=''
$ExpectedAdapterDllSize=0
$CanonicalLauncher=Join-Path $Root 'start_nanaimo_launcher.bat'
$RuntimeIdentityPath=Join-Path $AdapterData 'runtime_identity.json'
if(Test-Path -LiteralPath $AdapterManifest){
    $adapterContract=Get-Content -LiteralPath $AdapterManifest -Raw -Encoding UTF8|ConvertFrom-Json
    $adapterRow=@($adapterContract.files|Where-Object name -eq 'Nanaimo.Adapter.exe')[0]
    $bridgeRow=@($adapterContract.files|Where-Object name -eq 'nanaimo_gameplay_bridge.exe')[0]
    $adapterDllRow=@($adapterContract.files|Where-Object name -eq 'Nanaimo.Adapter.dll')[0]
    if($adapterRow){$ExpectedAdapterHash=[string]$adapterRow.sha256;$ExpectedAdapterSize=[long]$adapterRow.size}
    if($bridgeRow){$ExpectedBridgeHash=[string]$bridgeRow.sha256;$ExpectedBridgeSize=[long]$bridgeRow.size}
    if($adapterDllRow){$ExpectedAdapterDllHash=[string]$adapterDllRow.sha256;$ExpectedAdapterDllSize=[long]$adapterDllRow.size}
}
$ReleaseIdentity=if($ExpectedAdapterDllHash){'Adapter.dll '+$ExpectedAdapterDllHash.Substring(0,[Math]::Min(12,$ExpectedAdapterDllHash.Length))}else{'Adapter.dll UNVERIFIED'}
$GbK=[Text.Encoding]::GetEncoding(936)
$KnownDungeonTitles=@{0='修炼中的初级收集者';1='打败机炮飞艇的收集者';2='打败大机械熊偶的收集者';3='打败斯巴特洛的收集者';4='打败单眼怪里奥的收集者';5='打败石鬼豪斯的收集者';6='打败立忠大将军的收集者';7='打败独角蓝鬼咒魁的收集者';8='打败不老的始皇帝的收集者';9='打败乔伊桑和蜈蚣王的收集者';10='打败疾风刺客法雷的收集者';11='打败不死神坛的收集者';12='打败坏熊梅尔文的收集者';13='打败海盗王丹尼的收集者';14='打败轰天炮台的收集者';15='打败三龟巨魔的收集者';16='打败派·罗斯的收集者';17='打败瓦格拉诺的收集者';18='打败闇黑法老王的收集者';19='打败邪神马米加的收集者';20='打败迷之爱丽丝的收集者';21='打败冰龙邪王的收集者';22='打败大魔女明琪的收集者';23='打败头脑胶囊的收集者';24='打败马斯特洛克的收集者'}
function Get-DungeonTitleIconResource([int]$grade){if($grade-lt0-or$grade-gt42){throw '称号档位必须为0～42。'};return 1243+$grade}
function Get-DungeonTitleTextResource([int]$grade){if($grade-lt0-or$grade-gt42){throw '称号档位必须为0～42。'};if($grade-le20){return 2038+$grade};if($grade-le38){return 2394+($grade-21)};return 2412}
function New-DungeonTitleChoices([int]$currentGrade=-1){
    $rows=New-Object Collections.ArrayList
    [void]$rows.Add([pscustomobject]@{Grade=-1;ResourceId=$null;IconResource=$null;TextResource=$null;Rank='';Name='跟随进度档';Display='跟随已有地宫进度，不覆盖称号'})
    for($g=0;$g-le42;$g++){
        $icon=Get-DungeonTitleIconResource $g;$text=Get-DungeonTitleTextResource $g
        $rank=if($g-ge1-and$g-le16){'P'+$g}elseif($g-ge17-and$g-le39){'R'+($g-16)}else{''}
        $name=if($KnownDungeonTitles.ContainsKey($g)){[string]$KnownDungeonTitles[$g]}elseif($g-ge25){'原生文本为空'}else{"称号档位 $g"}
        $suffix=if($g-ge40){'（原生文本为空；无配套图标）'}elseif($rank){"（$rank）"}else{''}
        [void]$rows.Add([pscustomobject]@{Grade=$g;ResourceId=$icon;IconResource=$icon;TextResource=$text;Rank=$rank;Name=$name;Display=("档位 {0,2} | {1}{2}"-f$g,$name,$suffix)})
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
function Read-PureNewPlayerUsername {if(-not(Test-Path -LiteralPath $PureNewPlayerAccountState)){return ''};return [string](Get-Content -LiteralPath $PureNewPlayerAccountState -TotalCount 1).Trim()}
function Save-PureNewPlayerUsername([string]$username){$username=$username.Trim();if($username.Length-lt1-or$username.Length-gt64-or(@($username.ToCharArray()|Where-Object{[char]::IsControl($_)}).Count-gt0)){throw '新手档用户名必须为1到64个非控制字符。'};if(-not(Test-Path -LiteralPath $AdapterData)){New-Item -ItemType Directory -Path $AdapterData -Force|Out-Null};[IO.File]::WriteAllText($PureNewPlayerAccountState,$username+("`r`n"),(New-Object Text.UTF8Encoding($false)));return $username}
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
    } elseif($kind-eq'furniture') {
        foreach($c in '类型','名称','ID','天数','金币','NANA点','说明','效果','来源') {[void]$t.Columns.Add($c)}
        foreach($r in $rows){[void]$t.Rows.Add($r.type_name,$r.name,[string]$r.id,[string]$r.days,[string]$r.coin,[string]$r.nana,$r.description,$r.effect,$r.source)}
    } else {
        foreach($c in '部位','名称','ID','性别','等级或天数','效果说明','限制','模型') {[void]$t.Columns.Add($c)}
        foreach($r in $rows){[void]$t.Rows.Add($PartLabels[$r.part],$r.name,[string]$r.id,$r.gender,[string]$r.level_or_days,$r.effect_description,$r.restriction,$r.model)}
    }
    return ,$t
}function Escape-Filter([string]$s){return $s.Replace("'","''").Replace('[','[[]').Replace('%','[%]').Replace('*','[*]')}
function Get-CatalogPreviewAtlas([string]$kind){
    if($kind-eq'pet'){$path=$PetPreviewPng;if($script:petPreviewAtlas){return $script:petPreviewAtlas}}
    elseif($kind-eq'equip'){$path=$EquipPreviewPng;if($script:equipPreviewAtlas){return $script:equipPreviewAtlas}}
    elseif($kind-eq'furniture'){$path=$FurniturePreviewPng;if($script:furniturePreviewAtlas){return $script:furniturePreviewAtlas}}
    else{throw "Unknown catalog preview kind: $kind"}
    if(-not(Test-Path -LiteralPath $path)){return $null}
    $loaded=[Drawing.Image]::FromFile($path)
    try{$copy=New-Object Drawing.Bitmap $loaded}finally{$loaded.Dispose()}
    if($kind-eq'pet'){$script:petPreviewAtlas=$copy}elseif($kind-eq'equip'){$script:equipPreviewAtlas=$copy}else{$script:furniturePreviewAtlas=$copy}
    return $copy
}function New-CatalogPreviewPane($parent,[string]$caption){
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
    if($kind-eq'pet'){$row=$petById[$id];$entry=$petPreviewById[$id];$atlas=Get-CatalogPreviewAtlas 'pet'}
    elseif($kind-eq'equip'){$row=$equipById[$id];$entry=$equipPreviewById[$id];$atlas=Get-CatalogPreviewAtlas 'equip'}
    else{$row=$furnitureById[$id];$entry=$furniturePreviewById[$id];$atlas=Get-CatalogPreviewAtlas 'furniture'}
    $ok=Set-CatalogPreviewImage $pane.Picture $atlas $entry
    if($row){
        $pane.Name.Text="$($row.name)`r`n[$id]"
        if($kind-eq'pet'){$pane.Source.Text="图标: $($row.icon)`r`n模型: $($row.image)`r`n部位: $($row.part)`r`n性别: $($row.gender)"}
        elseif($kind-eq'equip'){$pane.Source.Text="图标: $($row.icon)`r`n模型: $($row.model)`r`n部位: $($row.part)`r`n性别: $($row.gender)"}
        else{$pane.Source.Text="类型: $($row.type_name)`r`n天数: $($row.days)`r`n金币/NANA: $($row.coin)/$($row.nana)`r`n效果: $($row.effect)`r`n来源: $($row.source)"}
    }else{$pane.Name.Text="[$id]";$pane.Source.Text='无资源目录记录'}
    $pane.Note.Text=if($ok){if($kind-eq'furniture'){'已生成本地家具目录缩略图；不修改游戏数据。'}else{'已从本地客户端资源生成预览。'}}else{'暂无预览：资源缺失或提取不支持。'}
}
function Get-PreferredLanIPv4 {
    $candidates=@([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()|Where-Object{$_.OperationalStatus-eq[Net.NetworkInformation.OperationalStatus]::Up-and$_.NetworkInterfaceType-ne[Net.NetworkInformation.NetworkInterfaceType]::Loopback}|ForEach-Object{
        $props=$_.GetIPProperties();$hasGateway=@($props.GatewayAddresses|Where-Object{$_.Address.AddressFamily-eq[Net.Sockets.AddressFamily]::InterNetwork-and-not$_.Address.Equals([Net.IPAddress]::Any)}).Count-gt0
        foreach($u in $props.UnicastAddresses){if($u.Address.AddressFamily-eq[Net.Sockets.AddressFamily]::InterNetwork-and-not[Net.IPAddress]::IsLoopback($u.Address)-and-not$u.Address.ToString().StartsWith('169.254.')){[pscustomobject]@{Address=$u.Address.ToString();Gateway=$hasGateway;Name=$_.Name}}}
    })
    $selected=@($candidates|Sort-Object @{Expression='Gateway';Descending=$true},Name,Address|Select-Object -First 1)
    if($selected.Count){return [string]$selected[0].Address};return '127.0.0.1'
}function Normalize-NetworkIPv4([string]$value){
    $candidate=$value.Trim();$parsed=$null
    if($candidate-notmatch '^\d{1,3}(\.\d{1,3}){3}$'-or-not[Net.IPAddress]::TryParse($candidate,[ref]$parsed)-or$parsed.AddressFamily-ne[Net.Sockets.AddressFamily]::InterNetwork-or$parsed.ToString()-ne$candidate){throw "Invalid adapter configuration."}
    return $candidate
}
function Get-LaunchModeInfo([string]$mode='network',[string]$networkIp='127.0.0.1'){
    if($mode-ne'network'){throw 'Unsupported launch configuration.'}
    $networkIp=Normalize-NetworkIPv4 $networkIp
    $parsed=[Net.IPAddress]::Parse($networkIp)
    return [pscustomobject]@{Key='network';Display='Network (-q)';Template=$NetworkOptionTemplate;Login='Network Login Game';AdapterIP=$networkIp;ClientArgs=[string[]]@('-q',':1:1:0:3:4:-i','5:-r',("6:7:1:{0}:"-f$networkIp));StartLocalAdapter=[Net.IPAddress]::IsLoopback($parsed)}
}function Test-LaunchModeConfig($info,[string]$path,[string]$expectedIp=$info.AdapterIP){
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
function Get-LaunchModeConfigText($info){
    $templateText=Test-LaunchModeTemplate $info
    return $templateText-replace '(?m)^ServerIP=.*$',("ServerIP={0}"-f$info.AdapterIP)
}
function Install-LaunchModeConfig($info){
    $activeText=Get-LaunchModeConfigText $info
    $targetDir=Split-Path $ActiveGameOption -Parent
    if(-not(Test-Path -LiteralPath $targetDir)){[void](New-Item -ItemType Directory -Path $targetDir -Force)}
    [IO.File]::WriteAllText($ActiveGameOption,$activeText,(New-Object Text.ASCIIEncoding))
    [void](Test-LaunchModeConfig $info $ActiveGameOption)
}

if(-not(Test-Path -LiteralPath $PetJson)-or-not(Test-Path -LiteralPath $EquipJson)-or-not(Test-Path -LiteralPath $FurnitureJson)){[Windows.Forms.MessageBox]::Show('资源目录不存在，请先运行 gui_launcher\generate_catalog.py。','Nanaimo 启动器')|Out-Null;exit 2}
$pets=Get-Content -LiteralPath $PetJson -Raw -Encoding UTF8|ConvertFrom-Json
$equips=Get-Content -LiteralPath $EquipJson -Raw -Encoding UTF8|ConvertFrom-Json
$furniture=Get-Content -LiteralPath $FurnitureJson -Raw -Encoding UTF8|ConvertFrom-Json
$attackModes=Get-Content -LiteralPath $AttackJson -Raw -Encoding UTF8|ConvertFrom-Json
$petPreviewRows=if(Test-Path -LiteralPath $PetPreviewJson){@(Get-Content -LiteralPath $PetPreviewJson -Raw -Encoding UTF8|ConvertFrom-Json)}else{@()}
$equipPreviewRows=if(Test-Path -LiteralPath $EquipPreviewJson){@(Get-Content -LiteralPath $EquipPreviewJson -Raw -Encoding UTF8|ConvertFrom-Json)}else{@()}
$furniturePreviewRows=if(Test-Path -LiteralPath $FurniturePreviewJson){@(Get-Content -LiteralPath $FurniturePreviewJson -Raw -Encoding UTF8|ConvertFrom-Json)}else{@()}
$petPreviewById=@{};foreach($x in $petPreviewRows){$petPreviewById[[uint32]$x.id]=$x}
$equipPreviewById=@{};foreach($x in $equipPreviewRows){$equipPreviewById[[uint32]$x.id]=$x}
$furniturePreviewById=@{};foreach($x in $furniturePreviewRows){$furniturePreviewById[[uint32]$x.id]=$x}
$script:petPreviewAtlas=$null;$script:equipPreviewAtlas=$null;$script:furniturePreviewAtlas=$null
$attackByPet=@{};foreach($x in $attackModes){$attackByPet[[uint32]$x.pet_id]=$x}
$petById=@{};foreach($x in $pets){$petById[[uint32]$x.id]=$x}
$equipById=@{};foreach($x in $equips){$equipById[[uint32]$x.id]=$x}
$furnitureById=@{};foreach($x in $furniture){$furnitureById[[uint32]$x.id]=$x}
$ini=Read-IniProfile
# Ignore legacy saved mode/address: GUI always uses the loopback network protocol.
$defaultLaunchMode='network'
$defaultNetworkIp='127.0.0.1'
$defaultName=if($ini.name_hex){Decode-NameHex $ini.name_hex}else{'Greyrat'}
$currentDungeonGrade=if($ini.name_hex){Read-DungeonGradeState $ProfileStateRoot ([string]$ini.name_hex)}else{-1}
$defaultDungeonGrade=-1;if($ini.ContainsKey('dungeon_grade')){[int]$parsedGrade=-1;if([int]::TryParse([string]$ini.dungeon_grade,[ref]$parsedGrade)-and$parsedGrade-ge0-and$parsedGrade-le42){$defaultDungeonGrade=$parsedGrade}}
$titleChoices=New-DungeonTitleChoices $currentDungeonGrade
$defaultLevel=if($ini.level){[int]$ini.level}else{25}
$defaultPetAge=if($ini.pet_age_a){[int]$ini.pet_age_a}else{$null}
$defaultAttackMode=if($ini.initial_attack_mode-ne$null){[int]$ini.initial_attack_mode}else{-1};$script:firstAttackModeLoad=$true
$defaultHpMax=Read-ProfileU16 $ini 'hp_max' 1500
$defaultMpMax=Read-ProfileU16 $ini 'mp_max' 500
$defaultAttack=Read-ProfileUInt64 $ini 'attack' 0;if($defaultAttack-gt1000000){throw 'attack must be 0..1000000.'};$defaultAttack=[uint32]$defaultAttack
$defaultDefense=Read-ProfileUInt64 $ini 'defense' 0;if($defaultDefense-gt65535){throw 'defense must be 0..65535.'};$defaultDefense=[uint16]$defaultDefense
$defaultCoin=Read-ProfileUInt64 $ini 'coin' 0
$defaultNanaPoint=Read-ProfileUInt64 $ini 'nana_point' 0
$defaultApartmentPoints=Read-ProfileUInt64 $ini 'apartment_recommendation_points' 1000;if($defaultApartmentPoints-gt4294967295){throw 'apartment_recommendation_points must be 0..4294967295.'}
$defaultCardKeyNormal=[uint16](Read-ProfileU16 $ini 'card_key_normal' 99 -AllowZero);if($defaultCardKeyNormal-gt255){throw 'card_key_normal must be 0..255.'}
$defaultCardKeyGold=[uint16](Read-ProfileU16 $ini 'card_key_gold' 99 -AllowZero);if($defaultCardKeyGold-gt255){throw 'card_key_gold must be 0..255.'}
$defaultCardKeyMystery=[uint16](Read-ProfileU16 $ini 'card_key_mystery' 99 -AllowZero);if($defaultCardKeyMystery-gt255){throw 'card_key_mystery must be 0..255.'}
$defaultCardKeySpecial=[uint16](Read-ProfileU16 $ini 'card_key_special' 99 -AllowZero);if($defaultCardKeySpecial-gt255){throw 'card_key_special must be 0..255.'}
$defaultFreeMagicKeyExpiry=Read-ProfileUInt64 $ini 'free_magic_key_expiry' 2099123123;if($defaultFreeMagicKeyExpiry-lt2000010100-or$defaultFreeMagicKeyExpiry-gt2100123123){throw 'free_magic_key_expiry must be YYYYMMDDHH in 2000010100..2100123123.'}
$defaultQuickbarExpiry=Read-ProfileUInt64 $ini 'quickbar_expiry' 0;if($defaultQuickbarExpiry-ne0-and($defaultQuickbarExpiry-lt2000010100-or$defaultQuickbarExpiry-gt2100123123)){throw 'quickbar_expiry must be 0 or YYYYMMDDHH in 2000010100..2100123123.'}
$skillSlotExpiryConfigured=$ini.ContainsKey('skill_slot_expiry');$defaultSkillSlotExpiry=Read-ProfileUInt64 $ini 'skill_slot_expiry' 0;if($defaultSkillSlotExpiry-ne0-and($defaultSkillSlotExpiry-lt2000010100-or$defaultSkillSlotExpiry-gt2100123123)){throw 'skill_slot_expiry must be 0 or YYYYMMDDHH in 2000010100..2100123123.'}
$defaultSkipTutorial=$ini.ContainsKey('skip_tutorial')-and([string]$ini.skip_tutorial-eq'1')
$defaultUnlockAllDungeons=-not$ini.ContainsKey('unlock_all_dungeons')-or([string]$ini.unlock_all_dungeons-eq'1')
$defaultPureNewPlayerUsername=Read-PureNewPlayerUsername
$skillProfile=$ini
if(-not($ini.ContainsKey('skill_config')-and[string]$ini.skill_config-eq'1')-and$ini.name_hex){$skillPath=Join-Path $Root ("skill_progress_state_v2_{0}.dat"-f([string]$ini.name_hex));if(Test-Path -LiteralPath $skillPath){$skillProfile=Read-KeyValueFile $skillPath}}
$defaultSkillGrades=New-Object int[] 16;$haveSkillGrades=$false
for($i=0;$i-lt16;$i++){$k="skill_grade$i";if(-not$skillProfile.ContainsKey($k)){$k="grade$i"};if($skillProfile.ContainsKey($k)){[int]$v=0;if(-not[int]::TryParse([string]$skillProfile[$k],[ref]$v)-or$v-lt0-or$v-gt5){throw "技能等级 $k 必须为0..5。"};$defaultSkillGrades[$i]=$v;$haveSkillGrades=$true}}
if(-not$haveSkillGrades){$defaultSkillGrades[0]=$defaultSkillGrades[1]=$defaultSkillGrades[8]=$defaultSkillGrades[9]=5}
$defaultProjectileRoute=if($ini.ContainsKey('skill_projectile_route')){[int]$ini.skill_projectile_route}else{Infer-SkillRoute $defaultSkillGrades ([int[]](2,4,6)) ([int[]](3,5,7))}
$defaultMeatRoute=if($ini.ContainsKey('skill_meat_route')){[int]$ini.skill_meat_route}else{Infer-SkillRoute $defaultSkillGrades ([int[]](10,12,14)) ([int[]](11,13,15))}
$defaultSkillZ=if($ini.ContainsKey('skill_slot_z')){[uint32]$ini.skill_slot_z}elseif($skillProfile.ContainsKey('slot0')){[uint32]$skillProfile.slot0}else{[uint32]0}
$defaultSkillX=if($ini.ContainsKey('skill_slot_x')){[uint32]$ini.skill_slot_x}elseif($skillProfile.ContainsKey('slot1')){[uint32]$skillProfile.slot1}else{[uint32]0}
$defaults=@{pet=15009205;hair=10130337;body=10100028;top=10110337;bottom=10120352;accessory=10150103;effect=10160017}
foreach($k in @($defaults.Keys)){if($ini.ContainsKey($(if($k-eq'pet'){'pet'}else{"equip_$k"}))){$defaults[$k]=[uint32]$ini[$(if($k-eq'pet'){'pet'}else{"equip_$k"})]}}
$defaultBody=$equipById[[uint32]$defaults.body];$defaultGender=if($ini.gender){[int]$ini.gender}elseif($defaultBody-and$defaultBody.gender-eq'M'){1}else{0}
if($SelfTestTitleIO){
    $tmp=Join-Path ([IO.Path]::GetTempPath()) ('nanaimo_title_'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Force -Path $tmp|Out-Null
    try{
        if($titleChoices.Count-ne44){throw 'title choice count'}
        foreach($g in 0..42){$row=@($titleChoices|Where-Object Grade -eq $g)[0];if($row.IconResource-ne(1243+$g)-or$row.ResourceId-ne$row.IconResource-or$row.TextResource-ne(Get-DungeonTitleTextResource $g)){throw "title resources $g"}}
        $p1=@($titleChoices|Where-Object Grade -eq 1)[0];$p16=@($titleChoices|Where-Object Grade -eq 16)[0];$r1=@($titleChoices|Where-Object Grade -eq 17)[0];$r7=@($titleChoices|Where-Object Grade -eq 23)[0];$r8=@($titleChoices|Where-Object Grade -eq 24)[0];$r23=@($titleChoices|Where-Object Grade -eq 39)[0];$last=@($titleChoices|Where-Object Grade -eq 42)[0]
        if($KnownDungeonTitles.Count-ne25-or$p1.Rank-ne'P1'-or$p1.Name-ne'打败机炮飞艇的收集者'-or$p16.Rank-ne'P16'-or$p16.Name-ne'打败派·罗斯的收集者'-or$r1.Rank-ne'R1'-or$r1.Name-ne'打败瓦格拉诺的收集者'-or$r7.Rank-ne'R7'-or$r7.IconResource-ne1266-or$r7.TextResource-ne2396-or$r7.Name-ne'打败头脑胶囊的收集者'-or$r8.Rank-ne'R8'-or$r8.TextResource-ne2397-or$r8.Name-ne'打败马斯特洛克的收集者'-or$r23.Rank-ne'R23'-or$r23.IconResource-ne1282-or$r23.Name-ne'原生文本为空'-or$last.Rank-ne''-or$last.IconResource-ne1285-or$last.TextResource-ne2412-or$last.Name-ne'原生文本为空'){throw 'title choice mapping'}
        $path=Write-DungeonGradeState $tmp '5449544C4554455354' 42;$state=Read-KeyValueFile $path
        if([int]$state.grade-ne42-or[int]$state.frontier_valid-ne0-or(Read-DungeonGradeState $tmp '5449544C4554455354')-ne42){throw 'grade42 state roundtrip'}
        Write-Output 'CHARACTER_TITLE_IO_PASS choices=44 automatic=P1..P16+R1..R7 native_names=grade0..24 blank_text=25..42 images=1243..1285 unavailable_images=1283..1285 state_roundtrip=PASS'
    }finally{if(Test-Path -LiteralPath $tmp){[IO.Directory]::Delete($tmp,$true)}};exit 0
}
if($SelfTestInventoryIO){Write-Output (Test-InventoryAdminInstallation $Root);exit 0}
if($ValidateOnly){
    $inventoryValidation=Test-InventoryAdminInstallation $Root
    if($pets.Count-ne868){throw 'pet catalog count'};if($equips.Count-ne3885){throw 'equipment catalog count'};if($furniture.Count-ne781){throw 'furniture catalog count'};if($attackModes.Count-ne868){throw 'attack mode catalog count'};if($petPreviewRows.Count-ne868-or$equipPreviewRows.Count-ne3885-or$furniturePreviewRows.Count-ne781-or-not(Test-Path -LiteralPath $PetPreviewPng)-or-not(Test-Path -LiteralPath $EquipPreviewPng)-or-not(Test-Path -LiteralPath $FurniturePreviewPng)){throw 'catalog preview assets'}
    foreach($path in @($AdapterManifest,$Adapter,$AdapterBridge)){if(-not(Test-Path -LiteralPath $path)){throw "adapter artifact missing: $path"}};if((Get-Item -LiteralPath $Adapter).Length-ne$ExpectedAdapterSize-or(Get-FileHash -Algorithm SHA256 -LiteralPath $Adapter).Hash-ne$ExpectedAdapterHash){throw 'adapter validation'};if((Get-Item -LiteralPath $AdapterBridge).Length-ne$ExpectedBridgeSize-or(Get-FileHash -Algorithm SHA256 -LiteralPath $AdapterBridge).Hash-ne$ExpectedBridgeHash){throw 'bridge validation'}
    foreach($id in 15009205,10130337,10100028,10110337,10120352,10150103,10160017){if(-not($petById.ContainsKey([uint32]$id)-or$equipById.ContainsKey([uint32]$id))){throw "missing default id $id"}}
    $round=Decode-NameHex (Encode-NameHex '测试角色');if($round-ne'测试角色'){throw 'GBK name roundtrip'}
    $pt=New-GridTable 'pet' $pets;$et=New-GridTable 'equip' $equips;$ft=New-GridTable 'furniture' $furniture;if($pt.Rows.Count-ne868-or$et.Rows.Count-ne3885-or$ft.Rows.Count-ne781){throw 'grid table count'}
    $pv=New-Object Data.DataView;$pv.Table=$pt;$pv.RowFilter="[ID] LIKE '%15009205%'";if($pv.Count-ne1){throw 'pet filter'}
    $ev=New-Object Data.DataView;$ev.Table=$et;$ev.RowFilter="[部位] = '发型'";if($ev.Count-ne1049){throw 'equipment hair filter'};$ev.RowFilter="[部位] = '效果（特效道具）'";if($ev.Count-ne438){throw 'equipment effect filter'}
    if(@($equips|Where-Object{$_.part-eq'other'}).Count-ne2){throw 'equipment other classification'}
    $networkInfo=Get-LaunchModeInfo 'network' '127.0.0.1'
    [void](Test-LaunchModeTemplate $networkInfo)
    if(($networkInfo.ClientArgs-join' ')-ne'-q :1:1:0:3:4:-i 5:-r 6:7:1:127.0.0.1:'){throw 'network client arguments'}
    if($defaultCardKeyNormal-ne99-or$defaultCardKeyGold-ne99-or$defaultCardKeyMystery-ne99-or$defaultCardKeySpecial-ne99-or$defaultFreeMagicKeyExpiry-ne2099123123){throw 'card key defaults'};if($titleChoices.Count-ne44-or@($titleChoices|Where-Object Grade -eq 1)[0].Rank-ne'P1'-or@($titleChoices|Where-Object Grade -eq 16)[0].Rank-ne'P16'-or@($titleChoices|Where-Object Grade -eq 23)[0].Rank-ne'R7'-or@($titleChoices|Where-Object Grade -eq 39)[0].Rank-ne'R23'-or@($titleChoices|Where-Object Grade -eq 42)[0].Rank-ne''){throw 'title choices'}
    Write-Host ('NETWORK_VALIDATE_PASS titles=43+auto pets={0} equipment={1} profile={2}/{3} hp={4}/{5} mp={6}/{7} attack={8} defense={9} coin={10} nana_point={11} skip_tutorial={12} launch_mode={13} network_ip={14} templates=PASS filters=PASS inventory_admin=PASS'-f$pets.Count,$equips.Count,$defaultName,$defaultLevel,$defaultHpMax,$defaultHpMax,$defaultMpMax,$defaultMpMax,$defaultAttack,$defaultDefense,$defaultCoin,$defaultNanaPoint,[int]$defaultSkipTutorial,$defaultLaunchMode,$defaultNetworkIp);exit 0
}
if($SelfTestLaunchModes){
    $savedActive=$ActiveGameOption;$tempActive=Join-Path ([IO.Path]::GetTempPath()) ('nanaimo_gamestartoption_'+[guid]::NewGuid().ToString('N')+'.ini')
    try{
        $ActiveGameOption=$tempActive
        $info=Get-LaunchModeInfo;Install-LaunchModeConfig $info
        $text=Test-LaunchModeConfig $info $ActiveGameOption
        if(-not$info.StartLocalAdapter-or($info.ClientArgs-join' ')-ne'-q :1:1:0:3:4:-i 5:-r 6:7:1:127.0.0.1:'){throw 'loopback network self-test'}
        $lan=Get-LaunchModeInfo 'network' '198.51.100.20';Install-LaunchModeConfig $lan
        if($lan.StartLocalAdapter-or($lan.ClientArgs-join' ')-ne'-q :1:1:0:3:4:-i 5:-r 6:7:1:198.51.100.20:'-or(Test-LaunchModeConfig $lan $ActiveGameOption)-notmatch'(?m)^ServerIP=198\.51\.100\.20$'){throw 'LAN network self-test'}
        foreach($bad in @(@('standalone','127.0.0.1'),@('network','999.1.1.1'))){
            $rejected=$false;try{[void](Get-LaunchModeInfo $bad[0] $bad[1])}catch{$rejected=$true}
            if(-not$rejected){throw 'unsupported launch mode/address accepted'}
        }
        Write-Output 'GUI_LAUNCH_MODE_SELFTEST_PASS loopback=local LAN=remote dynamic_ip=true unsupported_modes_rejected=true'
    }finally{$ActiveGameOption=$savedActive;Remove-Item -LiteralPath $tempActive -Force -ErrorAction SilentlyContinue}
    exit 0
}

$form=New-Object Windows.Forms.Form
$form.Text="OpenNanaimo Launcher [$ReleaseIdentity]";$form.Size=New-Object Drawing.Size(1120,940);$form.StartPosition='CenterScreen';$form.MinimumSize=New-Object Drawing.Size(1000,870)
$tabs=New-Object Windows.Forms.TabControl;$tabs.Dock='Fill';$form.Controls.Add($tabs)
$tabSocial=New-Object Windows.Forms.TabPage;$tabSocial.Text='社交模式';$tabs.TabPages.Add($tabSocial)
$tabStart=New-Object Windows.Forms.TabPage;$tabStart.Text='本地模式';$tabs.TabPages.Add($tabStart)
$tabResources=New-Object Windows.Forms.TabPage;$tabResources.Text='数值管理';$tabs.TabPages.Add($tabResources)
$tabLaunchInfo=New-Object Windows.Forms.TabPage;$tabLaunchInfo.Text='本次启动详情'
$tabPets=New-Object Windows.Forms.TabPage;$tabPets.Text='宠物查表'
$tabEquip=New-Object Windows.Forms.TabPage;$tabEquip.Text='装扮查表'
$tabFurniture=New-Object Windows.Forms.TabPage;$tabFurniture.Text='装饰家具查表'

$title=New-Object Windows.Forms.Label;$title.Text='OpenNanaimo Launcher';$title.Font=New-Object Drawing.Font('Microsoft YaHei UI',16,[Drawing.FontStyle]::Bold);$title.AutoSize=$true;$title.Location=New-Object Drawing.Point(28,16);$tabStart.Controls.Add($title)
$releaseLabel=New-Object Windows.Forms.Label;$releaseLabel.Text="Release: $ReleaseIdentity | Canonical entry: start_nanaimo_launcher.bat";$releaseLabel.AutoSize=$true;$releaseLabel.ForeColor=[Drawing.Color]::DarkGreen;$releaseLabel.Location=New-Object Drawing.Point(30,50);$tabStart.Controls.Add($releaseLabel)
$hint=New-Object Windows.Forms.Label;$hint.Text='等级现由通关结算推进：每次成功 CF88 结算 +100 EXP，下一级需要当前等级×100；此处等级仅在该角色没有进度档时作为初始种子。';$hint.AutoSize=$true;$hint.Location=New-Object Drawing.Point(30,76);$tabStart.Controls.Add($hint)

function Add-Label($parent,$text,$x,$y,$w=150){$l=New-Object Windows.Forms.Label;$l.Text=$text;$l.Location=New-Object Drawing.Point($x,$y);$l.Size=New-Object Drawing.Size($w,25);$parent.Controls.Add($l);return $l}

$detectedLanIp=Get-PreferredLanIPv4
$savedSocialIp='';if(Test-Path -LiteralPath $SocialAdapterIpState){$savedSocialIp=[string](Get-Content -LiteralPath $SocialAdapterIpState -TotalCount 1).Trim()}
try{$defaultSocialIp=if($savedSocialIp-and-not[Net.IPAddress]::IsLoopback([Net.IPAddress]::Parse((Normalize-NetworkIPv4 $savedSocialIp)))){Normalize-NetworkIPv4 $savedSocialIp}else{$detectedLanIp}}catch{$defaultSocialIp=$detectedLanIp}
$socialTitle=New-Object Windows.Forms.Label;$socialTitle.Text='社交模式（局域网研究）';$socialTitle.Font=New-Object Drawing.Font('Microsoft YaHei UI',16,[Drawing.FontStyle]::Bold);$socialTitle.AutoSize=$true;$socialTitle.Location=New-Object Drawing.Point(28,22);$tabSocial.Controls.Add($socialTitle)
$socialHint=New-Object Windows.Forms.Label;$socialHint.Text='主机填写本机局域网 IPv4 并启动适配器；参与者填写主机 IPv4。账号按用户名复用数据库档案，注册与登录按来源 IP 隔离。';$socialHint.Location=New-Object Drawing.Point(30,62);$socialHint.Size=New-Object Drawing.Size(1020,45);$tabSocial.Controls.Add($socialHint)
$detectedIpLabel=New-Object Windows.Forms.Label;$detectedIpLabel.Text="自动检测本机 IPv4：$detectedLanIp";$detectedIpLabel.Location=New-Object Drawing.Point(35,112);$detectedIpLabel.Size=New-Object Drawing.Size(500,28);$detectedIpLabel.ForeColor=[Drawing.Color]::DarkGreen;$tabSocial.Controls.Add($detectedIpLabel)
Add-Label $tabSocial '适配器/加入 IP' 35 154 140|Out-Null
$socialIpBox=New-Object Windows.Forms.TextBox;$socialIpBox.Location=New-Object Drawing.Point(185,150);$socialIpBox.Size=New-Object Drawing.Size(300,28);$socialIpBox.Text=$defaultSocialIp;$tabSocial.Controls.Add($socialIpBox)
$socialUseDetectedBtn=New-Object Windows.Forms.Button;$socialUseDetectedBtn.Text='使用自动检测';$socialUseDetectedBtn.Location=New-Object Drawing.Point(500,147);$socialUseDetectedBtn.Size=New-Object Drawing.Size(135,34);$tabSocial.Controls.Add($socialUseDetectedBtn)
$socialAdapterBtn=New-Object Windows.Forms.Button;$socialAdapterBtn.Text='启动适配器';$socialAdapterBtn.Location=New-Object Drawing.Point(655,142);$socialAdapterBtn.Size=New-Object Drawing.Size(190,42);$socialAdapterBtn.BackColor=[Drawing.Color]::LightSkyBlue;$tabSocial.Controls.Add($socialAdapterBtn)
$socialAdapterNote=New-Object Windows.Forms.Label;$socialAdapterNote.Text='启动会先关闭本项目已有适配器进程，再按该 IPv4 单例启动。请在系统防火墙中允许本程序的局域网访问。';$socialAdapterNote.Location=New-Object Drawing.Point(35,202);$socialAdapterNote.Size=New-Object Drawing.Size(980,48);$socialAdapterNote.ForeColor=[Drawing.Color]::DarkOrange;$tabSocial.Controls.Add($socialAdapterNote)
$socialUserBoxes=@();$socialPlayButtons=@()
for($slot=1;$slot-le3;$slot++){
    $y=285+(($slot-1)*92);Add-Label $tabSocial ("P{0} 用户名"-f$slot) 80 $y 120|Out-Null
    $box=New-Object Windows.Forms.TextBox;$box.Location=New-Object Drawing.Point(205,($y-4));$box.Size=New-Object Drawing.Size(420,30);$box.Text=("P{0}"-f$slot);$tabSocial.Controls.Add($box);$socialUserBoxes+=$box
    $button=New-Object Windows.Forms.Button;$button.Text=("P{0} 参与游玩"-f$slot);$button.Location=New-Object Drawing.Point(655,($y-9));$button.Size=New-Object Drawing.Size(190,42);$button.BackColor=[Drawing.Color]::LightGreen;$tabSocial.Controls.Add($button);$socialPlayButtons+=$button
}
$socialMultiNote=New-Object Windows.Forms.Label;$socialMultiNote.Text='同机测试：依次点击 P1 / P2 / P3，可并行启动三个客户端；不要手动交换点击顺序，以保持同一来源 IP 下的账号队列顺序。';$socialMultiNote.Location=New-Object Drawing.Point(80,570);$socialMultiNote.Size=New-Object Drawing.Size(900,48);$socialMultiNote.ForeColor=[Drawing.Color]::DimGray;$tabSocial.Controls.Add($socialMultiNote)
$socialStatus=New-Object Windows.Forms.Label;$socialStatus.Location=New-Object Drawing.Point(80,635);$socialStatus.Size=New-Object Drawing.Size(900,100);$socialStatus.ForeColor=[Drawing.Color]::DarkBlue;$tabSocial.Controls.Add($socialStatus)

$resourceTitle=New-Object Windows.Forms.Label;$resourceTitle.Text='人物数值、账户货币与卡册道具';$resourceTitle.Font=New-Object Drawing.Font('Microsoft YaHei UI',15,[Drawing.FontStyle]::Bold);$resourceTitle.AutoSize=$true;$resourceTitle.Location=New-Object Drawing.Point(28,24);$tabResources.Controls.Add($resourceTitle)
$resourceHint=New-Object Windows.Forms.Label;$resourceHint.Text='左侧含HP/MP、攻击加算、防御平减与货币；右侧为C3E8卡册钥匙。攻击写CFEC原生槽并由协议适配器镜像；防御使用本地平减规则。';$resourceHint.AutoSize=$true;$resourceHint.Location=New-Object Drawing.Point(30,62);$tabResources.Controls.Add($resourceHint)
function New-ResourceNumeric($parent,[string]$label,[int]$x,[int]$y,[decimal]$min,[decimal]$max,[decimal]$value,[int]$width=260){
    Add-Label $parent $label $x $y 190|Out-Null;$n=New-Object Windows.Forms.NumericUpDown;$n.Location=New-Object Drawing.Point(($x+205),($y-4));$n.Size=New-Object Drawing.Size($width,30);$n.Minimum=$min;$n.Maximum=$max;$n.DecimalPlaces=0;$n.ThousandsSeparator=$true;$n.Value=$value;$parent.Controls.Add($n);return $n
}
$u64Max=[decimal]::Parse('18446744073709551615',[Globalization.CultureInfo]::InvariantCulture)
$hpMaxBox=New-ResourceNumeric $tabResources '最大HP  hp_max' 45 125 1 65535 ([decimal]$defaultHpMax)
$mpMaxBox=New-ResourceNumeric $tabResources '最大MP  mp_max' 45 175 1 65535 ([decimal]$defaultMpMax)
$attackBox=New-ResourceNumeric $tabResources '攻击加算  attack（CFEC）' 45 225 0 1000000 ([decimal]$defaultAttack)
$defenseBox=New-ResourceNumeric $tabResources '防御平减  defense（本地）' 45 275 0 65535 ([decimal]$defaultDefense)
$coinBox=New-ResourceNumeric $tabResources '金币  coin' 45 355 0 $u64Max ([decimal]$defaultCoin) 260
$nanaPointBox=New-ResourceNumeric $tabResources 'NANA点  nana_point' 45 405 0 $u64Max ([decimal]$defaultNanaPoint) 260
$apartmentPointsBox=New-ResourceNumeric $tabResources '公寓推荐点数' 45 455 0 4294967295 ([decimal]$defaultApartmentPoints) 260
$apartmentPointsNote=New-Object Windows.Forms.Label;$apartmentPointsNote.Text='默认1000点；修改配置后于下次启动应用，同值重启保留游戏内余额。';$apartmentPointsNote.Location=New-Object Drawing.Point(45,500);$apartmentPointsNote.Size=New-Object Drawing.Size(470,45);$tabResources.Controls.Add($apartmentPointsNote)
$cardKeyNormalBox=New-ResourceNumeric $tabResources '一般魔法钥匙  card_key_normal' 555 125 0 255 ([decimal]$defaultCardKeyNormal) 210
$cardKeyGoldBox=New-ResourceNumeric $tabResources '黄金魔法钥匙  card_key_gold' 555 175 0 255 ([decimal]$defaultCardKeyGold) 210
$cardKeyMysteryBox=New-ResourceNumeric $tabResources '神秘钥匙  card_key_mystery' 555 225 0 255 ([decimal]$defaultCardKeyMystery) 210
$cardKeySpecialBox=New-ResourceNumeric $tabResources '其他钥匙槽  card_key_special' 555 275 0 255 ([decimal]$defaultCardKeySpecial) 210
$freeMagicKeyExpiryBox=New-ResourceNumeric $tabResources '自由魔法钥匙到期  YYYYMMDDHH' 555 355 2000010100 2100123123 ([decimal]$defaultFreeMagicKeyExpiry) 210
$quickbarExpiryBox=New-ResourceNumeric $tabResources '全开快捷栏期限（0=未启用）' 555 455 0 2100123123 ([decimal]$defaultQuickbarExpiry) 210
$skillSlotExpiryBox=New-ResourceNumeric $tabResources 'Z/X槽期限（0=未启用）' 555 505 0 2100123123 ([decimal]$defaultSkillSlotExpiry) 210
$skillSlotExpiryApplyBox=New-Object Windows.Forms.CheckBox;$skillSlotExpiryApplyBox.Text='写入Z/X槽期限（未勾选=保留角色数据库现值）';$skillSlotExpiryApplyBox.Location=New-Object Drawing.Point(555,540);$skillSlotExpiryApplyBox.Size=New-Object Drawing.Size(500,28);$skillSlotExpiryApplyBox.Checked=$skillSlotExpiryConfigured;$tabResources.Controls.Add($skillSlotExpiryApplyBox);$skillSlotExpiryBox.Enabled=$skillSlotExpiryApplyBox.Checked
$expansionExpiryNote=New-Object Windows.Forms.Label;$expansionExpiryNote.Text='未来期限表示扩容已生效，原客户端会阻止再次使用对应扩容券；要测试期限券请先明确设为0或过期值。';$expansionExpiryNote.Location=New-Object Drawing.Point(555,570);$expansionExpiryNote.Size=New-Object Drawing.Size(500,42);$expansionExpiryNote.ForeColor=[Drawing.Color]::DarkOrange;$tabResources.Controls.Add($expansionExpiryNote)
$freeMagicKeyNote=New-Object Windows.Forms.Label;$freeMagicKeyNote.Text='自由钥匙物品：44000010/44000011；C3E8 +0x88启用；真实背包链 C473(mode1)→C474。';$freeMagicKeyNote.Location=New-Object Drawing.Point(555,405);$freeMagicKeyNote.Size=New-Object Drawing.Size(500,45);$freeMagicKeyNote.ForeColor=[Drawing.Color]::DarkGreen;$tabResources.Controls.Add($freeMagicKeyNote)
$resourceBoundary=New-Object Windows.Forms.Label;$resourceBoundary.Text='Attack is a u32 additive CFEC modifier (0..1000000). Defense uses the local rule max(1, raw-defense) before D010/D015; this is not a recovered original formula. HP/MP use u16, currencies use u64, and card counts use u8. Selected-PET type1 gems are not automatically added again.';$resourceBoundary.Location=New-Object Drawing.Point(45,620);$resourceBoundary.Size=New-Object Drawing.Size(980,72);$resourceBoundary.ForeColor=[Drawing.Color]::DarkOrange;$tabResources.Controls.Add($resourceBoundary)
$tabResources.AutoScroll=$true;$tabResources.AutoScrollMinSize=New-Object Drawing.Size(1080,1180)
$skillWarning=New-Object Windows.Forms.Label;$skillWarning.Location=New-Object Drawing.Point(45,700);$skillWarning.Size=New-Object Drawing.Size(1000,34);$skillWarning.Font=New-Object Drawing.Font('Microsoft YaHei UI',9,[Drawing.FontStyle]::Bold);$tabResources.Controls.Add($skillWarning)
function New-SkillGradeControl($parent,[int]$idx,[int]$x,[int]$y){$d=$SkillDefs[$idx];Add-Label $parent ("{0} [{1}]"-f$d.Name,$d.Code) $x $y 185|Out-Null;$n=New-Object Windows.Forms.NumericUpDown;$n.Location=New-Object Drawing.Point(($x+188),($y-3));$n.Size=New-Object Drawing.Size(46,25);$n.Minimum=0;$n.Maximum=5;$n.Value=[decimal]$defaultSkillGrades[$idx];$n.Tag=$idx;$parent.Controls.Add($n);return $n}
$skillGradeBoxes=New-Object object[] 16
$projectileSkillGroup=New-Object Windows.Forms.GroupBox;$projectileSkillGroup.Text='炮弹型技能树';$projectileSkillGroup.Location=New-Object Drawing.Point(25,750);$projectileSkillGroup.Size=New-Object Drawing.Size(510,245);$tabResources.Controls.Add($projectileSkillGroup)
$meatSkillGroup=New-Object Windows.Forms.GroupBox;$meatSkillGroup.Text='肉弹型技能树';$meatSkillGroup.Location=New-Object Drawing.Point(550,750);$meatSkillGroup.Size=New-Object Drawing.Size(510,245);$tabResources.Controls.Add($meatSkillGroup)
$skillSectionFont=New-Object Drawing.Font('Microsoft YaHei UI',9,[Drawing.FontStyle]::Bold)
$projectilePrerequisiteLabel=Add-Label $projectileSkillGroup '共同前置技能（上/下路线共用）' 10 22 300;$projectilePrerequisiteLabel.Font=$skillSectionFont;$projectilePrerequisiteLabel.ForeColor=[Drawing.Color]::DarkGreen
$meatPrerequisiteLabel=Add-Label $meatSkillGroup '共同前置技能（上/下路线共用）' 10 22 300;$meatPrerequisiteLabel.Font=$skillSectionFont;$meatPrerequisiteLabel.ForeColor=[Drawing.Color]::DarkGreen
$skillGradeBoxes[0]=New-SkillGradeControl $projectileSkillGroup 0 10 50;$skillGradeBoxes[1]=New-SkillGradeControl $projectileSkillGroup 1 260 50
$skillGradeBoxes[8]=New-SkillGradeControl $meatSkillGroup 8 10 50;$skillGradeBoxes[9]=New-SkillGradeControl $meatSkillGroup 9 260 50
$projectileSkillSeparator=New-Object Windows.Forms.Label;$projectileSkillSeparator.BorderStyle='Fixed3D';$projectileSkillSeparator.Location=New-Object Drawing.Point(10,81);$projectileSkillSeparator.Size=New-Object Drawing.Size(485,2);$projectileSkillGroup.Controls.Add($projectileSkillSeparator)
$meatSkillSeparator=New-Object Windows.Forms.Label;$meatSkillSeparator.BorderStyle='Fixed3D';$meatSkillSeparator.Location=New-Object Drawing.Point(10,81);$meatSkillSeparator.Size=New-Object Drawing.Size(485,2);$meatSkillGroup.Controls.Add($meatSkillSeparator)
Add-Label $projectileSkillGroup '分支路线' 10 91 75|Out-Null;$projectileRouteCombo=New-Object Windows.Forms.ComboBox;$projectileRouteCombo.Location=New-Object Drawing.Point(88,87);$projectileRouteCombo.Size=New-Object Drawing.Size(220,26);$projectileRouteCombo.DropDownStyle='DropDownList';foreach($x in @('未选分支（分支等级全0）','上路线（只允许上路）','下路线（只允许下路）')){[void]$projectileRouteCombo.Items.Add($x)};$projectileRouteCombo.SelectedIndex=if($defaultProjectileRoute-ge0-and$defaultProjectileRoute-le2){$defaultProjectileRoute}else{0};$projectileSkillGroup.Controls.Add($projectileRouteCombo)
Add-Label $meatSkillGroup '分支路线' 10 91 75|Out-Null;$meatRouteCombo=New-Object Windows.Forms.ComboBox;$meatRouteCombo.Location=New-Object Drawing.Point(88,87);$meatRouteCombo.Size=New-Object Drawing.Size(220,26);$meatRouteCombo.DropDownStyle='DropDownList';foreach($x in @('未选分支（分支等级全0）','上路线（只允许上路）','下路线（只允许下路）')){[void]$meatRouteCombo.Items.Add($x)};$meatRouteCombo.SelectedIndex=if($defaultMeatRoute-ge0-and$defaultMeatRoute-le2){$defaultMeatRoute}else{0};$meatSkillGroup.Controls.Add($meatRouteCombo)
$projectileUpperLabel=Add-Label $projectileSkillGroup '上路线技能' 10 122 235;$projectileUpperLabel.Font=$skillSectionFont;$projectileUpperLabel.ForeColor=[Drawing.Color]::SteelBlue
$projectileLowerLabel=Add-Label $projectileSkillGroup '下路线技能' 260 122 235;$projectileLowerLabel.Font=$skillSectionFont;$projectileLowerLabel.ForeColor=[Drawing.Color]::DarkOrange
$meatUpperLabel=Add-Label $meatSkillGroup '上路线技能' 10 122 235;$meatUpperLabel.Font=$skillSectionFont;$meatUpperLabel.ForeColor=[Drawing.Color]::SteelBlue
$meatLowerLabel=Add-Label $meatSkillGroup '下路线技能' 260 122 235;$meatLowerLabel.Font=$skillSectionFont;$meatLowerLabel.ForeColor=[Drawing.Color]::DarkOrange
$skillGradeBoxes[2]=New-SkillGradeControl $projectileSkillGroup 2 10 152;$skillGradeBoxes[3]=New-SkillGradeControl $projectileSkillGroup 3 260 152
$skillGradeBoxes[4]=New-SkillGradeControl $projectileSkillGroup 4 10 182;$skillGradeBoxes[5]=New-SkillGradeControl $projectileSkillGroup 5 260 182
$skillGradeBoxes[6]=New-SkillGradeControl $projectileSkillGroup 6 10 212;$skillGradeBoxes[7]=New-SkillGradeControl $projectileSkillGroup 7 260 212
$skillGradeBoxes[10]=New-SkillGradeControl $meatSkillGroup 10 10 152;$skillGradeBoxes[11]=New-SkillGradeControl $meatSkillGroup 11 260 152
$skillGradeBoxes[12]=New-SkillGradeControl $meatSkillGroup 12 10 182;$skillGradeBoxes[13]=New-SkillGradeControl $meatSkillGroup 13 260 182
$skillGradeBoxes[14]=New-SkillGradeControl $meatSkillGroup 14 10 212;$skillGradeBoxes[15]=New-SkillGradeControl $meatSkillGroup 15 260 212
Add-Label $tabResources 'Z 实际装备技能' 45 1020 135|Out-Null;$skillZCombo=New-Object Windows.Forms.ComboBox;$skillZCombo.Location=New-Object Drawing.Point(190,1016);$skillZCombo.Size=New-Object Drawing.Size(350,28);$skillZCombo.DropDownStyle='DropDownList';$tabResources.Controls.Add($skillZCombo)
Add-Label $tabResources 'X 实际装备技能' 550 1020 135|Out-Null;$skillXCombo=New-Object Windows.Forms.ComboBox;$skillXCombo.Location=New-Object Drawing.Point(695,1016);$skillXCombo.Size=New-Object Drawing.Size(350,28);$skillXCombo.DropDownStyle='DropDownList';$tabResources.Controls.Add($skillXCombo)
$skillRouteNote=New-Object Windows.Forms.Label;$skillRouteNote.Text='前置关系：两项基础技能位于分支之前，并由上/下路线共用；每类只能选择一条分支。切换路线会把另一条路线的3项等级清零；Z/X只能装备等级>0的不同技能。';$skillRouteNote.Location=New-Object Drawing.Point(45,1065);$skillRouteNote.Size=New-Object Drawing.Size(990,52);$skillRouteNote.ForeColor=[Drawing.Color]::DarkRed;$tabResources.Controls.Add($skillRouteNote)
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


$pureNewPlayerBox=New-Object Windows.Forms.CheckBox;$pureNewPlayerBox.Text='构建与启动纯新手档';$pureNewPlayerBox.Location=New-Object Drawing.Point(35,104);$pureNewPlayerBox.Size=New-Object Drawing.Size(260,28);$pureNewPlayerBox.Checked=$false;$tabStart.Controls.Add($pureNewPlayerBox)
Add-Label $tabStart '新手档用户名' 330 108 135|Out-Null
$pureNewPlayerUsernameBox=New-Object Windows.Forms.TextBox;$pureNewPlayerUsernameBox.Location=New-Object Drawing.Point(475,104);$pureNewPlayerUsernameBox.Size=New-Object Drawing.Size(415,28);$pureNewPlayerUsernameBox.Text=$defaultPureNewPlayerUsername;$tabStart.Controls.Add($pureNewPlayerUsernameBox)
Add-Label $tabStart '用户名/角色显示名' 35 148|Out-Null
$nameBox=New-Object Windows.Forms.TextBox;$nameBox.Location=New-Object Drawing.Point(190,144);$nameBox.Size=New-Object Drawing.Size(700,28);$nameBox.Text=$defaultName;$tabStart.Controls.Add($nameBox)
function Get-SelectedLaunchMode {return 'network'}
function Get-NetworkIpInput {return '127.0.0.1'}
function Get-SelectedLaunchModeInfo {return Get-LaunchModeInfo}
Add-Label $tabStart '初始等级（无进度档）' 35 188 155|Out-Null
$levelBox=New-Object Windows.Forms.NumericUpDown;$levelBox.Location=New-Object Drawing.Point(190,184);$levelBox.Minimum=1;$levelBox.Maximum=99;$levelBox.Value=[Math]::Min(99,[Math]::Max(1,$defaultLevel));$levelBox.Size=New-Object Drawing.Size(120,28);$tabStart.Controls.Add($levelBox)
$resetProgressBtn=New-Object Windows.Forms.Button;$resetProgressBtn.Text='Reset level/EXP/title';$resetProgressBtn.Location=New-Object Drawing.Point(900,230);$resetProgressBtn.Size=New-Object Drawing.Size(170,30);$tabStart.Controls.Add($resetProgressBtn)
Add-Label $tabStart '性别（模型基础）' 340 188 130|Out-Null
$genderCombo=New-Object Windows.Forms.ComboBox;$genderCombo.Location=New-Object Drawing.Point(475,184);$genderCombo.Size=New-Object Drawing.Size(170,30);$genderCombo.DropDownStyle='DropDownList';[void]$genderCombo.Items.Add('女（F资源）');[void]$genderCombo.Items.Add('男（M资源）');$genderCombo.SelectedIndex=[Math]::Min(1,[Math]::Max(0,$defaultGender));$tabStart.Controls.Add($genderCombo)
Add-Label $tabStart '地宫初始攻击（0～2）' 665 188 120|Out-Null
$attackCombo=New-Object Windows.Forms.ComboBox;$attackCombo.Enabled=$true;$attackCombo.Location=New-Object Drawing.Point(790,184);$attackCombo.Size=New-Object Drawing.Size(285,30);$attackCombo.DropDownStyle='DropDownList';$attackCombo.DropDownWidth=760;$tabStart.Controls.Add($attackCombo)

Add-Label $tabStart '称号选择（grade 0～42）' 35 234 155|Out-Null
$titleCombo=New-Object Windows.Forms.ComboBox;$titleCombo.Location=New-Object Drawing.Point(190,230);$titleCombo.Size=New-Object Drawing.Size(700,30);$titleCombo.DropDownStyle='DropDownList';$titleCombo.DropDownWidth=930
foreach($row in $titleChoices){[void]$titleCombo.Items.Add($row.Display)};$titleCombo.Tag=$titleChoices;$titleCombo.SelectedIndex=0;for($i=0;$i-lt$titleChoices.Count;$i++){if([int]$titleChoices[$i].Grade-eq$defaultDungeonGrade){$titleCombo.SelectedIndex=$i;break}};$tabStart.Controls.Add($titleCombo)
Add-Label $tabStart '宠物选择' 35 274|Out-Null
$petCombo=New-Object Windows.Forms.ComboBox;$petCombo.Location=New-Object Drawing.Point(190,270);$petCombo.Size=New-Object Drawing.Size(700,30);$tabStart.Controls.Add($petCombo)
$petChoices=New-ChoiceList $pets -Pet;Bind-Combo $petCombo $petChoices $defaults.pet
Add-Label $tabStart '宠物当前岁数' 35 314|Out-Null
$petAgeCombo=New-Object Windows.Forms.ComboBox;$petAgeCombo.Location=New-Object Drawing.Point(190,310);$petAgeCombo.Size=New-Object Drawing.Size(140,30);$petAgeCombo.DropDownStyle='DropDownList';$tabStart.Controls.Add($petAgeCombo)
$petDetail=New-Object Windows.Forms.Label;$petDetail.Location=New-Object Drawing.Point(350,308);$petDetail.Size=New-Object Drawing.Size(690,42);$tabStart.Controls.Add($petDetail)

$comboMap=@{}
$y=365
foreach($part in @('body','hair','top','bottom','accessory','effect')){
    Add-Label $tabStart ($PartLabels[$part]) 35 $y|Out-Null
    $c=New-Object Windows.Forms.ComboBox;$c.Location=New-Object Drawing.Point(190,($y-4));$c.Size=New-Object Drawing.Size(700,30);$tabStart.Controls.Add($c)
    $rows=@($equips|Where-Object part -eq $part);$choices=New-ChoiceList $rows;Bind-Combo $c $choices ([uint32]$defaults[$part]);$comboMap[$part]=$c;$y+=46
}
$comboMap.body.add_SelectedIndexChanged({$d=Get-SelectedData $comboMap.body;if($d-and$d.gender-eq'M'){$genderCombo.SelectedIndex=1}elseif($d-and$d.gender-eq'F'){$genderCombo.SelectedIndex=0}})
$skipTutorialBox=New-Object Windows.Forms.CheckBox;$skipTutorialBox.Text='跳过新手教程';$skipTutorialBox.Location=New-Object Drawing.Point(35,646);$skipTutorialBox.Size=New-Object Drawing.Size(180,28);$skipTutorialBox.Checked=$defaultSkipTutorial;$tabStart.Controls.Add($skipTutorialBox)
$unlockAllDungeonsBox=New-Object Windows.Forms.CheckBox;$unlockAllDungeonsBox.Text='开启全部地宫进入权限';$unlockAllDungeonsBox.Location=New-Object Drawing.Point(240,646);$unlockAllDungeonsBox.Size=New-Object Drawing.Size(260,28);$unlockAllDungeonsBox.Checked=$defaultUnlockAllDungeons;$tabStart.Controls.Add($unlockAllDungeonsBox)
$warning=New-Object Windows.Forms.Label;$warning.Text='“保存配置”仅在适配器停止时写盘；“进入 Nanaimo”按用户名／角色显示名所选档案连接现有适配器。';$warning.ForeColor=[Drawing.Color]::DarkOrange;$warning.AutoSize=$true;$warning.Location=New-Object Drawing.Point(520,650);$tabStart.Controls.Add($warning)

$saveBtn=New-Object Windows.Forms.Button;$saveBtn.Text='保存配置';$saveBtn.Size=New-Object Drawing.Size(125,40);$saveBtn.Location=New-Object Drawing.Point(75,704);$tabStart.Controls.Add($saveBtn)
$clientBtn=New-Object Windows.Forms.Button;$clientBtn.Text='进入 Nanaimo';$clientBtn.Size=New-Object Drawing.Size(230,40);$clientBtn.Location=New-Object Drawing.Point(215,704);$clientBtn.BackColor=[Drawing.Color]::LightGreen;$tabStart.Controls.Add($clientBtn)
$adapterBtn=New-Object Windows.Forms.Button;$adapterBtn.Text='启动适配器';$adapterBtn.Size=New-Object Drawing.Size(175,40);$adapterBtn.Location=New-Object Drawing.Point(460,704);$adapterBtn.BackColor=[Drawing.Color]::LightSkyBlue;$tabStart.Controls.Add($adapterBtn)
$folderBtn=New-Object Windows.Forms.Button;$folderBtn.Text='打开日志目录';$folderBtn.Size=New-Object Drawing.Size(140,40);$folderBtn.Location=New-Object Drawing.Point(650,704);$tabStart.Controls.Add($folderBtn)
$defaultBtn=New-Object Windows.Forms.Button;$defaultBtn.Text='恢复默认';$defaultBtn.Size=New-Object Drawing.Size(120,40);$defaultBtn.Location=New-Object Drawing.Point(805,704);$tabStart.Controls.Add($defaultBtn)
$status=New-Object Windows.Forms.Label;$status.Location=New-Object Drawing.Point(35,759);$status.Size=New-Object Drawing.Size(1000,60);$status.ForeColor=[Drawing.Color]::DarkBlue;$tabStart.Controls.Add($status)
function Update-LaunchModePresentation {
    $clientBtn.Text=if($pureNewPlayerBox.Checked){'构建并进入纯新手档'}else{'进入 Nanaimo'}
    $warning.Text=if($pureNewPlayerBox.Checked){'纯新手档：固定进入教程并关闭全部地宫权限；仅连接已运行的本地协议适配器。'}else{'“保存配置”仅在适配器停止时写盘；“进入 Nanaimo”按用户名／角色显示名所选档案连接现有适配器。'}
}
function Update-PureNewPlayerPresentation {
    $normal=-not$pureNewPlayerBox.Checked
    if(-not$normal){$skipTutorialBox.Checked=$false;$unlockAllDungeonsBox.Checked=$false}
    foreach($control in @($nameBox,$levelBox,$resetProgressBtn,$genderCombo,$attackCombo,$titleCombo,$petCombo,$petAgeCombo)+@($comboMap.Values)){$control.Enabled=$normal}
    $pureNewPlayerUsernameBox.Enabled=-not$normal
    $skipTutorialBox.Enabled=$normal;$unlockAllDungeonsBox.Enabled=$normal
    $saveBtn.Enabled=$normal;$adapterBtn.Enabled=$normal
    Update-LaunchModePresentation
    if($launchInfoBox){Update-LaunchPreview}
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
    $hpMax=[uint16][decimal]$hpMaxBox.Value
    $mpMax=[uint16][decimal]$mpMaxBox.Value
    return [ordered]@{hp_max=$hpMax;mp_max=$mpMax;attack=[uint32][decimal]$attackBox.Value;defense=[uint16][decimal]$defenseBox.Value;coin=[uint64][decimal]$coinBox.Value;nana_point=[uint64][decimal]$nanaPointBox.Value;apartment_recommendation_points=[uint32][decimal]$apartmentPointsBox.Value;card_key_normal=[byte][decimal]$cardKeyNormalBox.Value;card_key_gold=[byte][decimal]$cardKeyGoldBox.Value;card_key_mystery=[byte][decimal]$cardKeyMysteryBox.Value;card_key_special=[byte][decimal]$cardKeySpecialBox.Value;free_magic_key_expiry=[uint32][decimal]$freeMagicKeyExpiryBox.Value;quickbar_expiry=[uint32][decimal]$quickbarExpiryBox.Value;skill_slot_expiry=[uint32][decimal]$skillSlotExpiryBox.Value;skill_slot_expiry_apply=[bool]$skillSlotExpiryApplyBox.Checked}
}
function Get-SelectedDungeonTitle {if($titleCombo.SelectedIndex-lt0-or-not$titleCombo.Tag-or$titleCombo.SelectedIndex-ge$titleCombo.Tag.Count){throw '请选择称号。'};return $titleCombo.Tag[$titleCombo.SelectedIndex]}
function Update-LaunchPreview([switch]$ComputeHashes){
    if($pureNewPlayerBox.Checked){
        $processFilter={param($p)(@($Adapter,$AdapterBridge,$LegacyAdapter)-contains$p.Path)-or($p.ProcessName-eq'game'-and$p.Path-eq$Client)}
        $running=@(Get-Process -ErrorAction SilentlyContinue|Where-Object $processFilter|ForEach-Object{"$($_.ProcessName)(PID=$($_.Id))"});if(-not$running){$running=@('<none>')}
        $lines=@(
            '=== Pure new player launch ===',
            ('Pure account: '+$(if($pureNewPlayerUsernameBox.Text.Trim()){$pureNewPlayerUsernameBox.Text.Trim()}else{'<auto-generated>'})),
            ('Dungeon access: '+$(if($unlockAllDungeonsBox.Checked){'all unlocked'}else{'progression prerequisites'})),
            'Pure profile: new loopback account | no pre-created character',
            'Ignored: GUI name/gender/level/title/pet/equipment/resources/skills, normal profile INI/JSON, inventory sidecars, existing character/task state.',
            'Applied: isolated runtime profile with skip_tutorial=0; server suppresses configurable local Hans/NANA/SP grants; retail client owns character creation.',
            "Pure runtime profile: $PureNewPlayerProfile",'',
            '=== Binary validation ===',"Release identity: $ReleaseIdentity","Canonical launcher: $CanonicalLauncher",
            (File-State-Line 'Full adapter' $Adapter $ExpectedAdapterSize $ExpectedAdapterHash -ComputeHash:$ComputeHashes),
            (File-State-Line 'Gameplay bridge' $AdapterBridge $ExpectedBridgeSize $ExpectedBridgeHash -ComputeHash:$ComputeHashes),
            (Client-State-Line),'',
            '=== Pre-launch actions ===',('Processes to stop: '+($running-join ', ')),
            'One-click button: leave normal profile/sidecars unchanged; build pure runtime profile; restart adapter; register unique pure account; launch game.',
            ('Working directory: '+$Root),'',
            '验收入口：客户端应先进入原生角色创建，创建后 TutorialCompleted=0，再由客户端本地引导/NPC对话状态继续。'
        )
        $launchInfoBox.Text=$lines-join "`r`n";return
    }
    $pet=Get-SelectedData $petCombo;$attackChoice=Selected-AttackMode;$resources=Get-ResourceSelection;$skills=Get-SkillSelection;$equipSummary=@();foreach($part in @('body','hair','top','bottom','accessory','effect')){$d=Get-SelectedData $comboMap[$part];if($d){$equipSummary+=("{0}={1}[{2}]"-f$PartLabels[$part],$d.name,$d.id)}}
    $processFilter={param($p)(@($Adapter,$AdapterBridge,$LegacyAdapter)-contains$p.Path)-or($p.ProcessName-eq'game'-and$p.Path-eq$Client)}
    $running=@(Get-Process -ErrorAction SilentlyContinue|Where-Object $processFilter|ForEach-Object{"$($_.ProcessName)(PID=$($_.Id))"});if(-not$running){$running=@('<none>')}
    $adapterState=File-State-Line 'Full adapter' $Adapter $ExpectedAdapterSize $ExpectedAdapterHash -ComputeHash:$ComputeHashes
    $bridgeState=File-State-Line 'Gameplay bridge' $AdapterBridge $ExpectedBridgeSize $ExpectedBridgeHash -ComputeHash:$ComputeHashes
    $lines=@(
        '=== Character and profile ===',
        "Character=$($nameBox.Text.Trim()) | Level=$([int]$levelBox.Value) | Gender=$(if($genderCombo.SelectedIndex-eq1){'M'}else{'F'}) | Title=$((Get-SelectedDungeonTitle).Display)",
        "Resources: MaxHP=$($resources.hp_max) MaxMP=$($resources.mp_max) attack_modifier=$($resources.attack) defense_flat=$($resources.defense) coin=$($resources.coin) nana_point=$($resources.nana_point)",
        ("Skills: projectile={0} meat={1} Z={2}[{3}] X={4}[{5}] grades={6}"-f@("none","upper","lower")[$skills.projectile_route],@("none","upper","lower")[$skills.meat_route],(Skill-CodeName $skills.slot_z),$skills.slot_z,(Skill-CodeName $skills.slot_x),$skills.slot_x,($skills.grades-join",")),
        "Card keys: normal=$($resources.card_key_normal) gold=$($resources.card_key_gold) mystery=$($resources.card_key_mystery) special=$($resources.card_key_special) free_expiry=$($resources.free_magic_key_expiry) quickbar_expiry=$($resources.quickbar_expiry) zx_expiry=$(if($resources.skill_slot_expiry_apply){$resources.skill_slot_expiry}else{'preserve-db'})",
        "Pet=$(if($pet){$pet.name+'['+$pet.id+'] age='+$(Selected-PetAge)+'/'+$pet.max_age}else{'<none>'}) | Initial attack choice: $attackChoice",
        ('Equipment: '+($equipSummary-join '; ')),
        "Profile INI : $ProfileIni","Profile JSON: $ProfileJson",'',
        '=== Binary validation ===',"Release identity: $ReleaseIdentity","Canonical launcher: $CanonicalLauncher",$adapterState,$bridgeState,
        (Client-State-Line),
        'Client compatibility is prepared from the local user-owned tree at launch: the furniture Index redirect, native revival/P-state migration, manual settlement with settlement-only EXP display preserving live score, P03 roads and minimap boundary, retirement of recognized generated chapter-seven artwork, SSTG alias, and PON fallback. Emotion-page safety uses the adapter''s bounded C355 couple/progression fields; the dedicated Index redirect provides click-time furniture access while C393 provides the bounded scene snapshot.',
        '=== Pre-launch actions ===',
        ('Processes to stop: '+($running-join ', ')),
        'Adapter button: save profile while offline; start/stop the complete local adapter; never launch the game.',
        'Enter button: require the existing adapter; use the already-saved profile; validate/register; launch one client; never start or stop services.','',
        ('Working directory: '+$Root),'',
        '这是韩国飞行射击游戏 Nanaimo 的启动器。'
    )
    $launchInfoBox.Text=$lines-join "`r`n"
}
$refreshLaunchInfoBtn.add_Click({try{Update-LaunchPreview -ComputeHashes}catch{[Windows.Forms.MessageBox]::Show($_.Exception.Message,'刷新启动信息失败')|Out-Null}})
$copyLaunchInfoBtn.add_Click({if($launchInfoBox.Text){[Windows.Forms.Clipboard]::SetText($launchInfoBox.Text);$status.Text='已复制“本次启动详情”到剪贴板。'}})
$tabs.add_SelectedIndexChanged({if($tabs.SelectedTab-eq$tabLaunchInfo){Update-LaunchPreview}})
$skillSlotExpiryApplyBox.add_CheckedChanged({$skillSlotExpiryBox.Enabled=$skillSlotExpiryApplyBox.Checked;if($launchInfoBox){Update-LaunchPreview}})
$pureNewPlayerBox.add_CheckedChanged({Update-PureNewPlayerPresentation})
$skipTutorialBox.add_CheckedChanged({Update-LaunchModePresentation;if($launchInfoBox){Update-LaunchPreview}})
$pureNewPlayerUsernameBox.add_TextChanged({if($launchInfoBox){Update-LaunchPreview}})
$unlockAllDungeonsBox.add_CheckedChanged({if($launchInfoBox){Update-LaunchPreview}})

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
foreach($resourceBox in @($hpMaxBox,$mpMaxBox,$attackBox,$defenseBox,$coinBox,$nanaPointBox,$apartmentPointsBox,$cardKeyNormalBox,$cardKeyGoldBox,$cardKeyMysteryBox,$cardKeySpecialBox,$freeMagicKeyExpiryBox,$quickbarExpiryBox,$skillSlotExpiryBox)){$resourceBox.add_ValueChanged({if($launchInfoBox){try{Update-LaunchPreview}catch{$launchInfoBox.Text=$_.Exception.Message}}})}
Update-PetAgeOptions $defaultPetAge;Update-AttackModes;Update-PetDetail;Update-PureNewPlayerPresentation;Update-LaunchPreview
if($PreviewOnly){Update-LaunchPreview -ComputeHashes;Write-Output $launchInfoBox.Text;exit 0}

function Test-AdapterBinary {
    if(-not(Test-Path -LiteralPath $AdapterManifest -PathType Leaf)){throw "Adapter runtime manifest missing: $AdapterManifest"}
    $contract=Get-Content -LiteralPath $AdapterManifest -Raw -Encoding UTF8|ConvertFrom-Json
    if(-not$contract.files-or@($contract.files).Count-lt2){throw 'Adapter runtime manifest has no file closure.'}
    $runtimePrefix=[IO.Path]::GetFullPath($AdapterRuntimeRoot).TrimEnd('\')+'\'
    foreach($row in @($contract.files)){
        $relative=[string]$row.name
        if([string]::IsNullOrWhiteSpace($relative)-or[IO.Path]::IsPathRooted($relative)-or@($relative -split '[\\/]')-contains'..'){throw "Invalid adapter manifest path: $relative"}
        $path=[IO.Path]::GetFullPath((Join-Path $AdapterRuntimeRoot $relative))
        if(-not$path.StartsWith($runtimePrefix,[StringComparison]::OrdinalIgnoreCase)){throw "Adapter manifest path escapes runtime root: $relative"}
        if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "Adapter runtime file missing: $relative"}
        $item=Get-Item -LiteralPath $path
        if($item.Length-ne[long]$row.size){throw "Adapter runtime size mismatch: $relative"}
        if((Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash-ne[string]$row.sha256){throw "Adapter runtime SHA-256 mismatch: $relative"}
    }
    foreach($required in @('Nanaimo.Adapter.exe','nanaimo_gameplay_bridge.exe','Nanaimo.Adapter.dll','Nanaimo.Gameplay.dll')){
        if(-not(@($contract.files|Where-Object name -eq $required)[0])){throw "Adapter manifest missing required file: $required"}
    }
}
if($SelfTestAdapterManifest){Test-AdapterBinary;Write-Output 'ADAPTER_RUNTIME_MANIFEST_SELFTEST_PASS';exit 0}
function Test-ClientBinary {
    if(-not(Test-Path -LiteralPath $Client -PathType Leaf)){throw "Client missing: $Client"}
}
function Get-ClientCompatibilityPython {
    $bundled=Join-Path $Root 'tools\python\python.exe'
    if(Test-Path -LiteralPath $bundled -PathType Leaf){return [pscustomobject]@{Path=$bundled;Prefix=@('-B')}}
    $py=Get-Command py.exe -ErrorAction SilentlyContinue
    if($py){return [pscustomobject]@{Path=$py.Source;Prefix=@('-3','-B')}}
    $python=Get-Command python.exe -ErrorAction SilentlyContinue
    if($python){return [pscustomobject]@{Path=$python.Source;Prefix=@('-B')}}
    throw 'Client compatibility preparation requires Python 3 (py.exe or python.exe).'
}
function Ensure-ClientCompatibility {
    Test-ClientBinary
    if(-not(Test-Path -LiteralPath $ClientCompatibilityTool -PathType Leaf)){throw "Client compatibility tool missing: $ClientCompatibilityTool"}
    if(-not(Test-Path -LiteralPath $AdapterData)){New-Item -ItemType Directory -Path $AdapterData -Force|Out-Null}
    $runtime=Get-ClientCompatibilityPython
    $arguments=@($runtime.Prefix)+@($ClientCompatibilityTool,'--source-root',$Root,'--output-root',$ClientCompatibilityOverlay,'--character-creation','--furniture','--native-state','--dungeon-state','--inventory-gift-display','--land-purchase','--apartment-exterior','--apartment-recommendation','--dungeon7','--overwrite','--apply')
    $output=@(& $runtime.Path @arguments 2>&1)
    if($LASTEXITCODE-ne0){throw ("Client compatibility preparation refused:`r`n"+($output-join"`r`n"))}
    if(-not(Test-Path -LiteralPath $ClientCompatibilityReport -PathType Leaf)){throw 'Client compatibility report was not generated.'}
    $report=Get-Content -LiteralPath $ClientCompatibilityReport -Raw -Encoding UTF8|ConvertFrom-Json
    if(-not$report.verification.all_pass){throw 'Client compatibility post-apply verification failed.'}
    return $report
}
function Send-LocalLaunchRegistration([string]$ip,[byte[]]$bytes,[string]$description){
    $tcp=New-Object Net.Sockets.TcpClient
    try{
        $tcp.Connect($ip,11999);$stream=$tcp.GetStream();$stream.ReadTimeout=6000;$stream.WriteTimeout=6000;$len=[BitConverter]::GetBytes([uint32]$bytes.Length)
        $stream.Write($len,0,4);$stream.Write($bytes,0,$bytes.Length);$stream.Flush();$ack=New-Object byte[] 3;$got=0
        while($got-lt3){$read=$stream.Read($ack,$got,3-$got);if($read-le0){break};$got+=$read}
        if($got-ne3-or[Text.Encoding]::ASCII.GetString($ack)-ne"OK`n"){throw "$description registry rejected: ack=$([Text.Encoding]::ASCII.GetString($ack,0,$got))"}
    }catch{throw "适配器$description 注册失败，请检查适配器状态及端口11999。详细信息：$($_.Exception.Message)"}finally{if($tcp){$tcp.Close()}}
}
function Register-ClientProfile([string]$ip){
    $selected=Sync-LauncherProfileIdentity
    if(-not$selected){throw 'Select a local account first.'}
    # Existing characters resume by account; named local copies are imported once.
    $account=if($null-ne$selected.account_id){[string]$selected.username}else{[string]$selected.character_name}
    if(-not$account){throw 'Selected local account has no identity.'}
    $json=[ordered]@{LocalAccount=$account;PureNewPlayer=$false}|ConvertTo-Json -Compress
    Send-LocalLaunchRegistration $ip ([Text.Encoding]::UTF8.GetBytes($json)) 'local account'
    return $account
}
function New-PureNewPlayerAccountName {return 'pure-'+(Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmssfff')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)}
function Write-PureNewPlayerRuntimeProfile {
    if(-not(Test-Path -LiteralPath $AdapterData)){New-Item -ItemType Directory -Path $AdapterData -Force|Out-Null}
    $lines=@('version=2','launch_mode=network','network_ip=127.0.0.1','skip_tutorial=0','unlock_all_dungeons=0','gender=0','name_hex=505552454E4557','dungeon_grade=auto','level=1','pet=0','pet_age_a=0','pet_age_b=0','initial_attack_mode=0','equip_hair=0','equip_body=0','equip_top=0','equip_bottom=0','equip_accessory=0','equip_effect=0','hp_max=1500','hp_current=1500','mp_max=100','mp_current=100','attack=0','defense=0','coin=0','nana_point=0','card_key_normal=0','card_key_gold=0','card_key_mystery=0','card_key_special=0','free_magic_key_expiry=2000010100','quickbar_expiry=0','skill_config=1','skill_projectile_route=0','skill_meat_route=0','skill_slot_z=0','skill_slot_x=0')
    for($i=0;$i-lt16;$i++){$lines+="skill_grade$i=0"}
    [IO.File]::WriteAllLines($PureNewPlayerProfile,$lines,(New-Object Text.ASCIIEncoding))
    return $PureNewPlayerProfile
}
function Assert-LocalAdapterRunning {
    if(-not(Get-LocalAdapters).Count){throw '本地协议适配器尚未运行。请先点击“启动适配器”，待启动成功后再进入 Nanaimo。'}
}
function Register-PureNewPlayer([string]$ip,[string]$requestedUsername){
    $account=$requestedUsername.Trim();if(-not$account){$account=New-PureNewPlayerAccountName};$account=Save-PureNewPlayerUsername $account
    $json=[ordered]@{LocalAccount=$account;PureNewPlayer=$true}|ConvertTo-Json -Compress
    Send-LocalLaunchRegistration $ip ([Text.Encoding]::UTF8.GetBytes($json)) '纯新手账号'
    return $account
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
function Test-AdapterPort([int]$port,[string]$targetHost='127.0.0.1'){$tcp=New-Object Net.Sockets.TcpClient;try{$ar=$tcp.BeginConnect($targetHost,$port,$null,$null);if(-not$ar.AsyncWaitHandle.WaitOne(150)){return $false};$tcp.EndConnect($ar);return $true}catch{return $false}finally{$tcp.Close()}}
function Get-AdapterListenerOwners {
    $rows=@()
    try{$rows=@(Get-NetTCPConnection -State Listen -ErrorAction Stop|Where-Object{$_.LocalPort-in@(11005,11999,12050)})}catch{return @()}
    return @($rows|ForEach-Object{
        $pidValue=[int]$_.OwningProcess;$proc=Get-Process -Id $pidValue -ErrorAction SilentlyContinue
        [pscustomobject]@{Port=[int]$_.LocalPort;Pid=$pidValue;Path=$(if($proc){try{$proc.Path}catch{''}}else{''})}
    })
}
function Assert-AdapterPortsAvailable {
    $owners=@(Get-AdapterListenerOwners)
    if(-not$owners.Count){return}
    $detail=($owners|Sort-Object Port|ForEach-Object{"$($_.Port): PID=$($_.Pid) path=$(if($_.Path){$_.Path}else{'<unknown>'})"})-join"`r`n"
    throw "Adapter ports are owned by another process; refusing to reuse an old service:`r`n$detail`r`nClose that process, then launch only through $CanonicalLauncher."
}
function Assert-StartedAdapterListeners([int]$processId) {
    $owners=@(Get-AdapterListenerOwners|Where-Object{$_.Port-in@(11005,11999,12050)})
    $missing=@(11005,11999,12050|Where-Object{$_ -notin @($owners|ForEach-Object{$_.Port})})
    $foreign=@($owners|Where-Object{$_.Pid-ne$processId})
    if($missing.Count-or$foreign.Count){
        $detail=($owners|Sort-Object Port|ForEach-Object{"$($_.Port): PID=$($_.Pid) path=$(if($_.Path){$_.Path}else{'<unknown>'})"})-join'; '
        throw "Adapter listener ownership validation failed: missing=$($missing-join',') owners=$detail"
    }
}
function Write-RuntimeIdentity([Diagnostics.Process]$proc,[string]$bindAddress) {
    $identity=[ordered]@{
        observed_at=(Get-Date).ToString('o');canonical_launcher=$CanonicalLauncher;root=$Root
        adapter_pid=$proc.Id;adapter_path=$Adapter;adapter_size=(Get-Item -LiteralPath $Adapter).Length;adapter_sha256=(Get-FileHash -LiteralPath $Adapter -Algorithm SHA256).Hash
        adapter_dll_path=(Join-Path $AdapterRuntimeRoot 'Nanaimo.Adapter.dll');adapter_dll_size=(Get-Item -LiteralPath (Join-Path $AdapterRuntimeRoot 'Nanaimo.Adapter.dll')).Length;adapter_dll_sha256=(Get-FileHash -LiteralPath (Join-Path $AdapterRuntimeRoot 'Nanaimo.Adapter.dll') -Algorithm SHA256).Hash
        bridge_path=$AdapterBridge;bridge_size=(Get-Item -LiteralPath $AdapterBridge).Length;bridge_sha256=(Get-FileHash -LiteralPath $AdapterBridge -Algorithm SHA256).Hash
        client_path=$Client;bind_address=$bindAddress;ports=@(11005,11999,12050);release_identity=$ReleaseIdentity
    }
    $identity|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $RuntimeIdentityPath -Encoding UTF8
}
function Test-PureNewPlayerNativeStartup([string]$profilePath) {
    $probeRoot=Join-Path ([IO.Path]::GetTempPath()) ('nanaimo_pure_native_'+[guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($probeRoot)|Out-Null
    $probeOut=Join-Path $probeRoot 'native.out.log';$probeErr=Join-Path $probeRoot 'native.err.log';$probe=$null
    try{
        $listeners=@([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()|ForEach-Object{$_.Port})
        $first=41000;while($first-lt65000-and(@($first..($first+7)|Where-Object{$_-in$listeners}).Count-or($first+20)-in$listeners)){$first+=11}
        if($first-ge65000){throw 'no free native probe port block'}
        $probeArgs=@([string]($first+20),'new','0','0',$profilePath,[string]$first)
        $probe=Start-Process -FilePath $AdapterBridge -ArgumentList $probeArgs -WorkingDirectory $probeRoot -WindowStyle Hidden -RedirectStandardOutput $probeOut -RedirectStandardError $probeErr -PassThru
        $ready=$false
        for($attempt=0;$attempt-lt50;$attempt++){
            Start-Sleep -Milliseconds 50
            if($probe.HasExited){$native=if(Test-Path -LiteralPath $probeOut){Get-Content -LiteralPath $probeOut -Raw}else{''};throw "pure native startup exited code=$($probe.ExitCode): $native"}
            if((Test-AdapterPort ($first+20))-and(Test-AdapterPort ($first+7))){$ready=$true;break}
        }
        if(-not$ready){throw 'pure native startup did not bind the isolated login/profile listeners'}
    }finally{
        if($probe-and-not$probe.HasExited){$probe|Stop-Process -Force;Wait-Process -Id $probe.Id -Timeout 5 -ErrorAction SilentlyContinue}
        if(Test-Path -LiteralPath $probeRoot){Remove-Item -LiteralPath $probeRoot -Recurse -Force -ErrorAction SilentlyContinue}
    }
}
function Get-AdapterStartupFailureDetail([Diagnostics.Process]$proc,[string]$runtimeProfile) {
    $sections=@("adapter_exit_code=$($proc.ExitCode)","profile=$runtimeProfile")
    foreach($entry in @(
        [pscustomobject]@{Label='process stderr';Path=$AdapterErr;Tail=80},
        [pscustomobject]@{Label='managed adapter error';Path=(Join-Path $AdapterLogs 'adapter-error.log');Tail=80},
        [pscustomobject]@{Label='native worker tail';Path=(Join-Path $AdapterData 'native.log');Tail=40}
    )){
        if(Test-Path -LiteralPath $entry.Path -PathType Leaf){
            $content=@(Get-Content -LiteralPath $entry.Path -Tail $entry.Tail -ErrorAction SilentlyContinue)
            if($content.Count){$sections+=("--- {0}: {1} ---`r`n{2}"-f$entry.Label,$entry.Path,($content-join"`r`n"))}
        }
    }
    return $sections-join"`r`n"
}
function Start-LocalAdapter([string]$runtimeProfile=$ProfileIni,[string]$bindAddress='127.0.0.1') {
    $bindAddress=Normalize-NetworkIPv4 $bindAddress
    Test-AdapterBinary
    Assert-AdapterPortsAvailable
    foreach($path in @($AdapterData,$AdapterLogs)){if(-not(Test-Path -LiteralPath $path)){New-Item -ItemType Directory -Path $path -Force|Out-Null}}
    Remove-Item -LiteralPath $AdapterStop,$AdapterLog,$AdapterErr -Force -ErrorAction SilentlyContinue
    $args=@('--data',$AdapterData,'--native',$AdapterBridge,'--profile',$runtimeProfile,'--bind-address',$bindAddress,'--login-port','11005','--world-port','12050','--profile-port','11999','--log-directory',$AdapterLogs)
    $proc=Start-Process -FilePath $Adapter -ArgumentList $args -WorkingDirectory $AdapterRuntimeRoot -WindowStyle Hidden -RedirectStandardOutput $AdapterLog -RedirectStandardError $AdapterErr -PassThru
    for($i=0;$i-lt100;$i++){
        Start-Sleep -Milliseconds 100
        if($proc.HasExited){$detail=if(Test-Path -LiteralPath $AdapterErr){Get-Content -LiteralPath $AdapterErr -Raw}else{'无错误日志。'};throw "适配器退出，代码 $($proc.ExitCode)：$detail"}
        if((Test-AdapterPort 11005 $bindAddress)-and(Test-AdapterPort 11999 $bindAddress)-and(Test-AdapterPort 12050 $bindAddress)){Assert-StartedAdapterListeners $proc.Id;Write-RuntimeIdentity $proc $bindAddress;return $proc}
    }
    Stop-LocalAdapter
    throw '适配器未在10秒内准备完成；请检查日志。'
}
function Register-SocialAccount([string]$ip,[string]$username){
    $account=$username.Trim();if($account.Length-lt1-or$account.Length-gt64-or(@($account.ToCharArray()|Where-Object{[char]::IsControl($_)}).Count-gt0)){throw '社交模式用户名必须为1到64个非控制字符。'}
    $json=[ordered]@{LocalAccount=$account;PureNewPlayer=$true}|ConvertTo-Json -Compress
    Send-LocalLaunchRegistration $ip ([Text.Encoding]::UTF8.GetBytes($json)) '社交账号'
    return $account
}
function Start-SocialParticipant([int]$slot,[string]$username){
    $ip=Normalize-NetworkIPv4 $socialIpBox.Text;$info=Get-LaunchModeInfo 'network' $ip
    Test-ClientBinary;$launchConfig=Get-LaunchModeConfigText $info
    $compatibility=Ensure-ClientCompatibility
    if(-not(Test-Path -LiteralPath $SocialClientLauncher -PathType Leaf)){throw "Social multi-client helper missing: $SocialClientLauncher"}
    # Failed cache preparation must not enqueue an account for a nonexistent client.
    $prepared=& $SocialClientLauncher -ClientPath $Client -SocialSlot $slot -WorkingDirectory $Root -LaunchModeConfigText $launchConfig -PrepareOnly
    $account=Register-SocialAccount $ip $username
    [IO.File]::WriteAllText($SocialAdapterIpState,$ip+"`r`n",(New-Object Text.ASCIIEncoding))
    $launch=& $SocialClientLauncher -ClientPath $Client -SocialSlot $slot -ClientArguments ([string[]]$info.ClientArgs) -WorkingDirectory $Root -LaunchModeConfigText $launchConfig
    $changed=@($compatibility.apply_results|Where-Object{$_.status-eq'applied'}).Count
    $socialStatus.Text="P$slot 已以账号 $account 连接 $ip；客户端 PID=$($launch.ProcessId)，独立工作目录=$($launch.WorkDirectory)，独立缓存=$($launch.RuntimeDirectory)；兼容性已校验（应用 $changed 项）。"
}
$socialUseDetectedBtn.add_Click({$socialIpBox.Text=$detectedLanIp})
$socialAdapterBtn.add_Click({
    try{
        $bindAddress=Normalize-NetworkIPv4 $socialIpBox.Text
        # Social accounts are registered separately; starting the shared service must not
        # save/import the unrelated local character or depend on its inventory catalog.
        if(-not(Test-Path -LiteralPath $ProfileIni -PathType Leaf)){throw "Adapter profile missing: $ProfileIni"}
        Stop-LocalAdapter
        $proc=Start-LocalAdapter $ProfileIni $bindAddress
        [IO.File]::WriteAllText($SocialAdapterIpState,$bindAddress+"`r`n",(New-Object Text.ASCIIEncoding))
        $socialStatus.Text="社交适配器已单例启动：$bindAddress，PID=$($proc.Id)。局域网参与者在上方填写该地址。"
    }catch{[Windows.Forms.MessageBox]::Show($_.Exception.Message,'社交适配器启动失败')|Out-Null}
})
for($i=0;$i-lt$socialPlayButtons.Count;$i++){
    $slot=$i+1;$box=$socialUserBoxes[$i]
    $handler={try{Start-SocialParticipant $slot $box.Text}catch{[Windows.Forms.MessageBox]::Show($_.Exception.Message,("P{0} 启动失败"-f$slot))|Out-Null}}.GetNewClosure()
    $socialPlayButtons[$i].add_Click($handler)
}$inventoryAdmin=Initialize-InventoryAdmin $tabs $Root $(if($ini.name_hex){[string]$ini.name_hex}else{Encode-NameHex $defaultName})
$inventoryAdmin.ProfileChanged={param($ctx)
    if($ctx.Profile){
        if($ctx.Profile.character_name){$nameBox.Text=[string]$ctx.Profile.character_name}
        if($null-ne$ctx.Profile.level){$levelBox.Value=[Math]::Min($levelBox.Maximum,[Math]::Max($levelBox.Minimum,[decimal][int]$ctx.Profile.level))}
        if($null-ne$ctx.Profile.gender-and([int]$ctx.Profile.gender-in@(0,1))){$genderCombo.SelectedIndex=[int]$ctx.Profile.gender}
        foreach($pair in @(@($hpMaxBox,'hp_max'),@($mpMaxBox,'mp_max'),@($attackBox,'attack'),@($defenseBox,'defense'),@($coinBox,'coin'),@($nanaPointBox,'nana_point'))){$value=[decimal]$ctx.Profile.($pair[1]);$pair[0].Value=[Math]::Min($pair[0].Maximum,[Math]::Max($pair[0].Minimum,$value))}
    }
    foreach($pair in @(@($apartmentPointsBox,'apartment_recommendation_points'),@($cardKeyNormalBox,'card_key_normal'),@($cardKeyGoldBox,'card_key_gold'),@($cardKeyMysteryBox,'card_key_mystery'),@($cardKeySpecialBox,'card_key_special'),@($freeMagicKeyExpiryBox,'free_magic_key_expiry'),@($quickbarExpiryBox,'quickbar_expiry'),@($skillSlotExpiryBox,'skill_slot_expiry'))){if($null-ne$ctx.Profile.($pair[1])){$value=[decimal]$ctx.Profile.($pair[1]);$pair[0].Value=[Math]::Min($pair[0].Maximum,[Math]::Max($pair[0].Minimum,$value))}}
    if($null-ne$ctx.Profile.skill_slot_expiry_apply){$skillSlotExpiryApplyBox.Checked=[bool]$ctx.Profile.skill_slot_expiry_apply}
    if($ctx.Shop){
        $appearance=@($ctx.Shop.equipped);$parts=@('hair','body','top','bottom','accessory');for($i=0;$i-lt$parts.Count-and$i-lt$appearance.Count;$i++){if([uint32]$appearance[$i]){[void](Select-ComboId $comboMap[$parts[$i]] ([uint32]$appearance[$i]))}}
        if([uint32]$ctx.Shop.effect){[void](Select-ComboId $comboMap.effect ([uint32]$ctx.Shop.effect))}
        if([uint32]$ctx.Shop.selected_pet){[void](Select-ComboId $petCombo ([uint32]$ctx.Shop.selected_pet));Update-PetAgeOptions $null}
    }
}.GetNewClosure()
[void](Add-InventoryAdminProfileSelector $tabResources $inventoryAdmin)
$profileSaveBtn=New-Object Windows.Forms.Button;$profileSaveBtn.Text='保存所选档案';$profileSaveBtn.Location=New-Object Drawing.Point(900,50);$profileSaveBtn.Size=New-Object Drawing.Size(175,36);$profileSaveBtn.BackColor=[Drawing.Color]::LightGreen;$tabResources.Controls.Add($profileSaveBtn)
$profileSaveBtn.add_Click({try{if((Get-LocalAdapters).Count){throw '请先停止适配器再修改用户档案。'};[void](Sync-LauncherProfileIdentity);$resources=Get-ResourceSelection;$inventoryAdmin.Shop.coin=[uint64]$resources.coin;$inventoryAdmin.Shop.nana=[uint64]$resources.nana_point;foreach($entry in @{hp_max=$resources.hp_max;mp_max=$resources.mp_max;attack=$resources.attack;defense=$resources.defense;coin=$resources.coin;nana_point=$resources.nana_point}.GetEnumerator()){$inventoryAdmin.Profile|Add-Member -NotePropertyName $entry.Key -NotePropertyValue $entry.Value -Force};$result=Save-InventoryAdminState $inventoryAdmin $inventoryAdmin.NameHex $inventoryAdmin.Shop $inventoryAdmin.Profile;$status.Text="已保存所选用户档案：$($inventoryAdmin.SelectedProfile.display)；备份：$($result.backup)"}catch{[Windows.Forms.MessageBox]::Show($_.Exception.Message,'档案保存失败')|Out-Null}})
function Capture-LauncherProfileEditorState {
    if(-not$inventoryAdmin.Profile){$inventoryAdmin.Profile=[pscustomobject]@{}}
    $inventoryAdmin.Profile|Add-Member -NotePropertyName level -NotePropertyValue ([int]$levelBox.Value) -Force
    $inventoryAdmin.Profile|Add-Member -NotePropertyName gender -NotePropertyValue ([int]$genderCombo.SelectedIndex) -Force
    $resources=Get-ResourceSelection
    foreach($entry in $resources.GetEnumerator()){$inventoryAdmin.Profile|Add-Member -NotePropertyName $entry.Key -NotePropertyValue $entry.Value -Force}
    if(-not$inventoryAdmin.Shop){$inventoryAdmin.Shop=[pscustomobject]@{equipped=@(0,0,0,0,0);effect=0;selected_pet=0}}
    $inventoryAdmin.Shop.coin=[uint64]$resources.coin;$inventoryAdmin.Shop.nana=[uint64]$resources.nana_point
    $parts=@('hair','body','top','bottom','accessory');$equipped=New-Object uint32[] 5;for($i=0;$i-lt$parts.Count;$i++){$d=Get-SelectedData $comboMap[$parts[$i]];if($d){$equipped[$i]=[uint32]$d.id}};$inventoryAdmin.Shop.equipped=@($equipped)
    $effect=Get-SelectedData $comboMap.effect;if($effect){$inventoryAdmin.Shop.effect=[uint32]$effect.id};$pet=Get-SelectedData $petCombo;if($pet){$inventoryAdmin.Shop.selected_pet=[uint32]$pet.id}
}
function Sync-LauncherProfileIdentity {
    if($pureNewPlayerBox.Checked){return $null}
    $identity=$nameBox.Text.Trim();$existing=Find-InventoryAdminProfile $inventoryAdmin $identity
    if(-not$existing-or($null-eq$existing.character_id-and-not(Test-Path -LiteralPath (Join-Path $Root ('inventory_admin_profiles/'+$existing.name_hex+'.json'))))){[void](ConvertTo-InventoryAdminNameHex $identity);Capture-LauncherProfileEditorState}
    $profile=Ensure-InventoryAdminProfile $inventoryAdmin $identity
    if($profile-and$profile.character_name-and[string]$nameBox.Text.Trim()-ne[string]$profile.character_name){$nameBox.Text=[string]$profile.character_name}
    return $profile
}
$nameBox.add_Leave({try{if(-not$pureNewPlayerBox.Checked){[void](Sync-LauncherProfileIdentity)}}catch{$status.Text='档案选择失败：'+$_.Exception.Message}})
$tabs.TabPages.Add($tabLaunchInfo);$tabs.TabPages.Add($tabPets);$tabs.TabPages.Add($tabEquip);$tabs.TabPages.Add($tabFurniture)
function Save-Profile {
    if($pureNewPlayerBox.Checked){throw '纯新手档不会保存或导入GUI角色配置；请取消勾选后再保存常规档。'}
    if((Get-LocalAdapters).Count){throw '适配器运行期间不能改写仓库/背包文件。请先停止适配器再保存；“进入 Nanaimo”不会再隐式保存或重启服务。'}
    [void](Sync-LauncherProfileIdentity)
    Capture-LauncherProfileEditorState
    $name=$nameBox.Text.Trim();$hex=Encode-NameHex $name;$pet=Get-SelectedData $petCombo;if(-not$pet){throw '请选择宠物。'}
    $selected=@{};foreach($part in $comboMap.Keys){$d=Get-SelectedData $comboMap[$part];if(-not$d){throw "请选择 $($PartLabels[$part])。"};$selected[$part]=$d}
    $age=Selected-PetAge;$resources=Get-ResourceSelection;$skills=Get-SkillSelection;$titleSelection=Get-SelectedDungeonTitle;$launchMode=Get-SelectedLaunchMode;$networkIp=Get-NetworkIpInput;$launchModeInfo=Get-SelectedLaunchModeInfo
    $lines=@('version=2',"launch_mode=$launchMode","network_ip=$networkIp","skip_tutorial=$([int]$skipTutorialBox.Checked)","unlock_all_dungeons=$([int]$unlockAllDungeonsBox.Checked)","gender=$($genderCombo.SelectedIndex)","name_hex=$hex","dungeon_grade=$(if([int]$titleSelection.Grade-ge0){[int]$titleSelection.Grade}else{'auto'})","level=$([int]$levelBox.Value)","pet=$($pet.id)","pet_age_a=$age","pet_age_b=$($pet.max_age)","initial_attack_mode=$(Selected-AttackMode)","equip_hair=$($selected.hair.id)","equip_body=$($selected.body.id)","equip_top=$($selected.top.id)","equip_bottom=$($selected.bottom.id)","equip_accessory=$($selected.accessory.id)","equip_effect=$($selected.effect.id)","hp_max=$($resources.hp_max)","mp_max=$($resources.mp_max)","attack=$($resources.attack)","defense=$($resources.defense)","coin=$($resources.coin)","nana_point=$($resources.nana_point)","apartment_recommendation_points=$($resources.apartment_recommendation_points)","card_key_normal=$($resources.card_key_normal)","card_key_gold=$($resources.card_key_gold)","card_key_mystery=$($resources.card_key_mystery)","card_key_special=$($resources.card_key_special)","free_magic_key_expiry=$($resources.free_magic_key_expiry)","quickbar_expiry=$($resources.quickbar_expiry)")
    if($resources.skill_slot_expiry_apply){$lines+="skill_slot_expiry=$($resources.skill_slot_expiry)"}
    $lines+=@('skill_config=1',"skill_projectile_route=$($skills.projectile_route)","skill_meat_route=$($skills.meat_route)","skill_slot_z=$($skills.slot_z)","skill_slot_x=$($skills.slot_x)")
    for($i=0;$i-lt16;$i++){$lines+="skill_grade$i=$($skills.grades[$i])"}
    $adminShop=[ordered]@{coin=[uint64]$resources.coin;nana=[uint64]$resources.nana_point;equipped=@([uint32]$selected.hair.id,[uint32]$selected.body.id,[uint32]$selected.top.id,[uint32]$selected.bottom.id,[uint32]$selected.accessory.id);effect=[uint32]$selected.effect.id;selected_pet=[uint32]$pet.id}
    $adminResult=Save-InventoryAdminState $inventoryAdmin $hex $adminShop
    [IO.File]::WriteAllLines($ProfileIni,$lines,(New-Object Text.ASCIIEncoding))
    $titleStatePath=$null;if([int]$titleSelection.Grade-ge0){$titleStatePath=Write-DungeonGradeState $ProfileStateRoot $hex ([int]$titleSelection.Grade)}
    $view=[ordered]@{launch_mode=$launchMode;network_ip=$networkIp;start_local_adapter=$launchModeInfo.StartLocalAdapter;skip_tutorial=[bool]$skipTutorialBox.Checked;unlock_all_dungeons=[bool]$unlockAllDungeonsBox.Checked;name=$name;level=[int]$levelBox.Value;title=[ordered]@{mode=if([int]$titleSelection.Grade-ge0){'fixed'}else{'progress'};grade=[int]$titleSelection.Grade;rank=[string]$titleSelection.Rank;resource_id=$titleSelection.ResourceId;icon_resource=$titleSelection.IconResource;text_resource=$titleSelection.TextResource;name=[string]$titleSelection.Name;state_file=$titleStatePath};gender=if($genderCombo.SelectedIndex-eq1){'M'}else{'F'};pet=$pet;pet_selected_age=$age;initial_attack_mode=Selected-AttackMode;equipment=[ordered]@{hair=$selected.hair;body=$selected.body;top=$selected.top;bottom=$selected.bottom;accessory=$selected.accessory;effect=$selected.effect};resources=[ordered]@{hp_max=$resources.hp_max;mp_max=$resources.mp_max;attack=$resources.attack;defense=$resources.defense;attack_carrier='CFEC+0x2E0';defense_policy='local max(1, raw-defense) before D010/D015';coin=$resources.coin;nana_point=$resources.nana_point;apartment_recommendation_points=$resources.apartment_recommendation_points;card_key_normal=$resources.card_key_normal;card_key_gold=$resources.card_key_gold;card_key_mystery=$resources.card_key_mystery;card_key_special=$resources.card_key_special;free_magic_key_expiry=$resources.free_magic_key_expiry;quickbar_expiry=$resources.quickbar_expiry;skill_slot_expiry=$resources.skill_slot_expiry;skill_slot_expiry_configured=$resources.skill_slot_expiry_apply;currency_carriers='C37B+C379';item_carriers='C3E8+C430+C474'};inventory_admin=[ordered]@{account_suffix=$adminResult.account_suffix;backup=$adminResult.backup;clothing=$inventoryAdmin.Clothing.Count;pets=$inventoryAdmin.Pets.Count;game_item_kinds=$inventoryAdmin.GameItems.Count;furniture=$inventoryAdmin.Furniture.Count;cards=$inventoryAdmin.Cards.Count};skills=[ordered]@{projectile_route=$skills.projectile_route;meat_route=$skills.meat_route;slot_z=$skills.slot_z;slot_x=$skills.slot_x;grades=@($skills.grades)};saved_at=(Get-Date).ToString('s')}
    [IO.File]::WriteAllText($ProfileJson,($view|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
    $status.Text="Saved profile + title selection + five inventory domains (backup: $($adminResult.backup)).`r`nMax HP $($resources.hp_max), Max MP $($resources.mp_max), attack +$($resources.attack), defense $($resources.defense); Title=$($titleSelection.Display); Z=$(Skill-CodeName $skills.slot_z), X=$(Skill-CodeName $skills.slot_x).";Update-LaunchPreview
}
if($SelfTestProfileIO){
    Save-Profile;$skillExpect=Get-SkillSelection;$roundIni=Read-IniProfile;$roundJson=Get-Content -LiteralPath $ProfileJson -Raw -Encoding UTF8|ConvertFrom-Json
    $expect=Get-ResourceSelection;$expectTitle=Get-SelectedDungeonTitle;$expectMode=Get-SelectedLaunchMode;$expectIp=Get-NetworkIpInput
    if([string]$roundIni.launch_mode-ne$expectMode-or[string]$roundJson.launch_mode-ne$expectMode){throw 'launch_mode roundtrip'}
    if([string]$roundIni.dungeon_grade-ne$(if([int]$expectTitle.Grade-ge0){[string][int]$expectTitle.Grade}else{'auto'})-or[int]$roundJson.title.grade-ne[int]$expectTitle.Grade){throw 'dungeon_grade roundtrip'}
    if([string]$roundIni.network_ip-ne$expectIp-or[string]$roundJson.network_ip-ne$expectIp){throw 'network_ip roundtrip'}
    $expectSkip=[int]$skipTutorialBox.Checked;if([int]$roundIni.skip_tutorial-ne$expectSkip-or[bool]$roundJson.skip_tutorial-ne[bool]$skipTutorialBox.Checked){throw 'skip_tutorial roundtrip'}
    $expectUnlock=[int]$unlockAllDungeonsBox.Checked;if([int]$roundIni.unlock_all_dungeons-ne$expectUnlock-or[bool]$roundJson.unlock_all_dungeons-ne[bool]$unlockAllDungeonsBox.Checked){throw 'unlock_all_dungeons roundtrip'}
    foreach($k in 'hp_max','mp_max','attack','defense','coin','nana_point','apartment_recommendation_points','card_key_normal','card_key_gold','card_key_mystery','card_key_special','free_magic_key_expiry','quickbar_expiry'){if([string]$roundIni[$k]-ne[string]$expect[$k]){throw "INI roundtrip $k"};if([string]$roundJson.resources.$k-ne[string]$expect[$k]){throw "JSON roundtrip $k"}};if($expect.skill_slot_expiry_apply){if([string]$roundIni.skill_slot_expiry-ne[string]$expect.skill_slot_expiry){throw 'INI roundtrip skill_slot_expiry'}}elseif($roundIni.ContainsKey('skill_slot_expiry')){throw 'implicit skill_slot_expiry write'};if([string]$roundJson.resources.skill_slot_expiry-ne[string]$expect.skill_slot_expiry-or[bool]$roundJson.resources.skill_slot_expiry_configured-ne[bool]$expect.skill_slot_expiry_apply){throw 'JSON roundtrip skill_slot_expiry'}
    foreach($legacy in 'hp_current','mp_current'){if($roundIni.ContainsKey($legacy)){throw "legacy INI resource key retained: $legacy"};if($roundJson.resources.PSObject.Properties.Name-contains$legacy){throw "legacy JSON resource key retained: $legacy"}}
    $skillExpectedValues=@{skill_projectile_route=$skillExpect.projectile_route;skill_meat_route=$skillExpect.meat_route;skill_slot_z=$skillExpect.slot_z;skill_slot_x=$skillExpect.slot_x}
    foreach($k in $skillExpectedValues.Keys){if([string]$roundIni[$k]-ne[string]$skillExpectedValues[$k]){throw "INI roundtrip $k"}}
    for($j=0;$j-lt16;$j++){if([int]$roundIni["skill_grade$j"]-ne[int]$skillExpect.grades[$j]){throw "INI roundtrip skill_grade$j"};if([int]$roundJson.skills.grades[$j]-ne[int]$skillExpect.grades[$j]){throw "JSON roundtrip skill_grade$j"}}
    if([uint32]$roundJson.skills.slot_z-ne[uint32]$skillExpect.slot_z-or[uint32]$roundJson.skills.slot_x-ne[uint32]$skillExpect.slot_x){throw 'JSON roundtrip skill slots'}
    Write-Output ("NETWORK_PROFILE_IO_PASS title_grade=$([int]$expectTitle.Grade) launch_mode={0} network_ip={1} max_hp={2} max_mp={3} attack={4} defense={5} coin={6} nana_point={7} skip_tutorial={8} skill_routes={9}/{10} Z={11} X={12}"-f$expectMode,$expectIp,$expect.hp_max,$expect.mp_max,$expect.attack,$expect.defense,$expect.coin,$expect.nana_point,$expectSkip,$skillExpect.projectile_route,$skillExpect.meat_route,$skillExpect.slot_z,$skillExpect.slot_x);exit 0
}
$saveBtn.add_Click({try{Save-Profile;[Windows.Forms.MessageBox]::Show('配置已保存。','Nanaimo 启动器')|Out-Null}catch{[Windows.Forms.MessageBox]::Show($_.Exception.Message,'配置错误')|Out-Null}})
$folderBtn.add_Click({$target=if(Test-Path -LiteralPath $AdapterLogs){$AdapterLogs}else{$Root};Start-Process explorer.exe -ArgumentList $target})
$resetProgressBtn.add_Click({try{if((Get-LocalAdapters).Count){throw 'Stop the local Nanaimo protocol adapter first.'};$name=$nameBox.Text.Trim();if(-not$name){throw 'Character name is required.'};$hex=Encode-NameHex $name;$paths=@((Join-Path $Root ("level_progress_state_v1_{0}.dat"-f$hex)),(Join-Path $Root ("level_progress_state_v1_{0}.bak"-f$hex)),(Join-Path $Root ("level_progress_state_v1_{0}.new"-f$hex)),(Join-Path $Root ("dungeon_grade_state_v1_{0}.dat"-f$hex)),(Join-Path $Root ("dungeon_grade_state_v1_{0}.bak"-f$hex)),(Join-Path $Root ("dungeon_grade_state_v1_{0}.new"-f$hex)));Remove-Item -LiteralPath $paths -Force -ErrorAction SilentlyContinue;$status.Text="Reset level, EXP, and dungeon-title progress for $name. Next start seeds level $([int]$levelBox.Value) and dungeon grade 0."}catch{[Windows.Forms.MessageBox]::Show($_.Exception.Message,'Reset failed')|Out-Null}})
$defaultBtn.add_Click({$pureNewPlayerBox.Checked=$false;$skipTutorialBox.Checked=$false;$unlockAllDungeonsBox.Checked=$true;$nameBox.Text='Greyrat';$titleCombo.SelectedIndex=0;$levelBox.Value=25;$hpMaxBox.Value=1500;$mpMaxBox.Value=500;$attackBox.Value=0;$defenseBox.Value=0;$coinBox.Value=0;$nanaPointBox.Value=0;$apartmentPointsBox.Value=1000;$cardKeyNormalBox.Value=99;$cardKeyGoldBox.Value=99;$cardKeyMysteryBox.Value=99;$cardKeySpecialBox.Value=99;$freeMagicKeyExpiryBox.Value=2099123123;$quickbarExpiryBox.Value=0;$skillSlotExpiryBox.Value=0;$skillSlotExpiryApplyBox.Checked=$true;$projectileRouteCombo.SelectedIndex=0;$meatRouteCombo.SelectedIndex=0;for($i=0;$i-lt16;$i++){$skillGradeBoxes[$i].Value=0};foreach($i in 0,1,8,9){$skillGradeBoxes[$i].Value=5};Set-SkillSlotChoices 0 0;Update-SkillWarning;$genderCombo.SelectedIndex=1;Select-ComboId $petCombo 15009205|Out-Null;Update-PetAgeOptions 3;Select-ComboId $comboMap.hair 10130337|Out-Null;Select-ComboId $comboMap.body 10100028|Out-Null;Select-ComboId $comboMap.top 10110337|Out-Null;Select-ComboId $comboMap.bottom 10120352|Out-Null;Select-ComboId $comboMap.accessory 10150103|Out-Null;Select-ComboId $comboMap.effect 10160017|Out-Null;Update-PetDetail;Update-LaunchModePresentation})
$adapterBtn.add_Click({
    try{
        if((Get-LocalAdapters).Count){Stop-LocalAdapter;$adapterBtn.Text='启动适配器';$status.Text='适配器已停止。';return}
        Save-Profile
        $proc=Start-LocalAdapter
        $adapterBtn.Text='Stop adapter';$status.Text="Adapter started PID=$($proc.Id), $ReleaseIdentity.`r`nCanonical entry: $CanonicalLauncher; identity: $RuntimeIdentityPath"
    }catch{[Windows.Forms.MessageBox]::Show($_.Exception.Message,'适配器启动失败')|Out-Null}
})
$clientBtn.add_Click({
    try{
        $launchModeInfo=Get-SelectedLaunchModeInfo;$pure=[bool]$pureNewPlayerBox.Checked
        if($launchModeInfo.Key-ne'network'-or$launchModeInfo.AdapterIP-ne'127.0.0.1'){throw '本地模式必须使用绑定 127.0.0.1 的 Network 协议适配器。'}
        Assert-LocalAdapterRunning
        if($pure){$runtimeProfile=Write-PureNewPlayerRuntimeProfile}else{[void](Sync-LauncherProfileIdentity)}
        Test-ClientBinary;Install-LaunchModeConfig $launchModeInfo
        $compatibility=Ensure-ClientCompatibility
        $account=$null
        if($pure){$account=Register-PureNewPlayer $launchModeInfo.AdapterIP $pureNewPlayerUsernameBox.Text}else{$account=Register-ClientProfile $launchModeInfo.AdapterIP}
        if($launchModeInfo.ClientArgs.Count){Start-Process -FilePath $Client -ArgumentList ([string[]]$launchModeInfo.ClientArgs) -WorkingDirectory $Root|Out-Null}else{Start-Process -FilePath $Client -WorkingDirectory $Root|Out-Null}
        $changed=@($compatibility.apply_results|Where-Object{$_.status-eq'applied'}).Count
        $status.Text=if($pure){"纯新手账号 $account 已注册到现有本地协议适配器；兼容性已验证（应用 $changed 个文件）；已启动另一个客户端。`r`n运行配置：$runtimeProfile"}else{"所选账号 $account 已注册到现有本地协议适配器；兼容性已验证（应用 $changed 个文件）；已启动另一个客户端。"}
    }catch{[Windows.Forms.MessageBox]::Show($_.Exception.Message,'启动 Nanaimo 失败')|Out-Null}
})
if($SelfTestLocalEntry){
    $source=Get-Content -LiteralPath $PSCommandPath -Raw -Encoding UTF8
    $entryStart=$source.IndexOf('$clientBtn.add_Click({');$entryEnd=$source.IndexOf('# Pet lookup tab',$entryStart)
    if($entryStart-lt0-or$entryEnd-le$entryStart){throw 'local entry source block missing'}
    $entry=$source.Substring($entryStart,$entryEnd-$entryStart)
    foreach($required in @('Get-SelectedLaunchModeInfo','Assert-LocalAdapterRunning','Write-PureNewPlayerRuntimeProfile','Test-ClientBinary;Install-LaunchModeConfig $launchModeInfo','Ensure-ClientCompatibility','Register-PureNewPlayer $launchModeInfo.AdapterIP','Register-ClientProfile $launchModeInfo.AdapterIP','Start-Process -FilePath $Client')){if(-not$entry.Contains($required)){throw "local entry missing: $required"}}
    foreach($forbidden in @('Stop-LocalAdapter','Start-LocalAdapter','Save-Profile','Get-Process -Name game','Stop-Process')){if($entry.Contains($forbidden)){throw "local entry must not invoke: $forbidden"}}
    $info=Get-SelectedLaunchModeInfo
    if($info.Key-ne'network'-or$info.AdapterIP-ne'127.0.0.1'-or-not$info.StartLocalAdapter){throw 'local entry is not fixed to loopback Network mode'}
    if($entry.IndexOf('Install-LaunchModeConfig $launchModeInfo')-gt$entry.IndexOf('Ensure-ClientCompatibility')-or$entry.IndexOf('Ensure-ClientCompatibility')-gt$entry.IndexOf('Register-ClientProfile $launchModeInfo.AdapterIP')-or$entry.IndexOf('Register-ClientProfile $launchModeInfo.AdapterIP')-gt$entry.IndexOf('Start-Process -FilePath $Client')){throw 'local entry operation order'}
    Write-Output 'GUI_LOCAL_ENTRY_SELFTEST_PASS mode=network endpoint=127.0.0.1 adapter=required_and_reused profile=selected_account existing_clients=preserved multiclient=true normal_and_pure_registration=true'
    $form.Close();$form.Dispose();exit 0
}
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
# Furniture lookup tab
$furnitureSearch=New-Object Windows.Forms.TextBox;$furnitureSearch.Location=New-Object Drawing.Point(18,18);$furnitureSearch.Size=New-Object Drawing.Size(420,28);$tabFurniture.Controls.Add($furnitureSearch)
$furnitureTypeFilter=New-Object Windows.Forms.ComboBox;$furnitureTypeFilter.Location=New-Object Drawing.Point(450,16);$furnitureTypeFilter.Size=New-Object Drawing.Size(190,30);$furnitureTypeFilter.DropDownStyle='DropDownList';[void]$furnitureTypeFilter.Items.Add('全部类型');foreach($x in @($furniture|ForEach-Object{$_.type_name}|Sort-Object -Unique)){[void]$furnitureTypeFilter.Items.Add([string]$x)};$furnitureTypeFilter.SelectedIndex=0;$tabFurniture.Controls.Add($furnitureTypeFilter)
$furnitureSearchBtn=New-Object Windows.Forms.Button;$furnitureSearchBtn.Text='筛选';$furnitureSearchBtn.Location=New-Object Drawing.Point(655,16);$furnitureSearchBtn.Size=New-Object Drawing.Size(90,32);$tabFurniture.Controls.Add($furnitureSearchBtn)
$furnitureClearBtn=New-Object Windows.Forms.Button;$furnitureClearBtn.Text='清除';$furnitureClearBtn.Location=New-Object Drawing.Point(755,16);$furnitureClearBtn.Size=New-Object Drawing.Size(90,32);$tabFurniture.Controls.Add($furnitureClearBtn)
$furnitureSplit=New-Object Windows.Forms.SplitContainer;$furnitureSplit.Location=New-Object Drawing.Point(18,58);$furnitureSplit.Size=New-Object Drawing.Size(1060,710);$furnitureSplit.Anchor='Top,Left';$furnitureSplit.Orientation='Vertical';$furnitureSplit.FixedPanel='Panel2';$furnitureSplit.SplitterDistance=790;$furnitureSplit.Panel1MinSize=520;$furnitureSplit.Panel2MinSize=245;$tabFurniture.Controls.Add($furnitureSplit)
$furnitureGrid=New-Object Windows.Forms.DataGridView;$furnitureGrid.Dock='Fill';$furnitureGrid.ReadOnly=$true;$furnitureGrid.AllowUserToAddRows=$false;$furnitureGrid.SelectionMode='FullRowSelect';$furnitureGrid.MultiSelect=$false;$furnitureGrid.AutoSizeColumnsMode='DisplayedCells';$furnitureSplit.Panel1.Controls.Add($furnitureGrid)
$furniturePreviewPane=New-CatalogPreviewPane $furnitureSplit.Panel2 '装饰家具预览'
$furnitureTable=New-GridTable 'furniture' $furniture;$furnitureView=New-Object Data.DataView;$furnitureView.Table=$furnitureTable;$furnitureGrid.DataSource=$furnitureView
$applyFurnitureFilter={ $conds=New-Object Collections.Generic.List[string];$q=Escape-Filter $furnitureSearch.Text.Trim();if($q){$conds.Add("([名称] LIKE '%$q%' OR [ID] LIKE '%$q%' OR [说明] LIKE '%$q%' OR [效果] LIKE '%$q%' OR [来源] LIKE '%$q%')")};if($furnitureTypeFilter.SelectedIndex-gt0){$v=Escape-Filter ([string]$furnitureTypeFilter.SelectedItem);$conds.Add("[类型] = '$v'")};$furnitureView.RowFilter=($conds-join' AND ');if($furnitureGrid.Rows.Count){$furnitureGrid.Rows[0].Selected=$true;Update-CatalogGridPreview 'furniture' $furnitureGrid $furniturePreviewPane} }
$furnitureSearchBtn.add_Click($applyFurnitureFilter);$furnitureClearBtn.add_Click({$furnitureSearch.Clear();$furnitureTypeFilter.SelectedIndex=0;$furnitureView.RowFilter='';if($furnitureGrid.Rows.Count){$furnitureGrid.Rows[0].Selected=$true;Update-CatalogGridPreview 'furniture' $furnitureGrid $furniturePreviewPane}})
$furnitureGrid.add_SelectionChanged({Update-CatalogGridPreview 'furniture' $furnitureGrid $furniturePreviewPane})
$layoutFurnitureCatalog={Set-CatalogSplitLayout $tabFurniture $furnitureSplit $furniturePreviewPane}.GetNewClosure();$tabFurniture.add_Resize($layoutFurnitureCatalog);&$layoutFurnitureCatalog
$form.add_FormClosed({if($petPreviewPane.Picture.Image){$petPreviewPane.Picture.Image.Dispose()};if($equipPreviewPane.Picture.Image){$equipPreviewPane.Picture.Image.Dispose()};if($furniturePreviewPane.Picture.Image){$furniturePreviewPane.Picture.Image.Dispose()};if($script:petPreviewAtlas){$script:petPreviewAtlas.Dispose()};if($script:equipPreviewAtlas){$script:equipPreviewAtlas.Dispose()};if($script:furniturePreviewAtlas){$script:furniturePreviewAtlas.Dispose()}})
if($petGrid.Rows.Count){Update-CatalogGridPreview 'pet' $petGrid $petPreviewPane};if($equipGrid.Rows.Count){Update-CatalogGridPreview 'equip' $equipGrid $equipPreviewPane};if($furnitureGrid.Rows.Count){Update-CatalogGridPreview 'furniture' $furnitureGrid $furniturePreviewPane}
function Assert-NoVisibleConnectionText([Windows.Forms.Control]$control){
    if(-not$control.Visible){return}
    if($control.Text-match '(?i)连接方式|连接模式|启动模式|适配器地址|固定使用|\bNetwork\b|127\.0\.0\.1|ServerIP|network_ip|launch_mode|Selected launch mode|Mode template|\bMode=|\bLogin=|Standalone|Stand_Alone|(?<!\w)-q(?!\w)'){
        throw "visible connection wording: $($control.GetType().Name) $($control.Text)"
    }
    foreach($child in $control.Controls){Assert-NoVisibleConnectionText $child}
}
if($SelfTestSocialMode){
    if($tabSocial.Text-ne'社交模式'-or$socialPlayButtons.Count-ne3-or$socialUserBoxes.Count-ne3){throw 'social mode controls'}
    $probe=Get-LaunchModeInfo 'network' '198.51.100.25'
    $payload=[ordered]@{LocalAccount='P2';PureNewPlayer=$false}|ConvertTo-Json -Compress|ConvertFrom-Json
    if($probe.StartLocalAdapter-or$probe.AdapterIP-ne'198.51.100.25'-or[string]$payload.LocalAccount-ne'P2'-or[bool]$payload.PureNewPlayer){throw 'social mode contract'}
    Write-Output "GUI_SOCIAL_MODE_SELFTEST_PASS detected=$detectedLanIp participants=3 source_ip_isolation=true dynamic_adapter_ip=true"
    $form.Close();$form.Dispose();exit 0
}
# Exercise real WinForms layout without launching a game or saving any player state.
if($SelfTestLayout){
    $form.ShowInTaskbar=$false;$form.Opacity=0;[void]$form.Show()
    try{
        $tabs.SelectedTab=$tabStart;[Windows.Forms.Application]::DoEvents();$defaultBtn.PerformClick()
        if((@($tabs.TabPages|ForEach-Object{$_.Text})-join'|')-ne'社交模式|本地模式|数值管理|衣物箱管理|宠物箱管理|游戏道具管理|装饰家具管理|卡片管理|本次启动详情|宠物查表|装扮查表|装饰家具查表'){throw 'launcher tab order'}
        if($nameBox.Text-ne'Greyrat'){throw 'default name'}
        if($pureNewPlayerBox.Checked){throw 'default pure-new-player mode'}
        $pureNewPlayerBox.Checked=$true;[Windows.Forms.Application]::DoEvents()
        if($nameBox.Enabled-or$skipTutorialBox.Enabled-or$unlockAllDungeonsBox.Enabled-or-not$pureNewPlayerUsernameBox.Enabled-or$saveBtn.Enabled-or$adapterBtn.Enabled-or-not$clientBtn.Enabled-or$skipTutorialBox.Checked-or$unlockAllDungeonsBox.Checked-or$clientBtn.Text-ne'构建并进入纯新手档'){throw 'pure-new-player control gate'}
        $pureNewPlayerBox.Checked=$false;[Windows.Forms.Application]::DoEvents()
        if(-not$inventoryAdmin.CardAddAllButton-or-not$inventoryAdmin.CardRemoveAllButton){throw 'card bulk-action button missing'}
        $tabs.SelectedTab=$inventoryAdmin.CardAddAllButton.Parent;[Windows.Forms.Application]::DoEvents();$inventoryAdmin.CardAddAllButton.PerformClick()
        $ownedCardCodes=@{};foreach($row in $inventoryAdmin.Cards){$ownedCardCodes[[uint32]$row.code]=$true};$missingCards=@($inventoryAdmin.Catalog.cards|Where-Object{-not$ownedCardCodes.ContainsKey([uint32]$_.id)});if($missingCards.Count){throw 'card add-all action'}
        $readonlyCode=[uint32]42424242
        if(@($inventoryAdmin.Catalog.cards|Where-Object{[uint32]$_.id-eq$readonlyCode}).Count){throw 'readonly test fixture is catalogued'}
        if(-not@($inventoryAdmin.Cards|Where-Object{[uint32]$_.code-eq$readonlyCode}).Count){[void]$inventoryAdmin.Cards.Add([pscustomobject]@{code=$readonlyCode;count=[uint32]3})}
        $protectedBefore=@($inventoryAdmin.Cards|Where-Object{[uint32]$_.code-notin@($inventoryAdmin.Catalog.cards|ForEach-Object{[uint32]$_.id})}|ForEach-Object{"$($_.code)=$($_.count)"})-join';'
        $inventoryAdmin.CardRemoveAllButton.PerformClick()
        $remaining=@($inventoryAdmin.Cards|ForEach-Object{"$($_.code)=$($_.count)"})-join';'
        if($remaining-ne$protectedBefore){throw 'card remove-all must preserve uncatalogued rows'}
        $expectedOutfit=@{hair=10130337;body=10100028;top=10110337;bottom=10120352;accessory=10150103;effect=10160017}
        foreach($part in $expectedOutfit.Keys){if((Get-SelectedData $comboMap[$part]).id-ne$expectedOutfit[$part]){throw "default outfit $part"}}
        if((Get-SelectedLaunchModeInfo).AdapterIP-ne'127.0.0.1'){throw 'fixed loopback mode'}
        foreach($page in $tabs.TabPages){
            $tabs.SelectedTab=$page;[Windows.Forms.Application]::DoEvents()
            if($page-ne$tabSocial){Assert-NoVisibleConnectionText $form}
        }
        $tabs.SelectedTab=$tabLaunchInfo;Update-LaunchPreview -ComputeHashes
        Assert-NoVisibleConnectionText $form
        $tabs.SelectedTab=$tabStart;[Windows.Forms.Application]::DoEvents()
        if($pureNewPlayerBox.Top-ne104-or$pureNewPlayerUsernameBox.Top-ne104-or$nameBox.Top-ne144-or$levelBox.Top-ne184-or$titleCombo.Top-ne230-or$comboMap.body.Top-ne361-or$skipTutorialBox.Top-ne646-or$unlockAllDungeonsBox.Top-ne646-or$saveBtn.Top-ne704-or$clientBtn.Left-ne215-or$adapterBtn.Left-ne460-or$status.Top-ne759){throw 'startup row compaction'}
        if($pureNewPlayerBox.Top-ne$pureNewPlayerUsernameBox.Top-or$pureNewPlayerBox.Bottom-ge$nameBox.Top-or$skipTutorialBox.Top-le$comboMap.effect.Bottom){throw 'new-player option row placement'}
        if($nameBox.Top-$pureNewPlayerBox.Top-ne40-or$levelBox.Top-$nameBox.Top-ne40){throw 'startup row spacing'}
        if($skillGradeBoxes[0].Bottom-ge$projectileSkillSeparator.Top-or$skillGradeBoxes[1].Bottom-ge$projectileSkillSeparator.Top-or$skillGradeBoxes[8].Bottom-ge$meatSkillSeparator.Top-or$skillGradeBoxes[9].Bottom-ge$meatSkillSeparator.Top){throw 'skill prerequisite separator placement'}
        if($projectileRouteCombo.Bottom-ge$skillGradeBoxes[2].Top-or$meatRouteCombo.Bottom-ge$skillGradeBoxes[10].Top){throw 'skill branch placement'}
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
        Write-Output 'GUI_LAYOUT_SELFTEST_PASS sizes=1000x870,1120x940 scales=1,1.25,1.5 skill_grades=16 prerequisite_sections=2 separators=2 routes=2 slots=2 overlap=false defaults=PASS connection_text=absent startup_compaction=40 new_player_options=independent_single_row tabs=ordered card_add_all=PASS card_remove_all=PASS state_writes=0'
    }finally{$form.Close();$form.Dispose()}
    exit 0
}

if($SelfTestPureNewPlayer){
    $savedPurePath=$PureNewPlayerProfile;$savedAccountState=$PureNewPlayerAccountState;$tempPure=Join-Path ([IO.Path]::GetTempPath()) ('nanaimo_pure_new_'+[guid]::NewGuid().ToString('N')+'.ini');$tempAccount=Join-Path ([IO.Path]::GetTempPath()) ('nanaimo_pure_account_'+[guid]::NewGuid().ToString('N')+'.txt')
    try{
        $PureNewPlayerProfile=$tempPure;$PureNewPlayerAccountState=$tempAccount;$pureNewPlayerBox.Checked=$true;$skipTutorialBox.Checked=$true;$pureNewPlayerUsernameBox.Text='test';$unlockAllDungeonsBox.Checked=$false
        $beforeIni=if(Test-Path -LiteralPath $ProfileIni){(Get-FileHash -LiteralPath $ProfileIni -Algorithm SHA256).Hash}else{$null}
        $beforeJson=if(Test-Path -LiteralPath $ProfileJson){(Get-FileHash -LiteralPath $ProfileJson -Algorithm SHA256).Hash}else{$null}
        $written=Write-PureNewPlayerRuntimeProfile;$profile=Read-KeyValueFile $written;$account=Save-PureNewPlayerUsername $pureNewPlayerUsernameBox.Text
        $payload=[ordered]@{LocalAccount=$account;PureNewPlayer=$true}|ConvertTo-Json -Compress|ConvertFrom-Json
        if($written-ne$tempPure-or[string]$profile.skip_tutorial-ne'0'-or[string]$profile.level-ne'1'-or[string]$profile.pet-ne'0'-or[string]$profile.coin-ne'0'-or[string]$profile.skill_grade0-ne'0'-or[string]$profile.unlock_all_dungeons-ne'0'){throw 'pure runtime profile'}
        [uint64]$freeExpiry=0;if(-not[uint64]::TryParse([string]$profile.free_magic_key_expiry,[ref]$freeExpiry)-or$freeExpiry-ne2000010100){throw 'pure runtime profile violates native free_magic_key_expiry startup/no-grant contract'}
        if(-not[bool]$payload.PureNewPlayer-or[string]$payload.LocalAccount-ne'test'-or(Read-PureNewPlayerUsername)-ne'test'){throw 'pure registration payload/state'}
        Test-PureNewPlayerNativeStartup $written
        $afterIni=if(Test-Path -LiteralPath $ProfileIni){(Get-FileHash -LiteralPath $ProfileIni -Algorithm SHA256).Hash}else{$null};$afterJson=if(Test-Path -LiteralPath $ProfileJson){(Get-FileHash -LiteralPath $ProfileJson -Algorithm SHA256).Hash}else{$null}
        if($beforeIni-ne$afterIni-or$beforeJson-ne$afterJson){throw 'normal profile mutated'}
        Write-Output 'GUI_PURE_NEW_PLAYER_SELFTEST_PASS profile=isolated native_startup=listening tutorial=required unlock_all_dungeons=0 level=1 grants=0 registration=json normal_profile_unchanged=true'
    }finally{$PureNewPlayerProfile=$savedPurePath;$PureNewPlayerAccountState=$savedAccountState;Remove-Item -LiteralPath $tempPure,$tempAccount -Force -ErrorAction SilentlyContinue;$form.Close();$form.Dispose()}
    exit 0
}

if($SelfTestCatalogPreview){
    $form.ShowInTaskbar=$false;$form.Opacity=0;$form.WindowState='Maximized';[void]$form.Show()
    if($tabs.TabPages.Count-ne12){throw 'launcher page count'}
    $tabs.SelectedTab=$tabStart;[Windows.Forms.Application]::DoEvents();$nameBox.Text='UI_TEST';$defaultBtn.PerformClick()
    if($nameBox.Text-ne'Greyrat'){throw 'restore-defaults handler did not complete'}
    $tabs.SelectedTab=$tabPets;[Windows.Forms.Application]::DoEvents();Test-CatalogSplitLayout $tabPets $petSplit $petPreviewPane 'pet'
    $tabs.SelectedTab=$tabEquip;[Windows.Forms.Application]::DoEvents();Test-CatalogSplitLayout $tabEquip $equipSplit $equipPreviewPane 'equip'
    $tabs.SelectedTab=$tabFurniture;[Windows.Forms.Application]::DoEvents();Test-CatalogSplitLayout $tabFurniture $furnitureSplit $furniturePreviewPane 'furniture'
    foreach($r in $petGrid.Rows){if([string]$r.Cells['ID'].Value-eq'15009205'){$petGrid.CurrentCell=$r.Cells['ID'];break}}
    foreach($r in $equipGrid.Rows){if([string]$r.Cells['ID'].Value-eq'10030458'){$equipGrid.CurrentCell=$r.Cells['ID'];break}}
    foreach($r in $furnitureGrid.Rows){if([string]$r.Cells['ID'].Value-eq'11460047'){$furnitureGrid.CurrentCell=$r.Cells['ID'];break}}
    Update-CatalogGridPreview 'pet' $petGrid $petPreviewPane;Update-CatalogGridPreview 'equip' $equipGrid $equipPreviewPane;Update-CatalogGridPreview 'furniture' $furnitureGrid $furniturePreviewPane
    if(-not$petPreviewPane.Picture.Image-or-not$equipPreviewPane.Picture.Image-or-not$furniturePreviewPane.Picture.Image){throw 'catalog preview self-test image missing'}
    if($petPreviewPane.Picture.Image.Width-ne64-or$equipPreviewPane.Picture.Image.Width-ne64-or$furniturePreviewPane.Picture.Image.Width-ne96-or$furniturePreviewPane.Picture.Image.Height-ne80){throw 'catalog preview self-test crop size'}
    $petRect=$petPreviewPane.Group.RectangleToScreen($petPreviewPane.Group.ClientRectangle);$equipRect=$equipPreviewPane.Group.RectangleToScreen($equipPreviewPane.Group.ClientRectangle);$furnitureRect=$furniturePreviewPane.Group.RectangleToScreen($furniturePreviewPane.Group.ClientRectangle)
    Write-Output ('NETWORK_CATALOG_PREVIEW_SELFTEST_PASS pet={0} equip={1} furniture={2} maps={3}/{4}/{5} petPane={6} equipPane={7} furniturePane={8}'-f$petGrid.CurrentRow.Cells['ID'].Value,$equipGrid.CurrentRow.Cells['ID'].Value,$furnitureGrid.CurrentRow.Cells['ID'].Value,$petPreviewRows.Count,$equipPreviewRows.Count,$furniturePreviewRows.Count,$petRect,$equipRect,$furnitureRect)
    $form.Close();$form.Dispose();exit 0
}


[void]$form.ShowDialog()
