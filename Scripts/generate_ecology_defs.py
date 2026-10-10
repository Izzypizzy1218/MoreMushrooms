"""Generate bounded wild ecology from Balance/wild_ecology.json.

Run after the three species generators. Repeated runs are idempotent and never
modify artwork, cultivation statistics, food effects or source handoffs.
"""
from pathlib import Path
import json
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
EXTENSION = "RimMushrooms.MushroomEcologyExtension"
DEF_CLASS = "RimMushrooms.WildMushroomEcologyDef"


def node(parent, tag, value=None, **attributes):
    result = ET.SubElement(parent, tag, attributes)
    if value is not None:
        result.text = str(value).lower() if isinstance(value, bool) else str(value)
    return result


def write_xml(path, root):
    path.parent.mkdir(parents=True, exist_ok=True)
    ET.indent(root, space="  ")
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


def ecology_description(settings, language):
    profile = settings["seasonProfile"]
    seasons = {
        "English": {"autumn": "autumn", "spring": "spring", "summer": "summer", "cool": "cool spring and autumn weather", "tropical": "warm seasons", "persistent": "most of the year"},
        "Korean": {"autumn": "가을", "spring": "봄", "summer": "여름", "cool": "선선한 봄과 가을", "tropical": "따뜻한 계절", "persistent": "연중 대부분"},
    }
    if language == "English":
        result = " Wild ecology: Most likely to appear during " + seasons[language][profile] + ". Seasonal emergence does not change cultivated growth."
        if settings["ringEligible"]:
            result += " May form a small fairy ring."
        if settings["regrowthChance"] > 0:
            result += f" After harvesting in the wild, surviving mycelium has a {settings['regrowthChance']:.0%} chance to fruit again after {settings['regrowthDelayDays']:g} days if the site remains suitable."
        if "glow" in settings:
            result += " Emits a small amount of light."
    else:
        result = " 야생 생태: " + seasons[language][profile] + "에 더 잘 발생합니다. 제철은 재배 성장 속도를 바꾸지 않습니다."
        if settings["ringEligible"]:
            result += " 작은 고리 모양 군락을 이룰 수 있습니다."
        if settings["regrowthChance"] > 0:
            result += f" 야생에서 수확한 자리가 계속 적합하면 남은 균사가 {settings['regrowthDelayDays']:g}일 뒤 {settings['regrowthChance']:.0%} 확률로 다시 자랄 수 있습니다."
        if "glow" in settings:
            result += " 주변에 약한 빛을 냅니다."
    return result


def generate():
    config = json.loads((ROOT / "Balance/wild_ecology.json").read_text(encoding="utf-8"))
    release = json.loads((ROOT / "Balance/mushrooms.json").read_text(encoding="utf-8"))
    assert config["contentVersion"] == release["version"]
    definitions = ET.Element("Defs")
    settings_def = node(definitions, DEF_CLASS)
    node(settings_def, "defName", "RMush_WildEcology")
    for key, value in config["settings"].items():
        node(settings_def, key, value)
    write_xml(ROOT / "Defs/WildEcology/WildEcology.xml", definitions)

    plants = {}
    paths = ("Defs/ThingDefs_Plants/Mushrooms.xml", "Defs/PoisonMushrooms/PoisonMushrooms.xml", "Defs/ExpansionMushrooms/Mushrooms.xml")
    for relative in paths:
        path = ROOT / relative
        document = ET.parse(path)
        for plant in document.getroot().findall("ThingDef"):
            name = plant.findtext("defName", "")
            if not name.startswith("RMush_Plant") or name == "RMush_PlantAssorted":
                continue
            identity = name.removeprefix("RMush_Plant")
            settings = config["species"]["Enoki" if identity == "EnokiWild" else identity]
            extensions = plant.find("modExtensions")
            if extensions is None:
                extensions = node(plant, "modExtensions")
            for previous in extensions.findall("li[@Class='" + EXTENSION + "']"):
                extensions.remove(previous)
            ecology = node(extensions, "li", Class=EXTENSION)
            for key, value in config["seasonProfiles"][settings["seasonProfile"]].items():
                node(ecology, key, value)
            for key in ("ringEligible", "regrowthChance", "regrowthDelayDays"):
                node(ecology, key, settings[key])
            if "glow" in settings:
                comps = plant.find("comps")
                if comps is None:
                    comps = node(plant, "comps")
                for previous in comps.findall("li[@Class='CompProperties_Glower']"):
                    comps.remove(previous)
                light = node(comps, "li", Class="CompProperties_Glower")
                for key, value in (("glowRadius", settings["glow"]["radius"]), ("glowColor", settings["glow"]["color"]), ("overlightRadius", settings["glow"]["overlightRadius"])):
                    node(light, key, value)
            plant.find("description").text = plant.findtext("description").split(" Wild ecology: ")[0] + ecology_description(settings, "English")
            plants[name] = settings
        write_xml(path, document.getroot())
    assert len(plants) == 47

    for language in ("English", "Korean"):
        for filename in ("Mushrooms.xml", "PoisonMushrooms.xml", "ExpansionMushrooms.xml"):
            path = ROOT / "Languages" / language / "DefInjected/ThingDef" / filename
            document = ET.parse(path)
            for entry in document.getroot():
                name = entry.tag.removesuffix(".description")
                if entry.tag.endswith(".description") and name in plants:
                    marker = " Wild ecology: " if language == "English" else " 야생 생태: "
                    entry.text = entry.text.split(marker)[0] + ecology_description(plants[name], language)
            write_xml(path, document.getroot())
        keyed = ET.Element("LanguageData")
        translations = {
            "MM_WildEcologySeasonFactor": ("Seasonal wild emergence multiplier: {0}", "제철에 따른 야생 발생 배율: {0}"),
            "MM_WildEcologyRing": ("May form a fairy ring in the wild.", "야생에서 고리 모양 군락을 이룰 수 있습니다."),
            "MM_WildEcologyRegrowth": ("Harvested wild mycelium may fruit again after {0} days.", "야생에서 수확한 자리의 균사가 {0}일 후 다시 자랄 수 있습니다."),
        }
        for key, pair in translations.items():
            node(keyed, key, pair[language == "Korean"])
        write_xml(ROOT / "Languages" / language / "Keyed/WildEcology.xml", keyed)
    print("Generated ecology settings for 47 mushroom plants, two native glowers and EN/KO ecology descriptions.")


if __name__ == "__main__":
    generate()
