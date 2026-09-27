"""Build the native Boss-contact lookup from the shared combat catalog."""
from pathlib import Path
import struct
import collections

ROOT = Path(__file__).resolve().parents[1]

def generate():
    source=next((ROOT/"adapter_runtime").rglob("dungeon_combat_catalog.bin"))
    data=source.read_bytes(); offset=4
    if data[:4] != b"DCC7":
        raise ValueError("Invalid combat catalog signature")
    def read(fmt):
        nonlocal offset
        values=struct.unpack_from(fmt,data,offset);offset+=struct.calcsize(fmt);return values
    def count():return read("<i")[0]
    def key():return read("<5BH")
    def template():return read("<iB4i")
    for _ in range(count()):key();template()
    bosses={}
    for _ in range(count()):
        k=key();bosses[k]=read("<iiB")
    values=collections.defaultdict(set)
    for _ in range(count()):
        k=key();component=read("<BBi");row=template()
        if 0 < row[3] <= 65535:
            values[k[:5]+component].add(row[3])
    rows=[(*k,next(iter(v))) for k,v in sorted(values.items()) if len(v)==1]
    # Collision selectors address the selected SMMO runtime vector.
    # Resource UIDs belong to a different namespace.
    contacts = []
    for _ in range(count()):
        k = key(); read("<H"); row = template()
        if not 0 <= row[3] <= 65535:
            raise ValueError("Collision attack exceeds the carrier")
        if k[4] % 3 == k[3]:
            contacts.append((*k, row[3]))
    if offset != len(data):
        raise ValueError("Unexpected combat catalog trailing data")
    ranges = []
    for row in sorted(contacts):
        scope, selector, attack = row[:5], row[5], row[6]
        if ranges and ranges[-1][:5] == scope and ranges[-1][6] + 1 == selector and ranges[-1][7] == attack:
            ranges[-1] = (*scope, ranges[-1][5], selector, attack)
        else:
            ranges.append((*scope, selector, selector, attack))
    ordinary = ["/* Selected-scope runtime contact damage ranges. */",
        "#ifndef NANAIMO_MONSTER_CONTACT_CATALOG_INC", "#define NANAIMO_MONSTER_CONTACT_CATALOG_INC",
        "struct monster_contact_row { unsigned hd,ep,dg,stage,slot,first,last,attack; };",
        "static const struct monster_contact_row monster_contact_rows[]={"]
    ordinary += [" {" + ",".join(str(v) + "u" for v in row) + "}," for row in ranges]
    ordinary += ["};", "#define MONSTER_CONTACT_ROW_COUNT " + str(len(ranges)) + "u", "#endif", ""]
    (ROOT/"release/components/stage_damage/monster_contact_catalog.inc").write_text("\n".join(ordinary),encoding="ascii")
    lines=["/* Shared combat-resource Boss contact values. */",
           "#ifndef NANAIMO_BOSS_CONTACT_CATALOG_INC", "#define NANAIMO_BOSS_CONTACT_CATALOG_INC",
           "struct boss_contact_row { unsigned hd,ep,dg,stage,slot,mode,child,ordinal,attack; };",
           "static const struct boss_contact_row boss_contact_rows[]={"]
    lines += [" {"+",".join(str(v)+"u" for v in row)+"}," for row in rows]
    lines += ["};", "#define BOSS_CONTACT_ROW_COUNT "+str(len(rows))+"u", "#endif", ""]
    target=ROOT/"release/components/stage_damage/boss_contact_catalog.inc"
    target.write_text("\n".join(lines),encoding="ascii")
    print("CONTACT_CATALOG_PASS boss_rows="+str(len(rows))+" monster_ranges="+str(len(ranges))+" runtime_selectors="+str(len(contacts)))
if __name__=="__main__":generate()
