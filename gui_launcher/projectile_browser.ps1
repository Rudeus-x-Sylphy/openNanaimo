# Optional client-local playground. Configuration is separate from character saves.
function Read-ProjectileSettings([string]$path){
    $result=@{enabled=0;resource='nanaimo_basketball.pon';reverse_direction=1}
    if(Test-Path -LiteralPath $path){
        foreach($line in [IO.File]::ReadAllLines($path)){
            if($line-match'^(enabled|resource|reverse_direction)=(.*)$'){$result[$matches[1]]=$matches[2]}
        }
    }
    foreach($key in @('enabled','reverse_direction')){
        if([string]$result[$key]-notmatch'^[01]$'){throw "Invalid projectile setting: $key"}
        $result[$key]=[int]$result[$key]
    }
    if([string]$result.resource-notmatch'^[A-Za-z0-9_][A-Za-z0-9_.]{0,57}\.pon$'){throw 'Invalid projectile resource leaf'}
    return $result
}
function Write-ProjectileSettings([string]$path,$settings){
    if([string]$settings.resource-notmatch'^[A-Za-z0-9_][A-Za-z0-9_.]{0,57}\.pon$'){throw 'Invalid projectile resource leaf'}
    foreach($key in @('enabled','reverse_direction')){if([string]$settings[$key]-notmatch'^[01]$'){throw "Invalid projectile setting: $key"}}
    $lines=@('[Flamethrower]',"enabled=$($settings.enabled)","resource=$($settings.resource)","reverse_direction=$($settings.reverse_direction)")
    $parent=Split-Path $path -Parent;[IO.Directory]::CreateDirectory($parent)|Out-Null
    $tmp=$path+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    try{
        [IO.File]::WriteAllLines($tmp,$lines,[Text.Encoding]::ASCII)
        if(Test-Path -LiteralPath $path){[IO.File]::Replace($tmp,$path,[NullString]::Value)}else{[IO.File]::Move($tmp,$path)}
    }finally{if(Test-Path -LiteralPath $tmp){Remove-Item -LiteralPath $tmp -Force}}
}
function Assert-ProjectileClientStopped([string]$root){
    $client=[IO.Path]::GetFullPath((Join-Path $root 'game.exe'))
    $running=@(Get-Process -Name game -ErrorAction SilentlyContinue|Where-Object{$_.Path-eq$client})
    if($running.Count){throw '请先退出这个目录的游戏客户端再保存子弹配置；不需要停止适配器。'}
}
function Initialize-ProjectileBrowser($tabs,[string]$root,[string]$configRoot){
    $data=Join-Path $root 'gui_launcher\data'
    $catalog=@((Get-Content -LiteralPath (Join-Path $data 'projectiles.json') -Raw -Encoding UTF8|ConvertFrom-Json).items)
    $aliases=Get-Content -LiteralPath (Join-Path $data 'projectile_aliases.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $byFile=@{};foreach($row in $catalog){$byFile[[string]$row.file]=$row}
    $previewRows=Get-Content -LiteralPath (Join-Path $data 'previews\projectile_frames.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $previews=@{};foreach($row in $previewRows){$previews[[string]$row.file]=$row}
    $path=Join-Path $configRoot 'nanaimo_projectile.ini'
    $settings=Read-ProjectileSettings $path
    $selected=[string]$settings.resource
    foreach($prop in $aliases.PSObject.Properties){if([string]$prop.Value-eq$selected){$selected=[string]$prop.Name;break}}
    if(-not$byFile.ContainsKey($selected)){throw "Unknown configured projectile: $selected"}
    $page=New-Object Windows.Forms.TabPage;$page.Text='子弹 DIY';$tabs.TabPages.Add($page)
    $header=New-Object Windows.Forms.Panel;$header.Dock='Top';$header.Height=148;$page.Controls.Add($header)
    $enabled=New-Object Windows.Forms.CheckBox;$enabled.Text='启用 V 键连续发射（90ms；松开即停）';$enabled.SetBounds(14,12,370,27);$enabled.Checked=($settings.enabled-eq1);$header.Controls.Add($enabled)
    $reverse=New-Object Windows.Forms.CheckBox;$reverse.Text='反向运动/朝向（篮球及七个兼容项；实验性）';$reverse.SetBounds(400,12,480,27);$reverse.Checked=($settings.reverse_direction-eq1);$header.Controls.Add($reverse)
    $current=New-Object Windows.Forms.Label;$current.SetBounds(14,43,1010,23);$current.Anchor='Top,Left,Right';$header.Controls.Add($current)
    $save=New-Object Windows.Forms.Button;$save.Text='保存子弹设置';$save.SetBounds(14,74,150,32);$header.Controls.Add($save)
    $ball=New-Object Windows.Forms.Button;$ball.Text='选自制篮球';$ball.SetBounds(175,74,145,32);$header.Controls.Add($ball)
    $use=New-Object Windows.Forms.Button;$use.Text='选中列表子弹';$use.SetBounds(331,74,145,32);$header.Controls.Add($use)
    $hint=New-Object Windows.Forms.Label;$hint.SetBounds(14,112,1020,32);$hint.Anchor='Top,Left,Right';$hint.Text='搜索名称/怪物/资源 → 双击选择 → 保存 → 重新进入游戏。独立客户端设置；人物存档单独管理，联机效果与伤害同步按场景验收。';$header.Controls.Add($hint)
    # Keep the list/search and preview in separate columns below the full-width header.
    $body=New-Object Windows.Forms.Panel;$body.Dock='Fill';$page.Controls.Add($body);$body.BringToFront()
    $listPanel=New-Object Windows.Forms.Panel;$listPanel.Dock='Fill'
    $searchRow=New-Object Windows.Forms.Panel;$searchRow.Dock='Top';$searchRow.Height=38;$searchRow.Padding=New-Object Windows.Forms.Padding(8,7,8,7);$listPanel.Controls.Add($searchRow)
    $searchLabel=New-Object Windows.Forms.Label;$searchLabel.Text='搜索';$searchLabel.Dock='Left';$searchLabel.Width=46;$searchLabel.TextAlign='MiddleLeft';$searchRow.Controls.Add($searchLabel)
    $search=New-Object Windows.Forms.TextBox;$search.Dock='Fill';$searchRow.Controls.Add($search);$search.BringToFront()
    $previewPanel=New-Object Windows.Forms.Panel;$previewPanel.Dock='Right';$previewPanel.Width=340;$body.Controls.Add($previewPanel);$body.Controls.Add($listPanel);$listPanel.BringToFront()
    $picture=New-Object Windows.Forms.PictureBox;$picture.Dock='Top';$picture.Height=280;$picture.SizeMode='Zoom';$picture.BackColor=[Drawing.Color]::FromArgb(42,42,42);$previewPanel.Controls.Add($picture)
    $detail=New-Object Windows.Forms.TextBox;$detail.Multiline=$true;$detail.ReadOnly=$true;$detail.ScrollBars='Vertical';$detail.Dock='Fill';$previewPanel.Controls.Add($detail);$detail.BringToFront()
    $grid=New-Object Windows.Forms.DataGridView;$grid.Dock='Fill';$grid.ReadOnly=$true;$grid.AllowUserToAddRows=$false;$grid.AllowUserToDeleteRows=$false;$grid.MultiSelect=$false;$grid.SelectionMode='FullRowSelect';$grid.RowHeadersVisible=$false;$grid.AutoSizeColumnsMode='Fill'
    foreach($col in @(@('style','样式'),@('file','PON资源'),@('monster','怪物/来源'))){[void]$grid.Columns.Add($col[0],$col[1])}
    $grid.Columns['file'].FillWeight=150;$listPanel.Controls.Add($grid);$grid.BringToFront()
    $script:DIY=@{Root=$root;Data=$data;Config=$path;Catalog=$catalog;ByFile=$byFile;Aliases=$aliases;Previews=$previews;Atlas=$null;Selected=$selected;Grid=$grid;Picture=$picture;Detail=$detail;Enabled=$enabled;Reverse=$reverse;Current=$current;Search=$search;Page=$page;Hint=$hint}
    $timer=New-Object Windows.Forms.Timer;$timer.Interval=250;$script:DIY.Timer=$timer
    $timer.add_Tick({$script:DIY.Timer.Stop();Update-ProjectileGrid})
    $search.add_TextChanged({$script:DIY.Timer.Stop();$script:DIY.Timer.Start()})
    $grid.add_SelectionChanged({if($script:DIY.Grid.SelectedRows.Count){Show-ProjectilePreview $script:DIY.Grid.SelectedRows[0].Tag}})
    $grid.add_CellDoubleClick({Select-ProjectileGridRow})
    $use.add_Click({Select-ProjectileGridRow})
    $ball.add_Click({$script:DIY.Selected='nanaimo_basketball.pon';Update-ProjectileCurrent;Show-ProjectilePreview $script:DIY.ByFile[$script:DIY.Selected]})
    $save.add_Click({try{
        Assert-ProjectileClientStopped $script:DIY.Root
        $resource=[string]$script:DIY.Selected
        $alias=$script:DIY.Aliases.PSObject.Properties[$resource]
        if($alias){$resource=[string]$alias.Value}
        Write-ProjectileSettings $script:DIY.Config @{enabled=[int]$script:DIY.Enabled.Checked;resource=$resource;reverse_direction=[int]$script:DIY.Reverse.Checked}
        $script:DIY.Hint.Text='子弹设置已保存。重新进入游戏后生效；篮球使用独立资源，原怪物炮弹不变。'
    }catch{[Windows.Forms.MessageBox]::Show($_.Exception.Message,'子弹设置未保存')|Out-Null}})
    Update-ProjectileCurrent;Update-ProjectileGrid
    $page.add_Disposed({$script:DIY.Timer.Stop();$script:DIY.Timer.Dispose();if($script:DIY.Picture.Image){$script:DIY.Picture.Image.Dispose()};if($script:DIY.Atlas){$script:DIY.Atlas.Dispose()}})
}
function Update-ProjectileCurrent {$script:DIY.Current.Text='当前选择：'+$script:DIY.Selected+' （开关/选择须点击“保存子弹设置”）'}
function Select-ProjectileGridRow {if($script:DIY.Grid.SelectedRows.Count){$script:DIY.Selected=[string]$script:DIY.Grid.SelectedRows[0].Tag.file;Update-ProjectileCurrent}}
function Update-ProjectileGrid {
    $grid=$script:DIY.Grid;$term=$script:DIY.Search.Text.Trim();$grid.SuspendLayout()
    try{$grid.Rows.Clear();foreach($row in $script:DIY.Catalog){
        $text=([string]$row.file)+' '+$row.style_label+' '+$row.monster_label+' '+$row.owner
        if($term-and$text.IndexOf($term,[StringComparison]::OrdinalIgnoreCase)-lt0){continue}
        $i=$grid.Rows.Add([string]$row.style_label,[string]$row.file,[string]$row.monster_label);$grid.Rows[$i].Tag=$row
        if($row.file-eq$script:DIY.Selected){$grid.Rows[$i].DefaultCellStyle.BackColor=[Drawing.Color]::LightGoldenrodYellow}
    }}finally{$grid.ResumeLayout()}
    if($grid.Rows.Count){$grid.Rows[0].Selected=$true;Show-ProjectilePreview $grid.Rows[0].Tag}
}
function Show-ProjectilePreview($row){
    if(-not$row){return}
    $d=$script:DIY;$picture=$d.Picture
    if($picture.Image){$old=$picture.Image;$picture.Image=$null;$old.Dispose()}
    $entry=$d.Previews[[string]$row.file];$mode='无可用代表图'
    if($row.file-eq'nanaimo_basketball.pon'){
        $picture.Image=[Drawing.Image]::FromFile((Join-Path $d.Data 'previews\basketball_spin.gif'));$mode='旋转篮球：16帧不同侧面 / 48×48；仅篮球本体，无喷口火星'
    }elseif($entry){
        if($entry.animated-and$entry.gif){$gif=Join-Path $d.Data ('previews\projectiles\'+[IO.Path]::GetFileName([string]$entry.gif));if(Test-Path -LiteralPath $gif){$picture.Image=[Drawing.Image]::FromFile($gif);$mode="代表图片GIF：$($entry.frame_count)帧（图像预览）"}}
        if(-not$picture.Image-and$entry.available){
            if(-not$d.Atlas){$d.Atlas=[Drawing.Image]::FromFile((Join-Path $d.Data 'previews\projectile_frames.png'))}
            $image=New-Object Drawing.Bitmap ([int]$entry.w),([int]$entry.h);$graphics=[Drawing.Graphics]::FromImage($image)
            try{$graphics.DrawImage($d.Atlas,(New-Object Drawing.Rectangle 0,0,([int]$entry.w),([int]$entry.h)),(New-Object Drawing.Rectangle ([int]$entry.x),([int]$entry.y),([int]$entry.w),([int]$entry.h)),[Drawing.GraphicsUnit]::Pixel)}finally{$graphics.Dispose()}
            $picture.Image=$image;$mode='静态代表帧（图像预览）'
        }
    }
    $mirror=if($row.file-eq'nanaimo_basketball.pon'-or$d.Aliases.PSObject.Properties[[string]$row.file]){'限定资源反向；按场景完成实机验收'}else{'原生方向；复杂轨迹按资源分别验收'}
    $d.Detail.Text=@($mode,'',('资源：'+$row.file),('样式：'+$row.style_label),('来源：'+$row.monster_label),("owner=$($row.owner)；行数=$($row.row_count)"),('PON原始攻击候选：'+($row.attack_values-join',')),'伤害采用当前适配器策略。','',('反向：'+$mirror),'','按住V发射；松开停止。切图/重建战斗对象后先松开V再按。','篮球为独立PON/EFF/IM3；辅助效果透明化，原炮弹保持原有内容。','复合弹幕/联机可见性/命中仍需实机验收。')-join"`r`n"
}
