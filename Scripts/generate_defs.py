"""Build XML and copy approved PNGs without editing the handoff or image pixels."""
from pathlib import Path
import argparse
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

def generate(handoff):
    config = json.loads((ROOT / "Balance/mushrooms.json").read_text(encoding="utf-8"))
    c = config["common"]
    assert 0 < c["lowMaxCount"] < c["mediumMaxCount"] < c["stackLimit"]
    plants, items, ko, en = [ET.Element(tag) for tag in ("Defs", "Defs", "LanguageData", "LanguageData")]
    thoughts, thoughts_ko, thoughts_en = [ET.Element(tag) for tag in ("Defs", "LanguageData", "LanguageData")]
    base = node(plants, "ThingDef", Name="RMush_PlantBase", ParentName="PlantBase", Abstract="True")
    stats = node(base, "statBases")
    for key, value in {"MaxHitPoints":85, "Nutrition":0.2}.items(): node(stats, key, value)
    gd = node(base, "graphicData")
    node(gd, "graphicClass", "Graphic_Random")
    node(gd, "drawSize", c["drawSize"])
    node(base, "selectable", True)
    node(base, "neverMultiSelect", False)
    node(base, "pathCost", 14)
    plant = node(base, "plant")
    for key, value in {
        "fertilityMin":c["fertilityMin"], "fertilitySensitivity":c["fertilitySensitivity"],
        "harvestTag":"Standard", "growMinGlow":0, "growOptimalGlow":0,
        "dieIfNoSunlight":False, "diesToLight":False, "topWindExposure":0,
        "visualSizeRange":"0.3~0.65", "maxMeshCount":9, "purpose":"Food",
        "humanFoodPlant":True, "wildOrder":1, "wildClusterRadius":3, "wildClusterWeight":5,
        "minGrowthTemperature":0, "minOptimalGrowthTemperature":10,
        "maxOptimalGrowthTemperature":30, "maxGrowthTemperature":45,
        "harvestMinGrowth":0.65, "harvestAfterGrowth":0, "lifespanDaysPerGrowDays":5
    }.items(): node(plant, key, value)
    base = node(items, "ThingDef", Name="RMush_RawBase", ParentName="PlantFoodRawBase", Abstract="True")
    node(base, "stackLimit", c["stackLimit"])
    node(base, "possessionCount", 10)
    stats = node(base, "statBases")
    node(stats, "Nutrition", c["nutrition"])
    gd = node(base, "graphicData")
    node(gd, "graphicClass", "RimMushrooms.Graphic_MushroomStack")
    node(gd, "drawSize", 1)
    node(node(base, "ingestible"), "foodType", "Fungus")
    node(node(node(base, "ingredient"), "mergeCompatibilityTags"), "li", "Fungus", MayRequire="Ludeon.RimWorld.Ideology")
    ext = node(node(base, "modExtensions"), "li", Class="RimMushrooms.MushroomStackThresholds")
    node(ext, "lowMaxCount", c["lowMaxCount"])
    node(ext, "mediumMaxCount", c["mediumMaxCount"])

    for m in config["mushrooms"]:
        raw_id = "RMush_Raw" + m["id"]
        thought_id = "RMush_Ate" + m["id"]
        thought = node(thoughts, "ThoughtDef")
        node(thought, "defName", thought_id)
        node(thought, "thoughtClass", "RimMushrooms.Thought_MushroomEnjoyment")
        node(thought, "durationDays", c["moodDurationHours"] / 24)
        node(thought, "stackLimit", 1)
        node(thought, "showBubble", True)
        node(thought, "icon", "Things/Mote/ThoughtSymbol/Food")
        stage = node(node(thought, "stages"), "li")
        mood_label_en = "enjoyed " + m["en"]
        mood_label_ko = m["ko"] + "의 맛"
        mood_desc_en = "That " + m["en"] + " had a satisfying flavor. A little pleasure from a good ingredient."
        mood_desc_ko = m["ko"] + "의 맛을 즐겼다. 좋은 식재료가 주는 작은 즐거움이다."
        node(stage, "label", mood_label_en)
        node(stage, "description", mood_desc_en)
        node(stage, "baseMoodEffect", m["mood"])
        for language, label, desc in ((thoughts_ko, mood_label_ko, mood_desc_ko), (thoughts_en, mood_label_en, mood_desc_en)):
            node(language, thought_id + ".stages.0.label", label)
            node(language, thought_id + ".stages.0.description", desc)
        raw = node(items, "ThingDef", ParentName="RMush_RawBase")
        node(raw, "defName", raw_id)
        node(raw, "label", m["en"])
        desc_en = "Edible " + m["en"] + "s. Can be cooked in ordinary meals or eaten raw. Counts as fungus."
        desc_ko = "식용 " + m["ko"] + ". 일반 요리의 재료로 쓰거나 생으로 먹을 수 있으며, 균류 식재료로 취급됩니다."
        desc_en += f" Eating it raw or in a meal grants +{m['mood']} mood for {c['moodDurationHours']} hours. Only the strongest mushroom bonus applies; eating a weaker mushroom does not extend it. Normal food and ideology effects still apply."
        desc_ko += f" 생식하거나 요리에 넣어 먹으면 {c['moodDurationHours']}시간 동안 무드 +{m['mood']}. 버섯 보너스는 가장 높은 하나만 적용되며, 더 약한 버섯으로는 지속시간을 연장할 수 없습니다. 기존 식사 및 사상 효과도 적용됩니다."
        node(raw, "description", desc_en)
        node(node(raw, "graphicData"), "texPath", "Things/Item/RimMushrooms/" + m["id"])
        node(node(raw, "statBases"), "MarketValue", m["value"])
        ingestible = node(raw, "ingestible")
        node(ingestible, "specialThoughtDirect", thought_id)
        node(ingestible, "specialThoughtAsIngredient", thought_id)
        rot = node(node(raw, "comps"), "li", Class="CompProperties_Rottable")
        node(rot, "daysToRotStart", m["rotDays"])
        node(rot, "rotDestroys", True)
        for language, label, desc in ((ko, m["ko"], desc_ko), (en, m["en"], desc_en)):
            node(language, raw_id + ".label", label)
            node(language, raw_id + ".description", desc)
        folder = ROOT / "Textures/Things/Item/RimMushrooms" / m["id"]
        folder.mkdir(parents=True, exist_ok=True)
        for source, target in (("low", "01Low"), ("medium", "02Medium"), ("full", "03Full")):
            shutil.copy2(handoff / "outputs/mushrooms-boxed/sprites-256" / (m["asset"] + "_" + source + ".png"), folder / (target + ".png"))
        variants = [(m["id"], m["asset"], m.get("wildOnly", False))]
        if m["id"] == "Enoki": variants.append(("EnokiWild", "05_enoki_wild", True))
        for plant_id, asset, wild_only in variants:
            def_id = "RMush_Plant" + plant_id
            p = node(plants, "ThingDef", ParentName="RMush_PlantBase")
            node(p, "defName", def_id)
            label_en = ("wild " if plant_id == "EnokiWild" else "") + m["en"] + " cluster"
            label_ko = ("야생 " if plant_id == "EnokiWild" else "") + m["ko"] + " 군락"
            desc_en = "A cluster of " + m["en"] + "s growing directly from the soil. Grows in both darkness and daylight. "
            desc_ko = "흙에서 올라오는 " + m["ko"] + " 군락입니다. 어둠과 햇빛 모두에서 자랍니다. "
            if wild_only:
                desc_en += "Gathered in the wild; cannot be sown."
                desc_ko += "야생에서 채집할 수 있으며 재배할 수 없습니다."
            else:
                desc_en += "Can be sown in growing zones."
                desc_ko += "재배 구역에 심을 수 있습니다."
            if plant_id == "EnokiWild":
                desc_en += " Its golden caps differ from cultivated white enoki. Both yield the same enoki ingredient."
                desc_ko += "황갈색 갓이 특징입니다. 흰 재배 팽이와 같은 팽이버섯 식재료를 생산합니다."
            node(p, "label", label_en)
            node(p, "description", desc_en)
            node(node(p, "graphicData"), "texPath", "Things/Plant/RimMushrooms/" + plant_id)
            pp = node(p, "plant")
            for key, value in {"growDays":m["growDays"], "harvestYield":m["yield"], "harvestedThingDef":raw_id}.items(): node(pp, key, value)
            if not wild_only:
                node(node(pp, "sowTags"), "li", "Ground")
                node(pp, "sowMinSkill", m["skill"])
            if plant_id != "Enoki":
                wb = node(pp, "wildBiomes")
                for biome, commonality in m["biomes"].items(): node(wb, biome, commonality)
            for language, label, desc in ((ko,label_ko,desc_ko), (en,label_en,desc_en)):
                node(language, def_id + ".label", label)
                node(language, def_id + ".description", desc)
            folder = ROOT / "Textures/Things/Plant/RimMushrooms" / plant_id
            folder.mkdir(parents=True, exist_ok=True)
            for v in ("A", "B"):
                shutil.copy2(handoff / "outputs/mushrooms-growing/textures-256" / (asset + "_" + v + ".png"), folder / (plant_id + v + ".png"))

    write_xml(ROOT / "Defs/ThingDefs_Plants/Mushrooms.xml", plants)
    write_xml(ROOT / "Defs/ThingDefs_Items/RawMushrooms.xml", items)
    write_xml(ROOT / "Defs/ThoughtDefs/MushroomEnjoyment.xml", thoughts)
    for name, language in (("Korean",ko),("English",en)):
        write_xml(ROOT / "Languages" / name / "DefInjected/ThingDef/Mushrooms.xml", language)
    for name, language in (("Korean",thoughts_ko),("English",thoughts_en)):
        write_xml(ROOT / "Languages" / name / "DefInjected/ThoughtDef/MushroomEnjoyment.xml", language)
    credits = ROOT / "Credits"
    credits.mkdir(exist_ok=True)
    for source, dest in (
        ("mushrooms/CREDITS.txt","MUSHROOM-REFERENCE-CREDITS.txt"),
        ("mushrooms-boxed/CREDITS.txt","BOXED-CREDITS.txt"),
        ("mushrooms-boxed/RIMSHARE-LICENSE.txt","RIMSHARE-LICENSE.txt"),
        ("mushrooms-growing/CREDITS.txt","GROWING-CREDITS.txt")):
        shutil.copy2(handoff / "outputs" / source, credits / dest)
    shutil.copy2(handoff / "outputs/mushrooms-boxed/boxed-preview.png", ROOT / "About/Preview.png")
    print("Generated 11 plants, 10 ingredients, 10 six-hour mood memories, 52 unchanged PNGs, and EN/KO translations.")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--handoff", type=Path, default=ROOT.parent / "RimWorld-Mushrooms-Handoff-2026-10-03")
    generate(parser.parse_args().handoff)
