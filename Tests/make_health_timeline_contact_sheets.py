"""Arrange native Health-tab crops, retaining the original unedited frames."""
import csv
import json
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

profile = Path(sys.argv[1]).resolve()
source = profile / "HealthTimeline"
output = Path(sys.argv[2]).resolve()
output.mkdir(parents=True, exist_ok=True)
rows = list(csv.DictReader((source / "health-timeline.csv").open(encoding="utf-8")))
assert len(rows) == 8, f"Expected 8 captured stages, found {len(rows)}"
assert "RESULT PASS" in (source / "health-timeline-report.txt").read_text(encoding="utf-8")
font = r"C:\Windows\Fonts\malgun.ttf"
bold = r"C:\Windows\Fonts\malgunbd.ttf"
title = ImageFont.truetype(bold, 27)
label = ImageFont.truetype(bold, 22)
small = ImageFont.truetype(font, 17)
names = [
    "① 잠복기", "② 증상 발현", "③ 위장 증상 완화 · 중독은 진행", "④ 심한 간 손상",
    "⑤ 첫 치료 후 · 아직 악화 중", "⑥ 반복 치료 후 안정화", "⑦ 회복 중", "⑧ 회복 완료",
]
for page in range(2):
    canvas = Image.new("RGB", (1326, 1180), "#ece8df")
    draw = ImageDraw.Draw(canvas)
    draw.text((22, 16), f"More Mushrooms · 독우산광대버섯 중독 경과 {page + 1}/2", font=title, fill="#20262a")
    draw.text((22, 57), "실제 게임 건강 탭 캡처 · 독우산광대버섯 1개 · 일반 약품으로 치료", font=small, fill="#454d52")
    for slot in range(4):
        index = page * 4 + slot
        row = rows[index]
        image = Image.open(source / (row["frame"] + ".png")).convert("RGB")
        assert image.size == (1600, 1000), image.size
        # Exact unscaled pixels of the native Health inspect tab and tab bar.
        crop = image.crop((0, 340, 630, 800))
        x = 22 + (slot % 2) * 652
        y = 101 + (slot // 2) * 517
        draw.text((x, y), names[index], font=label, fill="#20262a")
        hours = float(row["hoursAfterIngest"])
        severity = float(row["severity"])
        severity_label = f"중독 {severity:.0%}" if row["active"] == "True" else "중독 항목 소멸"
        subtitle = f"섭취 후 {hours:.1f}시간 · {severity_label} · 치료 {row['tendCount']}회"
        draw.text((x, y + 31), subtitle, font=small, fill="#454d52")
        canvas.paste(crop, (x, y + 56))
    draw.text((22, 1148), "원본 건강 탭을 잘라 모음 / 섭취·치료는 실제 작업, 중간 경과는 건강 틱만 압축", font=small, fill="#454d52")
    path = output / f"health-timeline-page-{page + 1}.png"
    canvas.save(path)
    print(path)
(output / "timeline-data.json").write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8")
