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
    mood_text = json.loads((ROOT / "Balance/mood_text.json").read_text(encoding="utf-8"))
    c = config["common"]
    assert 0 < c["lowMaxCount"] < c["mediumMaxCount"] < c["stackLimit"]
    plants, items, ko, en = [ET.Element(tag) for tag in ("Defs", "Defs", "LanguageData", "LanguageData")]
    thoughts, thoughts_ko, thoughts_en = [ET.Element(tag) for tag in ("Defs", "LanguageData", "LanguageData")]
    base = node(plants, "ThingDef", Name="RMush_PlantBase", ParentName="PlantBase", Abstract="True")
    node(base, "thingClass", "RimMushrooms.Plant_Mushroom")
    light = node(node(base, "modExtensions"), "li", Class="RimMushrooms.MushroomLightSettings")
    node(light, "shadeMaxGlow", c["shadeMaxGlow"])
    node(light, "fullLightGrowthFactor", c["fullLightGrowthFactor"])
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
        mood_hours = m.get("moodDurationHours", c["moodDurationHours"])
        raw_id = "RMush_Raw" + m["id"]
        thought_id = "RMush_Ate" + m["id"]
        thought = node(thoughts, "ThoughtDef")
        node(thought, "defName", thought_id)
        node(thought, "thoughtClass", "RimMushrooms.Thought_MushroomEnjoyment")
        node(thought, "durationDays", mood_hours / 24)
        node(thought, "stackLimit", 1)
        node(thought, "showBubble", True)
        node(thought, "icon", "Things/Mote/ThoughtSymbol/Food")
        stage = node(node(thought, "stages"), "li")
        wording = mood_text[m["id"]]
        mood_label_en = wording["en"]["label"]
        mood_label_ko = wording["ko"]["label"]
        mood_desc_en = wording["en"]["description"]
        mood_desc_ko = wording["ko"]["description"]
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
        desc_en += f" Eating it raw or in a meal grants +{m['mood']} mood for {mood_hours} hours. Only the strongest mushroom bonus applies; eating a weaker mushroom does not extend it. Normal food and ideology effects still apply."
        desc_ko += f" 생식하거나 요리에 넣어 먹으면 {mood_hours}시간 동안 무드 +{m['mood']}. 버섯 보너스는 가장 높은 하나만 적용되며, 더 약한 버섯으로는 지속시간을 연장할 수 없습니다. 기존 식사 및 사상 효과도 적용됩니다."
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
            desc_en = "A cluster of " + m["en"] + ("s growing directly from the soil. " if wild_only else "s grown in soil or a hydroponics basin. ")
            desc_en += f"Grows normally at light levels up to {c['shadeMaxGlow']:.0%}. Stronger light gradually slows growth to {c['fullLightGrowthFactor']:.0%} speed at 100% light, without direct light damage. "
            desc_ko = ("흙에서 올라오는 " if wild_only else "흙이나 수경분지에서 재배하는 ") + m["ko"] + " 군락입니다. "
            desc_ko += f"광량 {c['shadeMaxGlow']:.0%} 이하에서는 정상 성장하며, 그보다 강한 빛에서는 점차 느려져 광량 100%에서 성장 속도가 {c['fullLightGrowthFactor']:.0%}가 됩니다. 빛으로 직접 피해를 받지는 않습니다. "
            if wild_only:
                desc_en += "Gathered in the wild; cannot be sown."
                desc_ko += "야생에서 채집할 수 있으며 재배할 수 없습니다."
            else:
                desc_en += "Can be sown in growing zones and hydroponics basins. Native hydroponics power requirements apply."
                desc_ko += "재배 구역과 수경분지에 심을 수 있습니다. 수경분지는 기본 게임의 전력 규칙을 따릅니다."
            if plant_id == "EnokiWild":
                desc_en += " Its golden caps differ from cultivated white enoki. Both yield the same enoki ingredient."
                desc_ko += "황갈색 갓이 특징입니다. 흰 재배 팽이와 같은 팽이버섯 식재료를 생산합니다."
            node(p, "label", label_en)
            node(p, "description", desc_en)
            node(node(p, "graphicData"), "texPath", "Things/Plant/RimMushrooms/" + plant_id)
            pp = node(p, "plant")
            for key, value in {"growDays":m["growDays"], "harvestYield":m["yield"], "harvestedThingDef":raw_id}.items(): node(pp, key, value)
            if not wild_only:
                tags = node(pp, "sowTags")
                for tag in ("Ground", "Hydroponic"): node(tags, "li", tag)
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

    cultivable = [m for m in config["mushrooms"] if not m.get("wildOnly", False)]
    expansion = json.loads((ROOT / "Balance/expansion_mushrooms.json").read_text(encoding="utf-8"))
    cultivable += [m for m in expansion["mushrooms"] if m["cultivable"]]
    assorted_id = "RMush_PlantAssorted"
    assorted = node(plants, "ThingDef", ParentName="RMush_PlantBase")
    node(assorted, "defName", assorted_id)
    label_en, label_ko = "assorted mushrooms", "모둠버섯"
    desc_en = "Sow one of eleven cultivated mushroom varieties at random in growing zones or hydroponics basins, each with an equal chance. Includes cauliflower and purple blewit; excludes matsutake and wild enoki. Each new sowing picks again. The chosen variety keeps its own growth, harvest and food effects. Requires Plants skill 6. Native hydroponics power requirements apply."
    desc_ko = "재배 구역이나 수경분지에서 재배 가능한 버섯 11종 중 하나를 같은 확률로 무작위 파종합니다. 꽃송이와 자주방망이를 포함하며, 송이와 야생 팽이는 제외합니다. 수확 후 다시 심을 때도 새로 뽑습니다. 심어진 종류의 성장·수확·섭취 효과가 그대로 적용됩니다. 식물 기술 6이 필요합니다. 수경분지는 기본 게임의 전력 규칙을 따릅니다."
    node(assorted, "label", label_en)
    node(assorted, "description", desc_en)
    node(node(assorted, "graphicData"), "texPath", "Things/Plant/RimMushrooms/Button")
    node(assorted, "uiIconPath", "UI/Icons/AssortedMushrooms")
    icon = ROOT / "Textures/UI/Icons/AssortedMushrooms.png"
    icon.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(ROOT / "Art/Icons/AssortedMushrooms.png", icon)
    settings = node(node(assorted, "modExtensions"), "li", Class="RimMushrooms.AssortedMushroomSettings")
    varieties = node(settings, "varieties")
    for m in cultivable: node(varieties, "li", "RMush_Plant" + m["id"])
    pp = node(assorted, "plant")
    tags = node(pp, "sowTags")
    for tag in ("Ground", "Hydroponic"): node(tags, "li", tag)
    node(pp, "sowMinSkill", max(m["skill"] for m in cultivable))
    # Menu summary only; jobs always plant the chosen species' actual def.
    node(pp, "growDays", sum(m["growDays"] for m in cultivable) / len(cultivable))
    node(pp, "harvestYield", 0)
    node(pp, "harvestTag", "None")
    for language, label, desc in ((ko,label_ko,desc_ko),(en,label_en,desc_en)):
        node(language, assorted_id + ".label", label)
        node(language, assorted_id + ".description", desc)

    write_xml(ROOT / "Defs/ThingDefs_Plants/Mushrooms.xml", plants)
    write_xml(ROOT / "Defs/ThingDefs_Items/RawMushrooms.xml", items)
    write_xml(ROOT / "Defs/ThoughtDefs/MushroomEnjoyment.xml", thoughts)
    for name, language in (("Korean",ko),("English",en)):
        write_xml(ROOT / "Languages" / name / "DefInjected/ThingDef/Mushrooms.xml", language)
    for name, language in (("Korean",thoughts_ko),("English",thoughts_en)):
        write_xml(ROOT / "Languages" / name / "DefInjected/ThoughtDef/MushroomEnjoyment.xml", language)
    for name, text in (("Korean", "강한 빛에 따른 성장 배율: {0}"), ("English", "Bright-light growth multiplier: {0}")):
        language = ET.Element("LanguageData")
        node(language, "MM_BrightLightGrowthFactor", text)
        write_xml(ROOT / "Languages" / name / "Keyed/Mushrooms.xml", language)
    credits = ROOT / "Credits"
    credits.mkdir(exist_ok=True)
    for source, dest in (
        ("mushrooms/CREDITS.txt","MUSHROOM-REFERENCE-CREDITS.txt"),
        ("mushrooms-boxed/CREDITS.txt","BOXED-CREDITS.txt"),
        ("mushrooms-boxed/RIMSHARE-LICENSE.txt","RIMSHARE-LICENSE.txt"),
        ("mushrooms-growing/CREDITS.txt","GROWING-CREDITS.txt")):
        shutil.copy2(handoff / "outputs" / source, credits / dest)
    # RimWorld fixes the preview filename to Preview.png but LoadImage decodes
    # JPEG/PNG by content. Preserve the approved sub-1MB JPEG byte-for-byte.
    shutil.copy2(ROOT / "Art/Covers/v0.3.1/MoreMushrooms-cover-v1.jpg", ROOT / "About/Preview.png")
    print("Generated 11 species plants plus assorted sowing selection, 10 ingredients, 10 memories, 52 unchanged PNGs plus 1 assorted UI icon, and EN/KO translations.")

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--handoff", type=Path, default=ROOT.parent / "RimWorld-Mushrooms-Handoff-2026-10-03")
    generate(parser.parse_args().handoff)
    from generate_poison_defs import generate as generate_poison
    generate_poison()
    from generate_expansion_defs import generate as generate_expansion
    generate_expansion()
    from generate_ecology_defs import generate as generate_ecology
    generate_ecology()
