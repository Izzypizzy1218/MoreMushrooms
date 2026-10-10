"""Generate the 31 approved expansion species without changing baseline assets.

Balance values are game design, not real-world nutritional or medical advice.
Artwork is copied byte-for-byte from the immutable approved v0.1 art pack.
"""
from pathlib import Path
import hashlib
import json
import shutil
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]


def node(parent, tag, value=None, **attrs):
    result = ET.SubElement(parent, tag, attrs)
    if value is not None:
        result.text = str(value).lower() if isinstance(value, bool) else str(value)
    return result


def write_xml(path, root):
    path.parent.mkdir(parents=True, exist_ok=True)
    ET.indent(root, space="  ")
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


def extension(parent, class_name):
    extensions = parent.find("modExtensions")
    if extensions is None:
        extensions = node(parent, "modExtensions")
    return node(extensions, "li", Class=class_name)


def localized(ko, en, def_name, label_ko, label_en, description_ko, description_en):
    for tree, label, description in ((ko, label_ko, description_ko), (en, label_en, description_en)):
        node(tree, def_name + ".label", label)
        node(tree, def_name + ".description", description)


def exposure(raw, mushroom):
    properties = extension(raw, "RimMushrooms.MushroomExposureProperties")
    node(properties, "doseUnitCount", mushroom.get("doseUnitCount", 1))
    if mushroom.get("poisonHediff"):
        node(properties, "poisonHediff", mushroom["poisonHediff"])
    if mushroom["use"] == "psychoactive":
        node(properties, "psychoactive", True)
        node(properties, "moodBonus", mushroom["mood"])
        node(properties, "moodDurationHours", 6)
        node(properties, "hallucinationHoursMin", mushroom["hallucinationHours"][0])
        node(properties, "hallucinationHoursMax", mushroom["hallucinationHours"][1])


def generate():
    config = json.loads((ROOT / "Balance/expansion_mushrooms.json").read_text(encoding="utf-8"))
    catalog = json.loads((ROOT / config["catalog"]).read_text(encoding="utf-8"))
    common = json.loads((ROOT / "Balance/mushrooms.json").read_text(encoding="utf-8"))["common"]
    species = config["mushrooms"]
    assert len(species) == 31
    assert {m["id"] for m in species} == {m["id"] for m in catalog["species"]}
    assert all(m["id"] not in catalog["existing_species_excluded"] for m in species)
    definitions, thoughts, categories = [ET.Element("Defs") for _ in range(3)]
    ko, en, thoughts_ko, thoughts_en, categories_ko, categories_en = [ET.Element("LanguageData") for _ in range(6)]

    category = node(categories, "ThingCategoryDef")
    node(category, "defName", "RMush_FungalResources")
    node(category, "label", "fungal resources")
    node(category, "parent", "ResourcesRaw")
    node(categories_ko, "RMush_FungalResources.label", "버섯 자원")
    node(categories_en, "RMush_FungalResources.label", "fungal resources")

    resource_base = node(definitions, "ThingDef", Name="RMush_ExpansionResourceBase", ParentName="OrganicProductBase", Abstract="True")
    node(resource_base, "stackLimit", common["stackLimit"])
    node(resource_base, "possessionCount", 0)
    node(node(resource_base, "thingCategories"), "li", "RMush_FungalResources")
    node(node(resource_base, "statBases"), "Nutrition", 0)
    node(resource_base, "ingestible", Inherit="False", IsNull="True")
    graphic = node(resource_base, "graphicData")
    node(graphic, "graphicClass", "RimMushrooms.Graphic_MushroomStack")
    node(graphic, "drawSize", 1)
    stack = extension(resource_base, "RimMushrooms.MushroomStackThresholds")
    node(stack, "lowMaxCount", common["lowMaxCount"])
    node(stack, "mediumMaxCount", common["mediumMaxCount"])

    asset_base = ROOT / config["assetBase"] / "Textures"
    copied = []
    for mushroom in species:
        identifier = mushroom["id"]
        raw_id, plant_id = "RMush_Raw" + identifier, "RMush_Plant" + identifier
        use = mushroom["use"]
        food = use in ("food", "psychoactive")
        parent = "RMush_RawBase" if food else "RMush_PoisonRawBase" if use == "poisonous" else "RMush_ExpansionResourceBase"
        raw = node(definitions, "ThingDef", ParentName=parent)
        node(raw, "defName", raw_id)
        node(raw, "label", mushroom["en"])
        description_en = mushroom["description"]["en"]
        description_ko = mushroom["description"]["ko"]
        if food:
            description_en += " Can be eaten directly or used in ordinary meals. Counts as fungus."
            description_ko += " 직접 먹거나 일반 요리의 재료로 사용할 수 있으며, 균류 식재료로 취급됩니다."
        if use == "psychoactive":
            minimum, maximum = mushroom["hallucinationHours"]
            description_en += f" A first standard exposure (10 mushrooms) grants +{mushroom['mood']} mood for 6 hours and causes {minimum:g}–{maximum:g} hours of uncontrolled hallucination wandering. These effects also occur when used as a meal ingredient. Psychoactive mushroom mood bonuses do not stack with each other."
            description_ko += f" 첫 표준 노출(원물 10개)은 6시간 동안 무드 +{mushroom['mood']}, {minimum:g}~{maximum:g}시간 동안 통제할 수 없는 환각성 배회를 일으킵니다. 요리에 넣어 먹어도 같은 효과가 나타나며, 향정신성 버섯의 무드 효과끼리는 중첩되지 않습니다."
            description_en += " Amount affects mood and duration; new episodes are scheduled for 6–24 hours. Repeated intake cannot extend an episode beyond 24 hours from first exposure. Sleep or incapacitation can end wandering early."
            description_ko += " 섭취량에 따라 무드와 시간이 달라지며, 새 노출의 예정 시간은 6~24시간입니다. 반복 섭취도 최초 노출부터 24시간을 넘기지 않습니다. 수면이나 쓰러짐으로 배회가 먼저 끝날 수 있습니다."
        elif use == "food" and mushroom.get("mood", 0):
            mood_hours = mushroom.get("moodDurationHours", common["moodDurationHours"])
            thought_id = "RMush_Ate" + identifier
            thought = node(thoughts, "ThoughtDef")
            node(thought, "defName", thought_id)
            node(thought, "thoughtClass", "RimMushrooms.Thought_MushroomEnjoyment")
            node(thought, "durationDays", mood_hours / 24)
            node(thought, "stackLimit", 1)
            node(thought, "showBubble", True)
            node(thought, "icon", "Things/Mote/ThoughtSymbol/Food")
            stage = node(node(thought, "stages"), "li")
            node(stage, "label", mushroom["moodText"]["en"]["label"])
            node(stage, "description", mushroom["moodText"]["en"]["description"])
            node(stage, "baseMoodEffect", mushroom["mood"])
            for tree, language in ((thoughts_ko, "ko"), (thoughts_en, "en")):
                for field in ("label", "description"):
                    node(tree, thought_id + ".stages.0." + field, mushroom["moodText"][language][field])
            description_en += f" Grants +{mushroom['mood']} mood for {mood_hours} hours, raw or as a meal ingredient. Only the strongest mushroom bonus applies."
            description_ko += f" 직접 먹거나 요리에 넣어 먹으면 {mood_hours}시간 동안 무드 +{mushroom['mood']}. 버섯 보너스는 가장 높은 하나만 적용됩니다."
            ingestible = node(raw, "ingestible")
            node(ingestible, "specialThoughtDirect", thought_id)
            node(ingestible, "specialThoughtAsIngredient", thought_id)
        elif use == "poisonous":
            description_en += " Excluded from automatic eating and ordinary cooking. Can be eaten only by an explicit order. Poisoning is treated with ordinary doctor care, medicine and rest."
            description_ko += " 자동 섭취와 일반 요리에서 제외되며, 직접 명령으로만 먹을 수 있습니다. 중독은 의사의 일반 치료와 약품, 휴식으로 관리합니다."
        node(raw, "description", description_en)
        node(node(raw, "graphicData"), "texPath", "Things/Item/RimMushrooms/" + identifier)
        node(node(raw, "statBases"), "MarketValue", mushroom["value"])
        if use in ("psychoactive", "poisonous"):
            exposure(raw, mushroom)
            ingestible = node(raw, "ingestible")
            outcomes = node(ingestible, "outcomeDoers")
            if use == "psychoactive":
                node(outcomes, "li", Class="RimMushrooms.IngestionOutcomeDoer_MushroomExposure")
            else:
                node(ingestible, "ingestCommandString", "Eat {0} (poisonous)")
                node(ingestible, "ingestReportString", "Eating {0} (poisonous).")
                outcome = node(outcomes, "li", Class="RimMushrooms.IngestionOutcomeDoer_MushroomPoison")
                node(outcome, "hediff", mushroom["poisonHediff"])
                node(outcome, "doseUnitCount", 1)
        rot = node(node(raw, "comps"), "li", Class="CompProperties_Rottable")
        node(rot, "daysToRotStart", mushroom["rotDays"])
        node(rot, "rotDestroys", True)
        localized(ko, en, raw_id, mushroom["ko"], mushroom["en"], description_ko, description_en)

        plant = node(definitions, "ThingDef", ParentName="RMush_PlantBase")
        node(plant, "defName", plant_id)
        label_en, label_ko = mushroom["en"] + " cluster", mushroom["ko"] + " 군락"
        node(plant, "label", label_en)
        plant_desc_en = "A cluster of " + mushroom["en"] + ". "
        plant_desc_ko = mushroom["ko"] + " 군락입니다. "
        if mushroom["cultivable"]:
            plant_desc_en += "Can be grown in soil or hydroponics basins. "
            plant_desc_ko += "재배 구역과 수경분지에서 재배할 수 있습니다. "
        else:
            plant_desc_en += "Can be gathered in the wild but cannot be sown. "
            plant_desc_ko += "야생에서 채집할 수 있으며 재배할 수 없습니다. "
        plant_desc_en += "Grows without a sun lamp; stronger light slows its growth. " + mushroom["description"]["en"]
        plant_desc_ko += "태양등 없이 자라며 강한 빛에서는 성장 속도가 느려집니다. " + mushroom["description"]["ko"]
        node(plant, "description", plant_desc_en)
        node(node(plant, "graphicData"), "texPath", "Things/Plant/RimMushrooms/" + identifier)
        node(node(plant, "statBases"), "Nutrition", 0)
        node(plant, "ingestible", Inherit="False", IsNull="True")
        properties = node(plant, "plant")
        for field, value in (("purpose", "Food" if food else "Misc"), ("humanFoodPlant", food), ("growDays", mushroom["growDays"]), ("harvestYield", mushroom["yield"]), ("harvestedThingDef", raw_id), ("wildClusterWeight", 1), ("wildClusterRadius", 2)):
            node(properties, field, value)
        if mushroom["cultivable"]:
            tags = node(properties, "sowTags")
            for tag in ("Ground", "Hydroponic"):
                node(tags, "li", tag)
            node(properties, "sowMinSkill", mushroom["skill"])
        biomes = node(properties, "wildBiomes")
        for biome, weight in mushroom["biomes"].items():
            node(biomes, biome, weight)
        localized(ko, en, plant_id, label_ko, label_en, plant_desc_ko, plant_desc_en)

        for kind, names in (("Item", ("01Low.png", "02Medium.png", "03Full.png")), ("Plant", (identifier + "A.png", identifier + "B.png"))):
            source_folder = asset_base / "Things" / kind / "RimMushrooms" / identifier
            target_folder = ROOT / "Textures/Things" / kind / "RimMushrooms" / identifier
            target_folder.mkdir(parents=True, exist_ok=True)
            for name in names:
                source, target = source_folder / name, target_folder / name
                assert source.is_file(), source
                shutil.copy2(source, target)
                assert hashlib.sha256(source.read_bytes()).digest() == hashlib.sha256(target.read_bytes()).digest()
                copied.append(str(target.relative_to(ROOT)))

    assert len(copied) == 155
    write_xml(ROOT / "Defs/ExpansionMushrooms/Mushrooms.xml", definitions)
    write_xml(ROOT / "Defs/ExpansionMushrooms/Enjoyment.xml", thoughts)
    write_xml(ROOT / "Defs/ExpansionMushrooms/Categories.xml", categories)
    for language, things, moods, category_text in (("Korean", ko, thoughts_ko, categories_ko), ("English", en, thoughts_en, categories_en)):
        write_xml(ROOT / "Languages" / language / "DefInjected/ThingDef/ExpansionMushrooms.xml", things)
        write_xml(ROOT / "Languages" / language / "DefInjected/ThoughtDef/ExpansionMushrooms.xml", moods)
        write_xml(ROOT / "Languages" / language / "DefInjected/ThingCategoryDef/ExpansionMushrooms.xml", category_text)
    print(f"Generated {len(species)} expansion species, {len(thoughts)} food thoughts, and {len(copied)} unchanged approved PNGs.")


if __name__ == "__main__":
    generate()
