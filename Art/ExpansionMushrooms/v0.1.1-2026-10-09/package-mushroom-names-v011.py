"""Validate and retain the v0.1.1 metadata-only naming release."""
import hashlib
import json
import re
import shutil
import zipfile
from pathlib import Path

OUT = Path(__file__).resolve().parent
ART = Path('C:/Users/집/Desktop/codex/MoreMushrooms/Art/ExpansionMushrooms')
BASE = ART / 'v0.1-2026-10-09'
PATCH = ART / 'v0.1.1-2026-10-09'
ARCHIVE = ART / 'MoreMushrooms-names-v0.1.1-2026-10-09.zip'

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def gallery_data(path):
    source = path.read_text(encoding='utf-8')
    match = re.search(r'<script type="application/json" id="mm-example-data">(.*?)</script>', source, re.S)
    return json.loads(match.group(1))

original = gallery_data(OUT / 'mushroom-examples-v01.html')
updated = gallery_data(OUT / 'mushroom-examples-v011.html')
image_keys = ('photo', 'low', 'medium', 'full', 'a', 'b')
assert [{key: row[key] for key in ('id', 'scientific') + image_keys} for row in original] == [{key: row[key] for key in ('id', 'scientific') + image_keys} for row in updated]
assert {row['id'] for row in original if row['ko'] != next(new['ko'] for new in updated if new['id'] == row['id'])} == {'Cubensis', 'ZombieAntFungus'}
inventory = json.loads((PATCH / 'BASELINE-PRESERVED.json').read_text(encoding='utf-8'))['file_sha256']
current = {str(path.relative_to(BASE)): digest(path) for path in BASE.rglob('*') if path.is_file()}
assert inventory == current
qa = json.loads((PATCH / 'GALLERY-QA.json').read_text(encoding='utf-8'))
assert qa['species'] == 31 and not qa['js_errors'] and all(not row['overflow'] for row in qa['layouts'])
assert (OUT / 'mushroom-examples-v011.html').stat().st_size < 1_000_000

for name in ('rename-mushroom-korean-v011.py', 'check-mushroom-examples-v011.js', 'package-mushroom-names-v011.py'):
    shutil.copyfile(OUT / name, PATCH / name)

report = {
    'version': '0.1.1', 'date': '2026-10-09',
    'changed_names': {'Cubensis': '주사위환각버섯', 'ZombieAntFungus': '개미동충하초'},
    'scientific_identities_unchanged': True, 'all_186_embedded_image_uris_unchanged': True,
    'baseline_art_unchanged': True, 'baseline_art_files': len(current),
    'existing_runtime_files_unchanged': 108,
    'gallery_checks': {'species': 31, 'growth_stages': 124, 'js_errors': [], 'widths': [320, 360, 736]},
    'visual_review': {'Cubensis': 'passed', 'ZombieAntFungus': 'passed'},
    'gallery_sha256': digest(OUT / 'mushroom-examples-v011.html'),
    'preview_sha256': digest(PATCH / 'preview.html'),
    'release_type': 'Korean names and preview only; uses unchanged v0.1 art; no runtime installation'
}
(PATCH / 'FINAL-QA.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
pointer = {
    'version': '0.1.1', 'date': '2026-10-09',
    'release_type': 'Korean display-name metadata patch',
    'catalog': 'v0.1.1-2026-10-09/species.json',
    'baseline_art_version': '0.1', 'asset_base': 'v0.1-2026-10-09',
    'preview': 'v0.1.1-2026-10-09/preview.html',
    'name_changes': 'v0.1.1-2026-10-09/NAME-CHANGES.json',
    'archive': ARCHIVE.name,
    'runtime_mod_version': '0.4.0'
}
with (ART / 'CURRENT.json').open('x', encoding='utf-8') as handle:
    handle.write(json.dumps(pointer, ensure_ascii=False, indent=2) + '\n')
files = sorted(path for path in PATCH.rglob('*') if path.is_file())
with zipfile.ZipFile(ARCHIVE, 'x', zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    for path in files:
        archive.write(path, path.relative_to(ART).as_posix())
    archive.write(ART / 'CURRENT.json', 'CURRENT.json')
with zipfile.ZipFile(ARCHIVE) as archive:
    assert archive.testzip() is None
    assert len(archive.namelist()) == len(files) + 1
checksum = digest(ARCHIVE)
with ARCHIVE.with_suffix('.sha256.txt').open('x', encoding='utf-8') as handle:
    handle.write(checksum + '  ' + ARCHIVE.name + '\n')
print(json.dumps({'version': '0.1.1', 'archive': str(ARCHIVE), 'bytes': ARCHIVE.stat().st_size, 'sha256': checksum, 'files': len(files) + 1, 'zip_crc': 'passed', 'baseline_art_unchanged': True}, ensure_ascii=False))
