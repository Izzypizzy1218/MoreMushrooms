"""Apply the approved v0.6.5 wild-density policy from immutable v0.6.4 data.

This is an explicit, idempotent balance migration. The ordinary definition
generators continue to serialize the three species JSON files unchanged.
"""
from pathlib import Path
import json
import zipfile

ROOT = Path(__file__).resolve().parents[1]
BASELINE_VERSION = '0.6.4'
VERSION = '0.6.5'
CULTIVABLE = ['Button', 'Shiitake', 'Oyster', 'KingOyster', 'Enoki', 'WoodEar',
             'Beech', 'Maitake', 'LionsMane', 'Cauliflower', 'PurpleBlewit']
TROPICAL = ['TropicalRainforest', 'TropicalSwamp']
FILES = ['mushrooms.json', 'poison_mushrooms.json', 'expansion_mushrooms.json']


def write(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8', newline='\n')


def main():
    current_version = json.loads((ROOT / 'Balance/mushrooms.json').read_text(encoding='utf-8'))['version']
    if current_version not in (BASELINE_VERSION, VERSION):
        raise SystemExit('Historical v0.6.5 migration refused: current version is ' + current_version)
    archive_path = ROOT / 'Releases' / ('v' + BASELINE_VERSION) / ('MoreMushrooms-v' + BASELINE_VERSION + '-source.zip')
    with zipfile.ZipFile(archive_path) as archive:
        old = {name: json.loads(archive.read('MoreMushrooms/Balance/' + name)) for name in FILES}
        old_ecology = json.loads(archive.read('MoreMushrooms/Balance/wild_ecology.json'))
    species = [m for name in FILES for m in old[name]['mushrooms']]
    baseline = {m['id']: m['biomes'].copy() for m in species}
    assert len(baseline) == 46 and len(CULTIVABLE) == 11 and set(CULTIVABLE) < set(baseline)
    biomes = sorted({b for weights in baseline.values() for b in weights})
    ecology = json.loads((ROOT / 'Balance/wild_ecology.json').read_text(encoding='utf-8'))
    assert ecology['settings'] == old_ecology['settings']
    assert ecology['species'] == old_ecology['species'] and ecology['seasonProfiles'] == old_ecology['seasonProfiles']

    def factor(identity, biome):
        if biome in TROPICAL:
            return 1.0
        profile = ecology['species'][identity]['seasonProfile']
        return ecology['seasonProfiles'][profile]['springFactor']

    output = {identity: {} for identity in baseline}
    audit = {}
    for biome in biomes:
        before_c = sum(w.get(biome, 0) * factor(i, biome) for i, w in baseline.items() if i in CULTIVABLE)
        before_o = sum(w.get(biome, 0) * factor(i, biome) for i, w in baseline.items() if i not in CULTIVABLE)
        assert before_c > 0 and before_o > 0
        total = before_c + before_o
        a, b = .2 * .5 * total / before_c, .8 * .5 * total / before_o
        for identity, weights in baseline.items():
            if biome in weights:
                output[identity][biome] = round(weights[biome] * (a if identity in CULTIVABLE else b), 12)
        after_c = sum(w.get(biome, 0) * factor(i, biome) for i, w in output.items() if i in CULTIVABLE)
        after_o = sum(w.get(biome, 0) * factor(i, biome) for i, w in output.items() if i not in CULTIVABLE)
        audit[biome] = dict(beforeRawTotal=sum(w.get(biome, 0) for w in baseline.values()),
            afterRawTotal=sum(w.get(biome, 0) for w in output.values()),
            beforeEffectiveTotal=total, afterEffectiveTotal=after_c+after_o,
            beforeCultivableEffective=before_c, beforeWildOnlyEffective=before_o,
            afterCultivableEffective=after_c, afterWildOnlyEffective=after_o,
            cultivableMultiplier=a, wildOnlyMultiplier=b,
            afterCultivableShare=after_c/(after_c+after_o), effectiveRetention=(after_c+after_o)/total)
    for name in FILES:
        path = ROOT / 'Balance' / name
        config = json.loads(path.read_text(encoding='utf-8'))
        for mushroom in config['mushrooms']:
            assert mushroom['id'] in output
            mushroom['biomes'] = output[mushroom['id']]
        if name == 'mushrooms.json':
            config['version'] = VERSION
            config['status'] = 'v0.6.5: approved wild emergence weight reduction of 50%, with cultivable11/wild-only35 Spring-effective weight target20:80. Cultivation, food effects, ecology events, artwork and runtime source are unchanged.'
        elif name == 'expansion_mushrooms.json':
            config['contentVersion'] = VERSION
        else:
            config.pop('contentVersion', None)
            config['status'] = config['status'].replace('v' + BASELINE_VERSION, 'v' + VERSION)
        if name == 'expansion_mushrooms.json':
            config['newWildCommonalityTotals'] = {
                biome: round(sum(m['biomes'].get(biome, 0) for m in config['mushrooms']), 12)
                for biome in biomes
            }
        write(path, config)
    ecology['contentVersion'] = VERSION
    ecology['commonality'] = dict(baselineVersion=BASELINE_VERSION, baselineBiomes=baseline,
        retention=.5, cultivableShareTarget=.2, cultivableSpecies=CULTIVABLE,
        calibrationSeason='Spring', tropicalBiomes=TROPICAL,
        policy='Match the approved 50% preview: scale each existing native biome/species weight from immutable v0.6.4 data. Spring seasonal-effective total retains50%, with cultivable11 at20% and wild-only35 at80%; tropical biomes use native season factor1. Relative weights within each group and biome registrations are preserved. Raw totals, other seasons and realized plant counts need not have these exact ratios. Existing plants, cultivation and separate fairy-ring/mycelium budgets are unchanged.',
        biomeAudit=audit)
    write(ROOT / 'Balance/wild_ecology.json', ecology)
    about = ROOT / 'About/About.xml'
    text = about.read_text(encoding='utf-8')
    assert '<modVersion>' + BASELINE_VERSION + '</modVersion>' in text or '<modVersion>' + VERSION + '</modVersion>' in text
    about.write_text(text.replace('<modVersion>' + BASELINE_VERSION + '</modVersion>', '<modVersion>' + VERSION + '</modVersion>'), encoding='utf-8', newline='\n')
    print(json.dumps(dict(version=VERSION, biomeAudit=audit), ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
