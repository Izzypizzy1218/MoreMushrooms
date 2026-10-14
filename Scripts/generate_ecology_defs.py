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

# These names describe observable game conditions. They are not claims that the
# simplified RimWorld substrate and host rules reproduce real fungal biology.
HABITAT_TEXT = {
    "English": {
        "openField": "open soil with wild grass within 2 tiles and at most one living tree within 5 tiles",
        "forestEdge": "soil with 1–3 living trees within 5 tiles",
        "treeBase": "soil within 2 tiles of a living tree at least 50% grown",
        "stump": "soil within 2 tiles of a stump or dead tree",
        "pineStump": "soil within 2 tiles of a stump, dead tree or living pine at least 50% grown",
        "insectHost": "soil within 2 tiles of a living RimWorld insect or an insect corpse",
        "mushroomHost": "soil within 2 tiles of an existing wild mushroom of another species",
        "indoorSoil": "dim, roofed fertile soil without constructed flooring",
        "exposedWood": "wooden flooring, or suitable soil within 2 tiles of a wooden building, exposed outdoors or beside a roof opening",
        "wetMargin": "soil within 2 tiles of water or mud",
    },
    "Korean": {
        "openField": "2칸 안에 야생 풀이 있고 5칸 안에 살아 있는 나무가 1그루 이하인 들판의 흙",
        "forestEdge": "5칸 안에 살아 있는 나무가 1~3그루 있는 숲 가장자리의 흙",
        "treeBase": "성장률 50% 이상인 살아 있는 나무에서 2칸 이내의 흙",
        "stump": "그루터기나 죽은 나무에서 2칸 이내의 흙",
        "pineStump": "그루터기·죽은 나무 또는 성장률 50% 이상인 소나무에서 2칸 이내의 흙",
        "insectHost": "살아 있는 림월드 곤충이나 곤충 사체에서 2칸 이내의 흙",
        "mushroomHost": "다른 종류의 야생 버섯에서 2칸 이내의 흙",
        "indoorSoil": "바닥을 깔지 않은 어두운 지붕 아래의 비옥한 흙",
        "exposedWood": "야외나 지붕 구멍 주변의 나무바닥, 또는 목조 건물에서 2칸 이내의 적합한 흙",
        "wetMargin": "물이나 진흙에서 2칸 이내의 흙",
    },
}


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
        result += " New wild emergence may form irregular same-species clumps of 2–5 plants where space permits."
        if settings.get("habitatTree"):
            tree = {"pine": "a pine", "birch": "a birch", "any": "a tree"}[settings["habitatTree"]]
            result += f" Wild emergence requires suitable soil within {settings['habitatTreeRadius']} tiles of {tree} at least {settings['habitatTreeMinGrowth']:.0%} grown."
        habitat = settings.get("habitat", "soil")
        if habitat != "soil":
            result += " Wild habitat: " + HABITAT_TEXT[language][habitat] + "."
            if habitat == "insectHost":
                result += " Host proximity is a game rule and does not infect the insect."
            elif habitat == "exposedWood":
                result += " Does not damage the floor or building."
        if settings["ringEligible"]:
            result += " May form a small fairy ring."
        if settings["regrowthChance"] > 0:
            result += f" After harvesting in the wild, surviving mycelium has a {settings['regrowthChance']:.0%} chance to fruit again after {settings['regrowthDelayDays']:g} days if the site remains suitable."
        if "glow" in settings:
            result += " Emits a small amount of light."
    else:
        result = " 야생 생태: " + seasons[language][profile] + "에 더 잘 발생합니다. 제철은 재배 성장 속도를 바꾸지 않습니다."
        result += " 새로 발생하는 야생 버섯은 자리가 적합하면 같은 종 2~5칸의 불규칙한 작은 군락을 이룰 수 있습니다."
        if settings.get("habitatTree"):
            tree = {"pine": "소나무", "birch": "자작나무", "any": "나무"}[settings["habitatTree"]]
            result += f" 야생 발생에는 성장률 {settings['habitatTreeMinGrowth']:.0%} 이상인 {tree}에서 {settings['habitatTreeRadius']}칸 이내의 적합한 흙이 필요합니다."
        habitat = settings.get("habitat", "soil")
        if habitat != "soil":
            result += " 야생 발생 장소: " + HABITAT_TEXT[language][habitat] + "."
            if habitat == "insectHost":
                result += " 곤충 주변에서 발생하는 게임 규칙이며 숙주를 감염시키지는 않습니다."
            elif habitat == "exposedWood":
                result += " 나무바닥이나 건물을 손상시키지는 않습니다."
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
            if settings.get("habitat", "soil") != "soil":
                for key in ("habitat", "habitatRadius"):
                    node(ecology, key, settings[key])
            if settings.get("habitatTree"):
                for key in ("habitatTree", "habitatTreeMinGrowth", "habitatTreeRadius"):
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
