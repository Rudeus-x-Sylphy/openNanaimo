"""Generate native flight-item definitions."""
from pathlib import Path
import prepare_hero_dragon as crypto

ROOT = Path(__file__).resolve().parents[1]


def definitions():
    data = (ROOT / 'adapter_runtime/\u8d44\u6e90/\u6570\u636e/gi._D6').read_bytes()
    fields = crypto.decrypt(data, crypto.CN_KEY).decode('gbk').split('#')
    if fields[:3] != ['GAMEFOODITEM', '20080222', '121']:
        raise ValueError('Unsupported flight-item catalog')
    rows = []
    for start in range(3, 3 + int(fields[2]) * 18, 18):
        row = fields[start:start + 18]
        code, team, kind, seconds = (int(row[i]) for i in (0, 6, 7, 8))
        if code // 1000000 != 21 or team not in (0, 1) or not 1 <= kind <= 11 or not 0 <= seconds <= 120:
            raise ValueError('Invalid flight-item definition')
        rows.append((code, team, kind, seconds * 1000))
    if len(set(r[0] for r in rows)) != len(rows):
        raise ValueError('Duplicate flight-item code')
    return sorted(rows)


def render():
    lines = ['/* Generated numeric flight-item definitions. */',
             'struct flight_item_definition { unsigned code,team,kind,duration_ms; };',
             'static const struct flight_item_definition flight_item_definitions[] = {']
    lines += ['    {%uu,%uu,%uu,%uu},' % row for row in definitions()]
    lines += ['};', '']
    return '\n'.join(lines)


if __name__ == '__main__':
    output = ROOT / 'release/components/flight_items/flightitem_data.inc'
    output.write_text(render(), encoding='ascii', newline='\n')
    print('FLIGHT_ITEM_CATALOG_READY', len(definitions()))
