"""Generate additive poison mushroom definitions from JSON and approved assets.

The handoff ZIP is untrusted input: validate paths, lengths and SHA-256 before
extracting it. Image files are copied byte-for-byte; never resampled or edited.
This script does not touch the original ten edible mushrooms or their XML.
"""
from pathlib import Path, PurePosixPath
import argparse
import hashlib
import json
import shutil
import zipfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
CONFIG = ROOT / "Balance/poison_mushrooms.json"
ARCHIVE_PREFIX = "MoreMushrooms-Handoff-2026-10-05"


def node(parent, tag, value=None, **attrs):
    result = ET.SubElement(parent, tag, attrs)
    if value is not None:
        result.text = str(value).lower() if isinstance(value, bool) else str(value)
    return result


def write_xml(path, element):
    path.parent.mkdir(parents=True, exist_ok=True)
    ET.indent(element, space="  ")
    ET.ElementTree(element).write(path, encoding="utf-8", xml_declaration=True)


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def safe_relative(value):
    path = PurePosixPath(value)
    if "\\" in value or path.is_absolute() or ".." in path.parts or ":" in value:
        raise ValueError("Unsafe handoff path: " + value)
    return path


def import_handoff(archive, destination):
    with zipfile.ZipFile(archive) as package:
        infos = [info for info in package.infolist() if not info.is_dir()]
        paths = []
        for info in infos:
            path = safe_relative(info.filename)
            if path.parts[0] != ARCHIVE_PREFIX or len(path.parts) < 2:
                raise ValueError("Unexpected archive root: " + info.filename)
            paths.append(str(PurePosixPath(*path.parts[1:])))
        if len(paths) != len(set(paths)):
            raise ValueError("Duplicate handoff archive path")
        manifest = json.loads(package.read(ARCHIVE_PREFIX + "/package-manifest.json").decode("utf-8-sig"))
        expected = {entry["path"]: entry for entry in manifest["files"]}
        if len(expected) != len(manifest["files"]):
            raise ValueError("Duplicate handoff manifest path")
        if set(paths) != set(expected) | {"package-manifest.json"}:
            raise ValueError("ZIP contents do not match the handoff manifest")
        # Validate everything before writing any files.
        for path, entry in expected.items():
            safe_relative(path)
            data = package.read(ARCHIVE_PREFIX + "/" + path)
            if len(data) != entry["bytes"] or sha256(data) != entry["sha256"]:
                raise ValueError("Handoff integrity failure: " + path)
        for path in paths:
            target = destination.joinpath(*PurePosixPath(path).parts).resolve()
            if not target.is_relative_to(destination.resolve()):
                raise ValueError("Handoff extraction leaves destination")
            target.parent.mkdir(parents=True, exist_ok=True)
            data = package.read(ARCHIVE_PREFIX + "/" + path)
            if target.exists() and target.read_bytes() != data:
                raise ValueError("Refusing to replace changed preserved handoff: " + path)
            if not target.exists():
                target.write_bytes(data)
    print(f"Verified and preserved {len(paths)} handoff files; original ZIP unchanged.")


def verify_handoff(handoff):
    package_manifest = json.loads((handoff / "package-manifest.json").read_text(encoding="utf-8-sig"))
    for entry in package_manifest["files"]:
        path = safe_relative(entry["path"])
        data = handoff.joinpath(*path.parts).read_bytes()
        assert len(data) == entry["bytes"] and sha256(data) == entry["sha256"], entry["path"]
    art_manifest = json.loads((handoff / "art/manifest.json").read_text(encoding="utf-8-sig"))
    textures = [entry for entry in art_manifest["files"] if entry["file"].startswith("Textures/")]
    assert len(textures) == 25
    for entry in textures:
        path = safe_relative(entry["file"])
        source = handoff / "art" / Path(*path.parts)
        data = source.read_bytes()
        assert sha256(data) == entry["sha256"], entry["file"]
        destination = (ROOT / Path(*path.parts)).resolve()
        if not destination.is_relative_to(ROOT.resolve()):
            raise ValueError("Texture destination leaves the project")
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)
        assert destination.read_bytes() == data, entry["file"]
    return package_manifest, art_manifest


def inject(languages, def_name, labels, descriptions=None):
    for lang, label in zip(("English", "Korean"), labels):
        node(languages[lang], def_name + ".label", label)
    if descriptions:
        for lang, description in zip(("English", "Korean"), descriptions):
            node(languages[lang], def_name + ".description", description)


def generate_hediff(mushroom, common, hediffs, hediff_languages):
    identity = mushroom["id"]
    poison_name = "RMush_Poison" + identity
    en, ko = mushroom["en"], mushroom["ko"]
    settings = mushroom["settings"]
    hediff = node(hediffs, "HediffDef")
    node(hediff, "defName", poison_name)
    node(hediff, "label", en + " poisoning")
    if settings["fatal"] or settings.get("conditionalFatalThreshold", 0) > 0:
        description_en = mushroom["effectEn"] + " Care must be repeated using the normal medical system. Recovery is gradual after stabilization."
        description_ko = mushroom["effectKo"] + " 기본 의료 체계로 반복 치료하며, 안정화 뒤에도 서서히 회복합니다."
    else:
        description_en = mushroom["effectEn"] + " Symptoms resolve gradually after onset. Ordinary medical care can speed recovery."
        description_ko = mushroom["effectKo"] + " 발병 뒤 서서히 회복하며, 일반적인 치료는 회복을 도울 수 있습니다."
    if settings["fatal"] or settings.get("conditionalFatalThreshold", 0) > 0:
        description_en += " This poisoning's direct lethal check grants at least 24 hours after the first symptom warning. Other illnesses and injuries are not covered."
        description_ko += " 이 중독의 직접 사망 판정은 첫 증상 경고 후 최소 24시간의 대응 시간을 제공합니다. 다른 질환과 부상까지 포함한 생존 보장은 아닙니다."
    if settings.get("conditionalFatalThreshold", 0) > 0:
        description_en += " Ordinary exposure resolves gradually; only high or repeated exposure enters the life-threatening course."
        description_ko += " 보통 노출은 서서히 회복하지만 고노출·반복 섭취는 치명적 경과로 진행할 수 있습니다."
    node(hediff, "description", description_en)
    for key, value in {"hediffClass": "RimMushrooms.Hediff_MushroomPoisoning", "defaultLabelColor": "(0.85, 0.65, 0.35)", "initialSeverity": common["initialSeverity"], "maxSeverity": 1, "lethalSeverity": -1, "tendable": True, "isBad": True, "alwaysShowSeverity": True, "makesSickThought": True, "scenarioCanAdd": False}.items():
        node(hediff, key, value)
    comp = node(node(hediff, "comps"), "li", Class="HediffCompProperties_TendDuration")
    node(comp, "baseTendDurationHours", common["tendDurationHours"])
    node(comp, "tendOverlapHours", 0)
    node(comp, "severityPerDayTended", 0)
    extension = node(node(hediff, "modExtensions"), "li", Class="RimMushrooms.MushroomPoisonSettings")
    for key, value in settings.items():
        if key != "vomitMtbHours":
            node(extension, key, value)
    for key in ("responseGraceHours", "lethalSeverity", "stabilizationTreatment"):
        node(extension, key, common[key])
    stages = node(hediff, "stages")
    for index, specification in enumerate(mushroom["stages"]):
        stage = node(stages, "li")
        node(stage, "minSeverity", specification["minSeverity"])
        node(stage, "label", specification["en"])
        # Native stage vomiting interrupts ordinary jobs without custom
        # repeated jobs. The angel's apparent remission does not stop its
        # underlying poisoning progression.
        vomit_hours = settings.get("vomitMtbHours", 0)
        if index > 0 and vomit_hours > 0 and not (identity in ("DestroyingAngel", "DeathCap") and index == 2):
            node(stage, "vomitMtbDays", vomit_hours / 24)
        if "painOffset" in specification:
            node(stage, "painOffset", specification["painOffset"])
        if "immunityGainSpeedFactor" in specification:
            node(node(stage, "statFactors"), "ImmunityGainSpeed", specification["immunityGainSpeedFactor"])
        if specification.get("capacities"):
            modifiers = node(stage, "capMods")
            for capacity, offset in specification["capacities"].items():
                modifier = node(modifiers, "li")
                node(modifier, "capacity", capacity)
                node(modifier, "offset", offset)
        for lang, text in (("English", specification["en"]), ("Korean", specification["ko"])):
            node(hediff_languages[lang], poison_name + f".stages.{index}.label", text)
    inject(hediff_languages, poison_name, (en + " poisoning", ko + " 중독"), (description_en, description_ko))


def generate(config=None, handoff=None):
    if config is None:
        config = json.loads(CONFIG.read_text(encoding="utf-8"))
    if handoff is None:
        handoff = ROOT / config["handoff"]
    verify_handoff(handoff)
    common = config["common"]
    definitions, hediffs, categories = [ET.Element("Defs") for _ in range(3)]
    languages = {lang: ET.Element("LanguageData") for lang in ("English", "Korean")}
    hediff_languages = {lang: ET.Element("LanguageData") for lang in ("English", "Korean")}
    category_languages = {lang: ET.Element("LanguageData") for lang in ("English", "Korean")}

    category = node(categories, "ThingCategoryDef")
    node(category, "defName", "RMush_PoisonMushrooms")
    node(category, "label", "poison mushrooms")
    node(category, "parent", "ResourcesRaw")
    node(category, "iconPath", "Things/Item/RimMushrooms/FlyAgaric/03Full")
    inject(category_languages, "RMush_PoisonMushrooms", ("poison mushrooms", "독버섯"))

    # A separate resource branch avoids Foods/PlantFoodRaw recipe filters.
    # No CompDrug or drugCategory: drug policies never automatically select it.
    raw_base = node(definitions, "ThingDef", Name="RMush_PoisonRawBase", ParentName="OrganicProductBase", Abstract="True")
    node(raw_base, "stackLimit", common["stackLimit"])
    node(raw_base, "possessionCount", 0)
    node(raw_base, "socialPropernessMatters", True)
    node(node(raw_base, "thingCategories"), "li", "RMush_PoisonMushrooms")
    stats = node(raw_base, "statBases")
    node(stats, "Nutrition", common["nutrition"])
    node(stats, "FoodPoisonChanceFixedHuman", 0)
    graphic = node(raw_base, "graphicData")
    node(graphic, "graphicClass", "RimMushrooms.Graphic_MushroomStack")
    node(graphic, "drawSize", 1)
    ingestible = node(raw_base, "ingestible")
    for key, value in {"foodType": "Fungus", "drugCategory": "None", "preferability": "NeverForNutrition", "maxNumToIngestAtOnce": 1, "defaultNumToIngestAtOnce": 1, "baseIngestTicks": 500, "chairSearchRadius": 0, "ingestEffect": "EatVegetarian", "ingestSound": "RawVegetable_Eat", "ingestHoldUsesTable": False}.items():
        node(ingestible, key, value)
    extension = node(node(raw_base, "modExtensions"), "li", Class="RimMushrooms.MushroomStackThresholds")
    node(extension, "lowMaxCount", common["lowMaxCount"])
    node(extension, "mediumMaxCount", common["mediumMaxCount"])

    for mushroom in config["mushrooms"]:
        identity = mushroom["id"]
        raw_name = "RMush_Raw" + identity
        plant_name = "RMush_Plant" + identity
        poison_name = "RMush_Poison" + identity
        en, ko = mushroom["en"], mushroom["ko"]
        settings = mushroom["settings"]
        psychedelic = identity == "FlyAgaric"
        raw = node(definitions, "ThingDef", ParentName="RMush_RawBase" if psychedelic else "RMush_PoisonRawBase")
        node(raw, "defName", raw_name)
        raw_en = "Poisonous " + en + ". " + mushroom["effectEn"] + " Excluded from automatic eating and ordinary cooking. Can only be eaten by an explicit order. Treat poisoning with regular doctor care and medicine; no special antidote is required."
        raw_ko = "독성이 있는 " + ko + ". " + mushroom["effectKo"] + " 자동 섭취와 일반 요리 재료에서 제외되며, 직접 섭취 명령을 내리면 먹을 수 있습니다. 기존 의사 작업과 의약품으로 치료하며 전용 해독제는 필요하지 않습니다."
        if psychedelic:
            minimum, maximum = mushroom["hallucinationHours"]
            raw_en = f"Fly agaric mushrooms. Can be eaten raw or used automatically in ordinary cooking. A first standard exposure (10 mushrooms) grants +10 mood for 6 hours and {minimum:g}–{maximum:g} hours of uncontrolled hallucination wandering, together with neurological poisoning. " + mushroom["effectEn"] + " Effects and poisoning remain when used in meals."
            raw_ko = f"광대버섯입니다. 직접 먹거나 일반 요리에 자동으로 사용할 수 있습니다. 첫 표준 노출(원물 10개)은 6시간 동안 무드 +10과 {minimum:g}~{maximum:g}시간의 통제 불가 환각 배회, 신경계 중독을 함께 일으킵니다. " + mushroom["effectKo"] + " 요리에 넣어도 환각과 중독 효과가 전달됩니다."
            raw_en += " Amount affects mood and duration; new episodes are scheduled for 6–24 hours. Repeated intake cannot extend an episode beyond 24 hours from first exposure. Sleep or incapacitation can end wandering early."
            raw_ko += " 섭취량에 따라 무드와 시간이 달라지며, 새 노출의 예정 시간은 6~24시간입니다. 반복 섭취도 최초 노출부터 24시간을 넘기지 않습니다. 수면이나 쓰러짐으로 배회가 먼저 끝날 수 있습니다."
        node(raw, "label", en)
        node(raw, "description", raw_en)
        node(node(raw, "graphicData"), "texPath", "Things/Item/RimMushrooms/" + identity)
        node(node(raw, "statBases"), "MarketValue", mushroom["value"])
        ingestion = node(raw, "ingestible")
        node(ingestion, "ingestCommandString", "Eat {0}" if psychedelic else "Eat {0} (poisonous)")
        node(ingestion, "ingestReportString", "Eating {0}." if psychedelic else "Eating {0} (poisonous).")
        if psychedelic:
            node(node(ingestion, "outcomeDoers"), "li", Class="RimMushrooms.IngestionOutcomeDoer_MushroomExposure")
            exposure = node(node(raw, "modExtensions"), "li", Class="RimMushrooms.MushroomExposureProperties")
            for key, value in {"doseUnitCount": 10, "psychoactive": True, "moodBonus": 10, "moodDurationHours": 6, "hallucinationHoursMin": minimum, "hallucinationHoursMax": maximum, "poisonHediff": poison_name}.items():
                node(exposure, key, value)
        else:
            outcome = node(node(ingestion, "outcomeDoers"), "li", Class="RimMushrooms.IngestionOutcomeDoer_MushroomPoison")
            node(outcome, "hediff", poison_name)
            node(outcome, "doseUnitCount", 1)
            exposure = node(node(raw, "modExtensions"), "li", Class="RimMushrooms.MushroomExposureProperties")
            node(exposure, "doseUnitCount", 1)
            node(exposure, "poisonHediff", poison_name)
        rot = node(node(raw, "comps"), "li", Class="CompProperties_Rottable")
        node(rot, "daysToRotStart", mushroom["rotDays"])
        node(rot, "rotDestroys", True)
        inject(languages, raw_name, (en, ko), (raw_en, raw_ko))
        commands = (("English", "Eat {0}", "Eating {0}."), ("Korean", "{0} 먹기", "{0} 먹는 중.")) if psychedelic else (("English", "Eat {0} (poisonous)", "Eating {0} (poisonous)."), ("Korean", "{0} 먹기 (독버섯)", "{0} 먹는 중 (독버섯)."))
        for lang, command, report in commands:
            node(languages[lang], raw_name + ".ingestible.ingestCommandString", command)
            node(languages[lang], raw_name + ".ingestible.ingestReportString", report)

        plant = node(definitions, "ThingDef", ParentName="RMush_PlantBase")
        node(plant, "defName", plant_name)
        plant_en = "A wild cluster of poisonous " + en + ". Can be harvested but cannot be sown. " + mushroom["effectEn"] + " The harvested mushrooms are excluded from automatic eating and ordinary cooking."
        plant_ko = "야생에서 자라는 " + ko + " 군락입니다. 채집할 수 있지만 재배할 수 없습니다. " + mushroom["effectKo"] + " 수확물은 자동 섭취와 일반 요리 재료에서 제외됩니다."
        if psychedelic:
            plant_en = "A wild fly agaric cluster. Can be harvested but cannot be sown. The harvested mushrooms can be eaten raw or used in ordinary meals, causing mood effects, uncontrolled hallucination wandering and neurological poisoning."
            plant_ko = "야생에서 자라는 광대버섯 군락입니다. 채집할 수 있지만 재배할 수 없습니다. 수확물은 직접 먹거나 일반 요리에 넣을 수 있으며 무드 효과와 통제 불가 환각 배회, 신경계 중독을 함께 일으킵니다."
        node(plant, "label", en + " cluster")
        node(plant, "description", plant_en)
        node(node(plant, "graphicData"), "texPath", "Things/Plant/RimMushrooms/" + identity)
        # Wild plants are not food candidates themselves: nibbling would bypass
        # the harvested item's poison outcome and explicit-order restriction.
        node(node(plant, "statBases"), "Nutrition", 0)
        # Native IsNull prevents an empty ingestible-properties object, whose
        # missing food type would itself be an invalid ingestible definition.
        node(plant, "ingestible", Inherit="False", IsNull="True")
        plant_properties = node(plant, "plant")
        for key, value in {"purpose": "Misc", "humanFoodPlant": False, "growDays": mushroom["growDays"], "harvestYield": mushroom["yield"], "harvestedThingDef": raw_name}.items():
            node(plant_properties, key, value)
        wild_biomes = node(plant_properties, "wildBiomes")
        for biome, weight in mushroom["biomes"].items():
            node(wild_biomes, biome, weight)
        inject(languages, plant_name, (en + " cluster", ko + " 군락"), (plant_en, plant_ko))

        generate_hediff(mushroom, common, hediffs, hediff_languages)

    # Expansion raw items/plants belong to generate_expansion_defs.py.
    # This generator owns only their medical conditions and translations.
    for mushroom in config.get("additionalPoisons", []):
        generate_hediff(mushroom, common, hediffs, hediff_languages)

    write_xml(ROOT / "Defs/PoisonMushrooms/PoisonMushrooms.xml", definitions)
    write_xml(ROOT / "Defs/PoisonMushrooms/Hediffs.xml", hediffs)
    write_xml(ROOT / "Defs/PoisonMushrooms/Categories.xml", categories)
    for def_type, data in (("ThingDef", languages), ("HediffDef", hediff_languages), ("ThingCategoryDef", category_languages)):
        for lang, element in data.items():
            write_xml(ROOT / "Languages" / lang / "DefInjected" / def_type / "PoisonMushrooms.xml", element)
    generate_keyed()
    generate_credits(handoff)
    print(f"Generated {len(config['mushrooms'])} legacy wild plants/items and {len(config['mushrooms']) + len(config.get('additionalPoisons', []))} poisoning conditions with bilingual text; copied 25 unchanged PNGs.")


def generate_keyed():
    values = {
        "RMush_PoisonPhaseLatent": ("latent", "잠복 중"),
        "RMush_PoisonPhaseWorsening": ("worsening", "악화 중"),
        "RMush_PoisonPhaseStabilized": ("stabilized", "안정화됨"),
        "RMush_PoisonPhaseRecovering": ("recovering", "회복 중"),
        "RMush_PoisonExposureLabel": ("Poison mushroom eaten", "독버섯 섭취"),
        "RMush_PoisonExposureText": ("{0} has eaten {1}. Symptoms may appear after a latent period. Check their health and arrange medical care.", "{0}(이)가 {1}(을)를 먹었습니다. 잠복기 뒤 증상이 나타날 수 있습니다. 건강 상태를 확인하고 치료를 준비하세요."),
        "RMush_PoisonOnsetLabel": ("Mushroom poisoning symptoms", "버섯 중독 증상 발현"),
        "RMush_PoisonOnsetText": ("{0} has developed symptoms of {1}. Use regular doctor care and medicine to treat the poisoning.", "{0}에게 {1} 증상이 나타났습니다. 기존 의사 작업과 의약품으로 치료할 수 있습니다."),
        "RMush_PoisonDangerNote": ("This is a high-risk poisoning. Early, repeated care can stabilize it; apparent improvement does not mean it has been cured.", "위험도가 높은 중독입니다. 조기 반복 치료로 안정화할 수 있으며, 증상이 잦아들어도 완치된 것은 아닙니다."),
        "RMush_PoisonStabilizedLabel": ("Mushroom poisoning stabilized", "버섯 중독 안정화"),
        "RMush_PoisonStabilizedText": ("{0}'s {1} has stabilized. Recovery remains gradual; continue medical care and avoid further exposure.", "{0}의 {1}(이)가 안정화되었습니다. 회복에는 시간이 걸립니다. 돌봄을 이어가고 추가 섭취를 피하세요."),
        "RMush_PoisonTipPhase": ("Phase: {0}", "중독 상태: {0}"),
        "RMush_PoisonTipOnset": ("Symptoms expected in: {0}", "증상 발현까지: {0}"),
        "RMush_PoisonTipTreatment": ("Accumulated treatment: {0}", "누적 치료: {0}"),
        "RMush_PoisonTipRecovery": ("Estimated recovery remaining: {0}", "예상 회복 잔여 시간: {0}"),
        "RMush_PoisonTipGrace": ("Minimum response window remaining: {0}", "최소 대응 시간 잔여: {0}")
    }
    for index, lang in enumerate(("English", "Korean")):
        element = ET.Element("LanguageData")
        for key, translations in values.items():
            node(element, key, translations[index])
        write_xml(ROOT / "Languages" / lang / "Keyed/PoisonMushrooms.xml", element)


def generate_credits(handoff):
    references = json.loads((handoff / "art/references/sources.json").read_text(encoding="utf-8-sig"))
    lines = ["More Mushrooms poison mushroom artwork", "", "Approved handoff: 2026-10-05", "25 game textures and 25 masters generated with built-in ImageGen.", "The original ZIP and preserved handoff retain source, prompts and hash manifests.", "All approved game textures were copied byte-for-byte, with no image edits.", "", "Reference photographs are preserved only in development/source assets.", "They are unmodified; they are not included in the runtime mod textures.", "", "Reference photo attribution:"]
    for photo in references["photos"]:
        lines.extend(["", photo["name"] + " / " + photo["scientific"], "Author: " + photo["author"], "Source: " + photo["page"], "License: " + photo["license"], "License URL: " + photo["license_url"]])
    lines.extend(["", "Generated game artwork has no additional licensing declaration in the handoff.", "This credit file preserves reference-photo attribution; it does not invent a license for the mod or generated artwork.", ""])
    path = ROOT / "Credits/PoisonMushrooms-ART-CREDITS.txt"
    path.write_text("\n".join(lines), encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--import-handoff", type=Path, help="Verify and preserve the original ZIP before generation.")
    options = parser.parse_args()
    configuration = json.loads(CONFIG.read_text(encoding="utf-8"))
    preserved_handoff = ROOT / configuration["handoff"]
    if options.import_handoff:
        import_handoff(options.import_handoff, preserved_handoff)
    generate(configuration, preserved_handoff)
