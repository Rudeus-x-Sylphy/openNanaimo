"""Kobe outfit metadata, complete sprite frames, inherited stats and selection checks."""
import json
from pathlib import Path
import subprocess
import unittest

import prepare_kobe_wardrobe as kobe
import package_wardrobe_resources as package


class KobeWardrobeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.recipe = json.loads((kobe.ROOT / 'manifest/kobe_wardrobe.json').read_text('utf-8'))
        cls.bundle, cls.payloads = package.read()
        _, cls.rows = kobe.audit.read_catalog(cls.payloads['ava._D1'], 'clothing',
                                             kobe.wardrobe.crypto.CN_KEY, 'gbk')

    def test_five_male_items_and_complete_bonuses(self):
        self.assertEqual(self.recipe['text'], 'Mamba Out')
        self.assertEqual(self.recipe['hair'], 0)
        self.assertFalse(self.recipe['runtime_acceptance'])
        for part, (code, _, template, _) in kobe.PARTS.items():
            row = self.rows[code]
            self.assertEqual(code // 100000 % 10, 1)
            self.assertNotIn(code - 100000, self.rows)
            self.assertEqual(row[1], kobe.NAMES[part])
            self.assertEqual(row[7:13], ['0'] * 6)
            kobe.wardrobe.validate_row('clothing', row)
            source = 10130451 if part == 'effect' else template
            self.assertEqual(row[13:22], self.rows[source][13:22])

    def test_actual_existing_record_fields_are_preserved(self):
        key = kobe.wardrobe.crypto.CN_KEY
        def fields(raw):
            return kobe.wardrobe.crypto.decrypt(raw, key).decode('gbk').split('#')
        legacy = {code: row for code, row in self.rows.items()
                  if code not in {v[0] for v in kobe.PARTS.values()}}
        baseline = kobe.wardrobe.catalog_bytes('clothing', ['AVATA', '20070311', str(len(legacy))], legacy)
        before, after = fields(baseline), fields(self.payloads['ava._D1'])
        before_rows = {int(before[i+3]): before[i:i+25] for i in range(4, 4+25*len(legacy), 25)}
        after_rows = {int(after[i+3]): after[i:i+25] for i in range(4, 4+25*len(self.rows), 25)}
        # Legacy-view dates prefix the next row. Compare actual item fields;
        # full 25-field movement and date ownership are covered by shortcut-shop.
        for code, row in before_rows.items():
            self.assertEqual(row[:24], after_rows[code][:24])

    def test_pack_and_loose_models_preserve_every_action_and_pivot(self):
        images = kobe.audit.read_pack(self.payloads['images/Avatar_images_im3.pack'])
        favs = kobe.audit.read_pack(self.payloads['images/Avatar_images_fav.pack'])
        icons = kobe.audit.read_pack(self.payloads['images/AvatarIcon.pack'])
        for part, (code, key, _, source) in kobe.PARTS.items():
            self.assertEqual(kobe.codec.decode_im3(icons[key]).width, 46)
            if source is None:
                continue
            original, model = map(kobe.codec.decode_im3, (images[source], images[key]))
            self.assertEqual(model.sequences, original.sequences)
            self.assertEqual(model.pivot, original.pivot)
            self.assertEqual((model.width, model.height, model.frame_width, model.frame_height),
                             (300, 1500, 100, 100))
            self.assertEqual(favs[key], favs[source])
            self.assertEqual(self.payloads[self.rows[code][6].replace('\\', '/')], images[key])
            image, before = model.rgba(), original.rgba()
            for frame in range(45):
                x, y = frame % 3 * 100, frame // 3 * 100
                box = (x, y, x + 100, y + 100)
                self.assertEqual(image.crop(box).getbbox() is not None,
                                 before.crop(box).getbbox() is not None)
            if part in ('body', 'top', 'bottom'):
                self.assertEqual(image.getchannel('A').tobytes(), before.getchannel('A').tobytes())

    def test_basketball_stays_on_original_hand_for_every_pose(self):
        images = kobe.audit.read_pack(self.payloads['images/Avatar_images_im3.pack'])
        original = kobe.codec.decode_im3(images[910043]).rgba()
        basketball = kobe.codec.decode_im3(images[950904]).rgba()
        for frame in range(45):
            x, y = frame % 3 * 100, frame // 3 * 100
            box = (x, y, x + 100, y + 100)
            before, after = original.crop(box).getbbox(), basketball.crop(box).getbbox()
            if before:
                self.assertLessEqual(abs((before[0] + before[2]) - (after[0] + after[2])), 2)
                self.assertLessEqual(abs((before[1] + before[3]) - (after[1] + after[3])), 2)

    def test_effect_dependency_and_static_text_frame(self):
        effect = kobe.codec.decode_eff(self.payloads[kobe.EFFECT])
        self.assertEqual(len(effect['layers']), 1)
        self.assertEqual(effect['layers'][0]['name'], Path(kobe.EFFECT_IMAGE).name)
        frame = effect['layers'][0]['tracks'][11]
        self.assertEqual(frame, [(0., 0., 0, 130)])
        image = kobe.codec.decode_im3(self.payloads[kobe.EFFECT_IMAGE])
        self.assertEqual((image.width, image.height), (160, 40))
        self.assertEqual(image.sequences, b'')
        self.assertEqual(self.bundle['counts']['clothing'], 4790)
        self.assertEqual(self.bundle['custom_addons']['kobe']['recipe_sha256'],
                         kobe.audit.identity(kobe.encoded(self.recipe))['sha256'])

    def test_release_deploys_the_catalog_and_preview_generation_together(self):
        release = json.loads((kobe.ROOT / 'manifest/open_release_manifest.json').read_text('utf-8'))
        for name in ('gui_launcher/data/equipment.json', 'gui_launcher/data/inventory_clothing.json',
                     'gui_launcher/data/previews/equipment_icons.json',
                     'gui_launcher/data/previews/equipment_icons.png',
                     'gui_launcher/kobe_outfit.ps1', 'manifest/kobe_wardrobe.json'):
            self.assertIn(name, release['critical_files'])
            actual = kobe.audit.identity((kobe.ROOT / name).read_bytes())
            declared = release['critical_files'][name]
            self.assertEqual(actual['size'], declared['size'])
            self.assertEqual(actual['sha256'].upper(), declared['sha256'].upper())

    def test_launcher_selection_is_male_and_validates_before_changes(self):
        command = r'''
        . './gui_launcher/kobe_outfit.ps1'
        $ids=Get-KobeOutfitIds;$combos=@{}
        foreach($part in $ids.Keys){$combos[$part]=[pscustomobject]@{DataSource=@([pscustomobject]@{Id=[uint32]999},[pscustomobject]@{Id=$ids[$part]});SelectedIndex=0}}
        try{Set-KobeOutfit 0 $combos;throw 'female selection accepted'}catch{if($_.Exception.Message-eq'female selection accepted'){throw}}
        foreach($combo in $combos.Values){if($combo.SelectedIndex-ne0){throw 'selection changed'}}
        $combos.effect.DataSource=@([pscustomobject]@{Id=[uint32]999})
        try{Set-KobeOutfit 1 $combos;throw 'partial catalog accepted'}catch{if($_.Exception.Message-eq'partial catalog accepted'){throw}}
        foreach($combo in $combos.Values){if($combo.SelectedIndex-ne0){throw 'partial selection'}}
        $combos.effect.DataSource=@([pscustomobject]@{Id=[uint32]999},[pscustomobject]@{Id=$ids.effect})
        Set-KobeOutfit 1 $combos
        foreach($combo in $combos.Values){if($combo.SelectedIndex-ne1){throw 'outfit selection differs'}}
        Write-Output 'KOBE_SELECTION_PASS'
        '''
        result = subprocess.run(['powershell', '-NoProfile', '-Command',
                                 "$ErrorActionPreference='Stop';" + command],
                                cwd=kobe.ROOT, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn('KOBE_SELECTION_PASS', result.stdout)


if __name__ == '__main__':
    unittest.main()
