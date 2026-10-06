"""Mechanical RGBA export and preview assembly. Does not paint or recolor artwork."""
from pathlib import Path
from PIL import Image
import base64
import hashlib
import io
import json

ROOT = Path(__file__).resolve().parent
SPECIES = ['FlyAgaric', 'DestroyingAngel', 'SulfurTuft', 'Tsukiyotake', 'PoisonFireCoral']
KINDS = ['PlantA', 'PlantB', '01Low', '02Medium', '03Full']

def normalize(path, plant):
    src = Image.open(path).convert('RGBA')
    alpha = src.getchannel('A')
    assert alpha.getextrema() == (0, 255), f'No genuine alpha: {path}'
    box = alpha.point(lambda a: 255 if a >= 16 else 0).getbbox()
    assert box, path
    cropped = src.crop(box)
    scale = 192 / max(cropped.size) if plant else 226 / cropped.width
    size = tuple(max(1, round(v * scale)) for v in cropped.size)
    sprite = cropped.resize(size, Image.Resampling.LANCZOS)
    out = Image.new('RGBA', (256, 256))
    bottom = 224 if plant else 217
    origin = (round(128 - sprite.width / 2), bottom - sprite.height)
    assert origin[0] >= 0 and origin[1] >= 0, f'Sprite exceeds canvas: {path}, {size}'
    out.alpha_composite(sprite, origin)
    return out, {'source_size': list(src.size), 'source_alpha_bbox16': list(box), 'export_origin': list(origin), 'export_size': list(size)}

def main():
    entries, previews = [], {}
    for species in SPECIES:
        for kind in KINDS:
            source = ROOT / 'masters' / species / (kind + '.png')
            if not source.exists():
                raise FileNotFoundError(source)
            plant = kind.startswith('Plant')
            directory = ROOT / 'Textures' / 'Things' / ('Plant' if plant else 'Item') / 'RimMushrooms' / species
            directory.mkdir(parents=True, exist_ok=True)
            target = directory / ((species + kind[-1] if plant else kind) + '.png')
            im, metadata = normalize(source, plant)
            im.save(target, optimize=True)
            buffer = io.BytesIO()
            im.resize((128, 128), Image.Resampling.LANCZOS).save(buffer, format='PNG', optimize=True)
            previews[species + kind] = 'data:image/png;base64,' + base64.b64encode(buffer.getvalue()).decode('ascii')
            entries.append({'species': species, 'kind': kind, 'file': target.relative_to(ROOT).as_posix(), 'sha256': hashlib.sha256(target.read_bytes()).hexdigest(), 'dimensions': [256, 256], 'mode': im.mode, 'alpha_range': list(im.getchannel('A').getextrema()), **metadata})
    fragment = (ROOT / 'preview-template.html').read_text(encoding='utf-8').replace('__ASSET_DATA__', json.dumps(previews, ensure_ascii=False, separators=(',', ':')))
    assert len(fragment.encode('utf-8')) < 1_000_000
    (ROOT / 'poison-mushrooms-preview.html').write_text(fragment, encoding='utf-8')
    comparison = (ROOT / 'comparison-template.html').read_text(encoding='utf-8').replace('__ASSET_DATA__', json.dumps(previews, ensure_ascii=False, separators=(',', ':')))
    assert len(comparison.encode('utf-8')) < 1_000_000
    (ROOT / 'poison-mushrooms-comparison.html').write_text(comparison, encoding='utf-8')
    manifest = {'date': '2026-10-05', 'generator': 'built-in ImageGen', 'reference_repo_commit': '82c5c1c9bbb801560a0d14a0c1fd16f0eaf8dc16', 'deliverable': 'art assets only', 'texture_count': len(entries), 'preview_bytes': len(fragment.encode('utf-8')), 'files': entries}
    (ROOT / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'textures': len(entries), 'preview_bytes': manifest['preview_bytes'], 'all_rgba256': True, 'all_alpha': True}, ensure_ascii=False))

if __name__ == '__main__':
    main()
