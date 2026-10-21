function Get-KobeOutfitIds {
    return @{hair=[uint32]0;body=[uint32]10100901;top=[uint32]10110901;bottom=[uint32]10120901;accessory=[uint32]10150901;effect=[uint32]10160901}
}
function Set-KobeOutfit([int]$gender,$combos) {
    if($gender-ne1){throw '科比套装仅限男角色。'}
    $ids=Get-KobeOutfitIds
    $indices=@{}
    foreach($part in $ids.Keys){
        if(-not$combos.ContainsKey($part)){throw "装扮部位缺失：$part"}
        $data=$combos[$part].DataSource
        for($i=0;$i-lt$data.Count;$i++){if([uint32]$data[$i].Id-eq$ids[$part]){$indices[$part]=$i;break}}
        if(-not$indices.ContainsKey($part)){throw "装扮目录缺失：$($ids[$part])"}
    }
    foreach($part in $ids.Keys){$combos[$part].SelectedIndex=$indices[$part]}
}
