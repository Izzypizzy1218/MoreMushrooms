"""Create versioned Korean-name metadata and gallery; preserve original art bytes."""
import copy,hashlib,json,re,zipfile
from pathlib import Path
OUT=Path(__file__).resolve().parent
PROJECT=Path("C:/Users/집/Desktop/codex/MoreMushrooms")
BASE=PROJECT/"Art/ExpansionMushrooms/v0.1-2026-10-09"
PATCH=PROJECT/"Art/ExpansionMushrooms/v0.1.1-2026-10-09"
OLD=OUT/"mushroom-examples-v01.html"
NEW=OUT/"mushroom-examples-v011.html"
if PATCH.exists() or NEW.exists():
    raise FileExistsError("Versioned naming patch already exists; review rather than overwrite.")
sources={
 "Cubensis":{
  "new":"주사위환각버섯",
  "scientific":"Psilocybe cubensis",
  "name_status":"국내 사용명; 공식 표준국명 여부 미확인",
  "source":{"title":"어린이과학동아 2020년 8호 · 도전! 섭섭박사 실험실",
            "url":"https://images.dongascience.com/uploads/article/pdf/202008/164-167_08%ED%98%B8_%EC%84%AD%EC%84%AD%EC%8B%A4%ED%97%98%EC%8B%A4.pdf",
            "page":2,"printed_page":166,
            "evidence_summary":"사진 설명에서 Psilocybe cubensis와 주사위환각버섯 이름을 함께 표기함."},
  "note":"기존 그림의 학명과 종 선택은 유지. 국내 과학매체에서 실제 사용한 한국어 이름을 채택."},
 "ZombieAntFungus":{
  "new":"개미동충하초",
  "scientific":"Ophiocordyceps unilateralis s.l.",
  "name_status":"국내 통용명; 공식 표준국명 여부 미확인",
  "source":{"title":"최종수 · 버섯에 관한 8가지 놀라운 사실: 야생버섯의 신비(190)",
            "url":"https://www.jadam.kr/news/articleView.html?idxno=16333",
            "section":"3. 어떤 버섯은 개미를 좀비로 만든다",
            "evidence_summary":"사진3과 본문에서 Ophiocordyceps unilateralis를 개미동충하초로 표기함."},
  "note":"좀비개미균의 국내 통용명으로 채택. 다른 개미 기생 동충하초에도 이름이 쓰이므로 학명과 s.l. 범위를 보존. O. formicarum으로 종을 바꾸지 않음.",
  "taxonomy_caveat_source":"https://www.nibr.go.kr/aiibook/catImage/37/Ascomycota.pdf"}
}
def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
before={str(p.relative_to(BASE)):digest(p) for p in BASE.rglob("*") if p.is_file()}
registry=json.loads((BASE/"species.json").read_text(encoding="utf-8"))
registry["baseline_art_version"]=registry["art_version"]
registry["art_version"]="0.1.1"
registry["deliverable"]="Korean display-name metadata patch; reuses unchanged v0.1 artwork"
registry["asset_base"]="../v0.1-2026-10-09"
changes=[]
for row in registry["species"]:
    info=sources.get(row["id"])
    if not info:continue
    assert row["scientific"]==info["scientific"]
    old=row["ko"];row["ko"]=info["new"]
    row["korean_display_aliases"]=[old]
    row["korean_name_status"]=info["name_status"]
    row["korean_name_note"]=info["note"]
    row["korean_name_sources"]=[info["source"]]
    changes.append({"id":row["id"],"old":old,"new":row["ko"],"scientific":row["scientific"],"status":info["name_status"]})
fragment=OLD.read_text(encoding="utf-8")
m=re.search(r'(<script type="application/json" id="mm-example-data">)(.*?)(</script>)',fragment,re.S)
if not m:raise ValueError("No gallery data")
data=json.loads(m.group(2));original_image_uris={r["id"]:{k:r[k] for k in ("photo","low","medium","full","a","b")} for r in data}
for row in data:
    info=sources.get(row["id"])
    if info:
        row["ko"]=info["new"];row["name_status"]=info["name_status"]
        row["name_source"]=info["source"]["url"]
        row["name_note"]=info["note"]
payload=json.dumps(data,ensure_ascii=False,separators=(",",":")).replace("<",r"\u003c")
fragment=fragment[:m.start(2)]+payload+fragment[m.end(2):]
fragment=fragment.replace("more-mushrooms-examples-v01","more-mushrooms-examples-v011")
fragment=fragment.replace("row.scientific+(row.provisional?' · 그림 초안의 잠정 대표종':'')","row.scientific+(row.name_status?' · 국내 사용명':'')+(row.provisional?' · 그림 초안의 잠정 대표종':'')")
fragment=fragment.replace("const initial=window.openai?.widgetState?.privateContent?.species||data[0].id;","const initial=window.openai?.widgetState?.privateContent?.species||'Cubensis';")
assert len(fragment.encode("utf-8"))<1_000_000
assert len(changes)==2 and len(data)==31
assert original_image_uris=={r["id"]:{k:r[k] for k in ("photo","low","medium","full","a","b")} for r in data}
PATCH.mkdir(parents=True)
NEW.write_text(fragment,encoding="utf-8")
(PATCH/"species.json").write_text(json.dumps(registry,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
record={"version":"0.1.1","date":"2026-10-09","type":"Korean display-name patch","baseline_art_version":"0.1","changes":changes,"sources":sources,"unchanged":"Scientific identities, internal IDs, all original photos and all 155 master/exported PNGs","base_art_directory":"../v0.1-2026-10-09","gallery_file":str(NEW),"previous_gallery_sha256":digest(OLD),"updated_gallery_sha256":digest(NEW)}
(PATCH/"NAME-CHANGES.json").write_text(json.dumps(record,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
(PATCH/"README.txt").write_text("More Mushrooms 이름 수정 v0.1.1 / 2026-10-09\n\n표시명 수정: 실로시빈 버섯 추가종 -> 주사위환각버섯; 좀비개미 균류 -> 개미동충하초.\n기존 학명 Psilocybe cubensis와 Ophiocordyceps unilateralis s.l. 및 내부 ID를 유지한다.\n국내 자료에 실제 쓰인 이름이며 공식 표준국명으로 확정한 것은 아니다.\n개미동충하초는 다른 종에도 쓰이는 통용명이므로 학명을 함께 확인한다.\n이 버전은 이름 메타데이터와 비교판의 수정이다. 원본 그림은 v0.1에서 그대로 재사용한다.\nspecies.json은 새 이름을 포함한 전체31종 목록이다. NAME-CHANGES.json은 근거와 변경 기록이다.\npreview.html은 사진과 그림이 내장된 보기용 문서이며 성장 예시는 배치 시뮬레이션이다.\n기존 v0.1 폴더/ZIP과 기존 모드 소스/설치 파일은 수정하지 않았다.\n",encoding="utf-8")
after={str(p.relative_to(BASE)):digest(p) for p in BASE.rglob("*") if p.is_file()}
assert before==after
(PATCH/"BASELINE-PRESERVED.json").write_text(json.dumps({"base_art_unchanged":True,"base_art_files":len(before),"file_sha256":before},ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
print(json.dumps({"version":"0.1.1","changed_names":changes,"gallery":str(NEW),"catalog":str(PATCH/"species.json"),"bytes":NEW.stat().st_size,"all_image_data_unchanged":True,"base_art_unchanged":True,"base_art_files":len(before)},ensure_ascii=False))
