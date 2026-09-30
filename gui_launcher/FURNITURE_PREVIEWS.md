# Furniture previews

The furniture preview catalog maps encrypted `inter._D3` icon paths to numeric
keys in `images/InteriorIcon.pack`. Packed IM3 images use ARGB1555 and occupy
96×80 slots in `furniture_icons.png`; `furniture_icons.json` records each icon key,
availability and atlas coordinates.

The generator requires PyCryptodome and Pillow. Entries with an unavailable
resource retain `available: false`, coordinates `-1,-1`, and a specific `failure`
category. The GUI presents usable entries as decoded resource thumbnails.
