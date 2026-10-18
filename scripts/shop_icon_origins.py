"""Exact resource corrections for the shop's top-left icon placement."""
import hashlib
import struct

PACK = 'images/AvatarIcon.pack'
PREVIOUS_PACK = {'size': 9514469,
    'sha256': '5719dafd319ce3b451011492059d336ddce73f9e973cb197e41ba4cb77257d7a'}
CORRECTED_PACK = {'size': 9514469,
    'sha256': 'cdfb4bd52273b19dfc476d7f37f59886616b1bd97a4c928a7a8b9b5edd345344'}
RECORDS = {
    10127: ('8373f0e4980f980d73f2dae33d90d70180e37cf588ffd9ca00d036f2ab9bf0dd',
            'f5d1939ae0ab2e2b929fc4a9e72f5a7397fde4e7be7ccc2247240e2e334d0ab8'),
    300272: ('69586c58de4fefb890c052700193f0dfccb1ed0e582633bb69191f8d0b7d77cc',
             '96f008af58edbdb460611f35483dbf0161198a723287cb0795c276630ea2499d'),
}


def correct_record(key, data):
    if key not in RECORDS:
        return data
    before, after = RECORDS[key]
    digest = hashlib.sha256(data).hexdigest()
    if digest == after:
        return data
    if digest != before:
        raise ValueError(f'Unrecognized shop icon {key}')
    if (struct.unpack_from('<9I', data) != (0, 2, 4612, 46, 46, 46, 46, 8, 0)
            or struct.unpack_from('<ff', data, 36) != (23.0, 23.0)):
        raise ValueError(f'Invalid shop icon header {key}')
    result = data[:36] + bytes(8) + data[44:]
    if hashlib.sha256(result).hexdigest() != after:
        raise ValueError(f'Shop icon output differs {key}')
    return result


def verify_records(records):
    for key, (_, expected) in RECORDS.items():
        if key not in records or hashlib.sha256(records[key]).hexdigest() != expected:
            raise ValueError(f'Shop icon origin correction required: {key}')
