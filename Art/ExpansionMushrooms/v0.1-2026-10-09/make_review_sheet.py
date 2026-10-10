"""Contact sheets for human QA; original sprites remain unchanged."""
import argparse, json, math
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
ROOT = Path(__file__).resolve().parent
KINDS = ("01Low","02Medium","03Full","PlantA","PlantB")
def font(size):
    for path in ("C:/Windows/Fonts/malgun.ttf","C:/Windows/Fonts/arial.ttf"):
        if Path(path).exists():
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()
def texture(identity, kind):
    plant = kind.startswith("Plant")
    name = identity+kind[-1] if plant else kind
    return ROOT/"Textures"/"Things"/("Plant" if plant else "Item")/"RimMushrooms"/identity/(name+".png")
def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--species", nargs="+")
    args=parser.parse_args()
    rows=json.loads((ROOT/"species.json").read_text(encoding="utf-8-sig"))["species"]
    if args.species:
        rows=[s for s in rows if s["id"] in args.species]
    for species in rows:
        for kind in KINDS:
            if not texture(species["id"],kind).is_file():
                raise FileNotFoundError(texture(species["id"],kind))
    out=ROOT/"reviews"/"contact-sheets"
    out.mkdir(parents=True,exist_ok=True)
    sheets=[]
    for index in range(math.ceil(len(rows)/7)):
        page=rows[index*7:(index+1)*7]
        canvas=Image.new("RGB",(1120,92+len(page)*204),(27,31,30))
        draw=ImageDraw.Draw(canvas)
        draw.text((20,9),"More Mushrooms - Art v0.1 / 2026-10-09",font=font(23),fill=(243,224,177))
        labels=("수확물 적음","수확물 중간","수확물 가득","성장 단위 A","성장 단위 B")
        for col,label in enumerate(labels):
            draw.text((230+col*174,50),label,font=font(18),fill=(223,224,215))
        for row,species in enumerate(page):
            y=84+row*204
            draw.text((16,y+40),species["ko"],font=font(20),fill=(241,226,187))
            draw.text((16,y+73),species["id"],font=font(16),fill=(169,179,173))
            for col,kind in enumerate(KINDS):
                x=222+col*174
                for cy in range(0,176,16):
                    for cx in range(0,160,16):
                        fill=(44,50,47) if (cx//16+cy//16)%2 else (52,57,53)
                        draw.rectangle((x+cx,y+cy,x+cx+15,y+cy+15),fill=fill)
                image=Image.open(texture(species["id"],kind)).convert("RGBA")
                image.thumbnail((160,160),Image.Resampling.LANCZOS)
                canvas.paste(image,(x+(160-image.width)//2,y+(176-image.height)//2),image)
            draw.line((14,y+195,1105,y+195),fill=(77,84,77))
        suffix=("-"+"-".join(args.species)) if args.species else ""
        path=out/("review"+suffix+"-"+str(index+1).zfill(2)+".png")
        canvas.save(path)
        sheets.append(str(path))
    print(json.dumps({"contact_sheets":sheets,"review_species":len(rows)},ensure_ascii=False))
if __name__=="__main__":
    main()

