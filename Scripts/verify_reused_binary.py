"""Verify a compatibility-only release reuses an unchanged, archived build."""
from pathlib import Path
import argparse
import hashlib
import json
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--previous-version', required=True)
    previous = parser.parse_args().previous_version
    release = ROOT / 'Releases' / ('v' + previous)
    manifest = json.loads((release / 'manifest.json').read_text(encoding='utf-8'))
    expected = manifest['files']['Assemblies/RimMushrooms.dll']
    actual = hashlib.sha256((ROOT / 'Assemblies/RimMushrooms.dll').read_bytes()).hexdigest()
    if actual != expected:
        raise SystemExit('Production DLL differs from the archived verified build.')
    with zipfile.ZipFile(release / ('MoreMushrooms-v' + previous + '-source.zip')) as archive:
        sources = {n.removeprefix('MoreMushrooms/') for n in archive.namelist() if n.startswith('MoreMushrooms/Source/') and n.endswith('.cs')}
        current = {p.relative_to(ROOT).as_posix() for p in (ROOT / 'Source').glob('*.cs')}
        if sources != current:
            raise SystemExit('Production source file set changed; rebuild instead of reusing a DLL.')
        for name in sorted(sources | {'MoreMushrooms.csproj'}):
            if archive.read('MoreMushrooms/' + name) != (ROOT / name).read_bytes():
                raise SystemExit('Production build input changed: ' + name)
    print(json.dumps({'status':'PASS','previousVersion':previous,'productionSources':len(sources),
                      'productionDllSha256':actual,'buildMode':'verified unchanged net472 binary; no new compilation claimed'}, indent=2))


if __name__ == '__main__':
    main()
