"""Read-only v0.6.5 weight-policy and v0.6.4 runtime preservation audit.

Only existing plant wildBiomes, Balance weight/audit metadata, release versions
and status text may change. This verifies definitions, not realized spawn counts.
"""
from copy import deepcopy
from pathlib import Path
import hashlib
import json
import sys
import xml.etree.ElementTree as ET
import zipfile

import validate_assets

ROOT = Path(__file__).resolve().parents[1]
BASE = ROOT / "Releases/v0.6.4"
PREFIXES = ("About/", "Assemblies/", "Defs/", "Languages/", "Textures/", "Credits/", "Patches/")
ANCHORS = {
    "MoreMushrooms-v0.6.4.zip": "ebae0e6ae44d4a9bb17ec0879f12184cc3d3d8606059f333be76138d85501f42",
    "MoreMushrooms-v0.6.4-source.zip": "1c1b0d467d9638ac53102c56e3827d7415b87eea25d0f9a689199d844f842ded",
}
checks = 0
errors = []


def check(condition, message):
    global checks
    checks += 1
    if not condition:
        errors.append(message)


def sha(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def relative(name):
    return name.removeprefix("MoreMushrooms/")


def signature(node):
    return (node.tag, tuple(sorted(node.attrib.items())), (node.text or "").strip(),
            tuple(signature(child) for child in node))


def normalized_xml(data, path, wild_names):
    tree = ET.fromstring(data)
    if path == "About/About.xml":
        tree.find("modVersion").text = "APPROVED_VERSION_CHANGE"
    elif path.startswith("Defs/"):
        for definition in tree:
            if definition.tag == "ThingDef" and definition.findtext("defName") in wild_names:
                plant = definition.find("plant")
                if plant is not None:
                    for record in list(plant):
                        if record.tag == "wildBiomes":
                            plant.remove(record)
    return signature(tree)


def normalized_balance(data, path):
    data = deepcopy(data)
    if path in {"Balance/mushrooms.json", "Balance/poison_mushrooms.json", "Balance/expansion_mushrooms.json", "Balance/wild_ecology.json"}:
        for key in ("version", "contentVersion", "status"):
            if key in data:
                data[key] = "APPROVED_METADATA_CHANGE"
    if path == "Balance/wild_ecology.json":
        data["commonality"] = "APPROVED_WILD_WEIGHT_POLICY"
    elif path in {"Balance/mushrooms.json", "Balance/poison_mushrooms.json", "Balance/expansion_mushrooms.json"}:
        for mushroom in data["mushrooms"]:
            mushroom["biomes"] = "APPROVED_WILD_WEIGHTS"
        if path == "Balance/expansion_mushrooms.json":
            data["newWildCommonalityTotals"] = "DERIVED_WILD_WEIGHT_TOTALS"
    return data


def main():
    for name, expected in ANCHORS.items():
        check((BASE / name).is_file() and sha(BASE / name) == expected, "immutable baseline archive changed: " + name)
    if errors:
        raise AssertionError("; ".join(errors))
    with zipfile.ZipFile(BASE / "MoreMushrooms-v0.6.4-source.zip") as source:
        old_balances = {relative(name): json.loads(source.read(name)) for name in source.namelist()
                        if name.startswith("MoreMushrooms/Balance/") and name.endswith(".json")}
        old_registry = {m["id"]: m for path in ("Balance/mushrooms.json", "Balance/poison_mushrooms.json", "Balance/expansion_mushrooms.json")
                        for m in old_balances[path]["mushrooms"]}
        check(len(old_registry) == 46, "baseline contains exactly forty-six species")
        wild_names = {"RMush_Plant" + ("EnokiWild" if identity == "Enoki" else identity) for identity in old_registry}
        production = {relative(name): source.read(name) for name in source.namelist()
                      if name.startswith("MoreMushrooms/Source/") and not name.endswith("/")}
        production["MoreMushrooms.csproj"] = source.read("MoreMushrooms/MoreMushrooms.csproj")
        current_sources = {p.relative_to(ROOT).as_posix() for p in (ROOT / "Source").rglob("*") if p.is_file()} | {"MoreMushrooms.csproj"}
        check(current_sources == set(production), "production source/project file set changed")
        for path, old_data in production.items():
            check((ROOT / path).is_file() and (ROOT / path).read_bytes() == old_data, "production source/project changed: " + path)
        current_balance_paths = {p.relative_to(ROOT).as_posix() for p in (ROOT / "Balance").glob("*.json")}
        check(current_balance_paths == set(old_balances), "Balance JSON file set changed")
        new_balances = {}
        for path, old_data in old_balances.items():
            new_data = json.loads((ROOT / path).read_text(encoding="utf-8"))
            new_balances[path] = new_data
            check(normalized_balance(old_data, path) == normalized_balance(new_data, path),
                  "non-weight balance values, identities, descriptions or ecology profiles changed: " + path)
        config = new_balances["Balance/wild_ecology.json"]
        check(config["commonality"]["baselineBiomes"] == {identity: mushroom["biomes"] for identity, mushroom in old_registry.items()},
              "policy baseline weights equal the anchored v0.6.4 archive")
        check(config["settings"] == old_balances["Balance/wild_ecology.json"]["settings"], "ecology settings preserved exactly")
        check(config["seasonProfiles"] == old_balances["Balance/wild_ecology.json"]["seasonProfiles"], "four-season emergence profiles preserved exactly")
        check(config["species"] == old_balances["Balance/wild_ecology.json"]["species"], "per-species season, glow, ring and regrowth profiles preserved exactly")
        balance, poison, expansion = (new_balances["Balance/" + name + ".json"] for name in ("mushrooms", "poison_mushrooms", "expansion_mushrooms"))
        check(balance["version"] == expansion["contentVersion"] == config["contentVersion"] == "0.6.5", "all candidate release versions are v0.6.5")
        check(all(value == "0.6.5" for document in (balance, poison, expansion, config)
                  for key, value in document.items() if key in {"version", "contentVersion"}), "all present release metadata versions are v0.6.5")
        registry = {m["id"]: m for m in balance["mushrooms"] + poison["mushrooms"] + expansion["mushrooms"]}
        cultivable = {m["id"] for m in balance["mushrooms"] if not m.get("wildOnly", False)} | {m["id"] for m in expansion["mushrooms"] if m["cultivable"]}
        previous_policy_checks = validate_assets.checks
        validate_assets.validate_v065_wild_policy(config, registry, cultivable)
        policy_checks = validate_assets.checks - previous_policy_checks
        expansion_totals = {biome: sum(m["biomes"].get(biome, 0) for m in expansion["mushrooms"])
                            for biome in config["commonality"]["biomeAudit"]}
        check(set(expansion["newWildCommonalityTotals"]) == set(expansion_totals)
              and all(abs(expansion["newWildCommonalityTotals"][b] - v) < 1e-10 for b, v in expansion_totals.items()),
              "derived expansion commonality totals equal the new species weights")
        old_audit = json.loads(source.read("MoreMushrooms/Docs/TestResults/v0.6.4-Comparison.json"))
        old_archives = old_audit["oldReleaseArchiveHashes"]
        check(len(old_archives) == 34, "anchored v0.6.4 audit contains thirty-four earlier immutable release archives")
        for entry in old_archives:
            path = ROOT / entry["path"]
            check(path.is_file() and sha(path) == entry["expected"].lower(), "earlier release archive changed: " + entry["path"])

    definition_count = 0
    with zipfile.ZipFile(BASE / "MoreMushrooms-v0.6.4.zip") as runtime:
        runtime_paths = {relative(name): name for name in runtime.namelist() if relative(name).startswith(PREFIXES) and not name.endswith("/")}
        current_paths = {p.relative_to(ROOT).as_posix() for prefix in PREFIXES for p in (ROOT / prefix).rglob("*") if p.is_file()}
        check(current_paths == set(runtime_paths), "runtime file set changed")
        for path, name in runtime_paths.items():
            current = ROOT / path
            if not current.is_file():
                continue
            old_data, new_data = runtime.read(name), current.read_bytes()
            if path.startswith("Defs/") and path.endswith(".xml"):
                definition_count += len(ET.fromstring(old_data))
                check(normalized_xml(old_data, path, wild_names) == normalized_xml(new_data, path, wild_names),
                      "runtime definition changed outside existing wildBiomes: " + path)
                for definition in ET.fromstring(new_data):
                    plant_name = definition.findtext("defName")
                    if plant_name in wild_names:
                        identity = plant_name.removeprefix("RMush_Plant")
                        identity = "Enoki" if identity == "EnokiWild" else identity
                        records = definition.findall("plant/wildBiomes/*")
                        weights = {record.tag: float(record.text) for record in records}
                        check(len(records) == len(weights) and weights == registry[identity]["biomes"],
                              "generated wild registrations and weights equal the audited Balance policy: " + plant_name)
            elif path == "About/About.xml":
                check(ET.fromstring(new_data).findtext("modVersion") == "0.6.5", "candidate About version is v0.6.5")
                check(normalized_xml(old_data, path, wild_names) == normalized_xml(new_data, path, wild_names), "About metadata changed outside release version")
            else:
                check(old_data == new_data, "runtime binary, art, translation or patch changed: " + path)
        check((ROOT / "Assemblies/RimMushrooms.dll").read_bytes() == runtime.read("MoreMushrooms/Assemblies/RimMushrooms.dll"), "production DLL remains byte-identical to v0.6.4")

    result = {"status": "FAIL" if errors else "PASS", "checks": checks + policy_checks, "preservationChecks": checks,
              "policyChecks": policy_checks, "baseline": "v0.6.4", "candidate": "v0.6.5", "runtimeFiles": len(runtime_paths),
              "existingDefinitions": definition_count, "unchangedProductionSourceAndProjectFiles": len(production),
              "immutableReleaseArchives": len(old_archives) + len(ANCHORS),
              "scope": "Existing wildBiomes and their derived Balance policy/audit metadata, release versions and status text only. Production sources/DLL, identities, artwork, mood, growth, yield, food/poison values, descriptions, ecology settings and season/ring/regrowth profiles preserved. Weight arithmetic is not a realized population measurement.",
              "differences": errors}
    print(json.dumps(result, indent=2, ensure_ascii=False))
    return 1 if errors else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(json.dumps({"status": "FAIL", "checks": checks + validate_assets.checks, "error": str(error), "differences": errors}, indent=2, ensure_ascii=False))
        sys.exit(1)
