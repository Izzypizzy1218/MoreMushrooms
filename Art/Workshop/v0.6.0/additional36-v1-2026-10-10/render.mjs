import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';

// Lay out existing artwork as a catalog. Original PNGs remain unchanged.
const directory = path.dirname(fileURLToPath(import.meta.url));
const project = path.resolve(directory, '../../../..');
const catalog = JSON.parse(fs.readFileSync(path.join(directory, 'catalog.json'), 'utf8'));
const runtimeModules = 'C:/Users/집/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules';
const require = createRequire(import.meta.url);
const { chromium } = require(path.join(runtimeModules, 'playwright'));
const hash = (buffer) => crypto.createHash('sha256').update(buffer).digest('hex');
const escape = (value) => value.replace(/[&<>"']/g, (c) => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' }[c]));

const style = `
@font-face{font-family:CatalogKR;src:url('file:///C:/Windows/Fonts/NotoSansKR-VF.ttf');font-weight:100 900}
*{box-sizing:border-box}
html,body{margin:0;padding:0;background:#f4f1e8;color:#424b41;font-family:CatalogKR,'Malgun Gothic',sans-serif}
.sheet{width:1800px;height:1120px;padding:28px 32px 22px;background:#f4f1e8;overflow:hidden}
header{height:140px;display:flex;justify-content:space-between;align-items:flex-start}
.brand{font-size:16px;font-weight:700;letter-spacing:3px;line-height:26px;color:#7c8575}
h1{font-size:44px;font-weight:600;line-height:64px;letter-spacing:-1.7px;margin:0}
.subtitle{font-size:17px;line-height:27px;color:#8c9283;letter-spacing:.3px}
.page{font-size:25px;font-weight:500;letter-spacing:3px;line-height:40px;margin-top:28px;color:#7d8776}
.grid{display:grid;grid-template-columns:repeat(6,1fr);grid-template-rows:repeat(3,286px);gap:16px}
.card{background:#e6e5da;border-radius:19px;padding:12px 17px 14px;overflow:hidden}
.picture{height:218px;display:flex;align-items:center;justify-content:center;padding:4px 5px 5px}
svg{display:block;width:100%;height:100%;overflow:hidden}
.ko{height:31px;line-height:31px;font-weight:500;font-size:25px;letter-spacing:-.65px;white-space:nowrap}
.en{height:20px;line-height:20px;font-weight:450;font-size:13px;letter-spacing:.8px;color:#929887;white-space:nowrap}
footer{margin-top:23px;border-top:1px solid #d8d9cc;padding-top:12px;display:flex;justify-content:space-between;align-items:center;color:#8a9281;font-size:14px;letter-spacing:1px}
`;

function card(species) {
  const [x1,y1,x2,y2] = species.alphaBounds16;
  const pad = 28;
  const x = Math.max(0,x1-pad), y = Math.max(0,y1-pad);
  const width = Math.min(species.width,x2+pad)-x;
  const height = Math.min(species.height,y2+pad)-y;
  const imageURL = pathToFileURL(path.join(project,species.source)).href;
  return `<article class="card" data-id="${species.id}"><div class="picture"><svg viewBox="${x} ${y} ${width} ${height}" preserveAspectRatio="xMidYMid meet" aria-label="${escape(species.ko)}"><image href="${escape(imageURL)}" width="${species.width}" height="${species.height}"/></svg></div><div class="ko">${escape(species.ko)}</div><div class="en">${escape(species.en.toUpperCase())}</div></article>`;
}

const executable = chromium.executablePath();
const browser = await chromium.launch({headless:true, ...(fs.existsSync(executable) ? {} : {channel:'msedge'})});
const page = await browser.newPage({viewport:{width:1800,height:1120},deviceScaleFactor:1});
const outputs = [];
try {
  for(let number=1;number<=2;number++) {
    const rows = catalog.species.filter((s)=>s.page===number);
    if(rows.length!==18) throw new Error('Each page must contain 18 mushrooms.');
    const html = `<!doctype html><html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=1800"><title>More Mushrooms — 추가 버섯 36종 (${number}/2)</title><style>${style}</style></head><body><main class="sheet"><header><div><div class="brand">MORE MUSHROOMS</div><h1>추가 버섯 36종</h1><div class="subtitle">처음 만나는 다양한 버섯들 · ${number===1?'첫 번째':'두 번째'} 모음</div></div><div class="page">0${number} / 02</div></header><section class="grid">${rows.map(card).join('')}</section><footer><span>MORE MUSHROOMS · v0.6.0</span><span>${number===1?'01—18':'19—36'} / 36</span></footer></main></body></html>`;
    const basename = `MoreMushrooms-additional36-page${number}-v1`;
    const htmlPath = path.join(directory, basename+'.html');
    fs.writeFileSync(htmlPath,html,'utf8');
    await page.goto(pathToFileURL(htmlPath).href,{waitUntil:'load'});
    await page.evaluate(async()=>{
      await document.fonts.ready;
      for(const label of document.querySelectorAll('.ko,.en')) {
        let size=parseFloat(getComputedStyle(label).fontSize);
        while(label.scrollWidth>label.clientWidth && size>11) {
          size-=0.5;
          label.style.fontSize=size+'px';
        }
      }
    });
    // SVG images load as part of the document's load event. Check every href
    // independently as well so a missing local source cannot produce a blank tile.
    const checks = await page.evaluate(async()=>{
      const images = await Promise.all([...document.querySelectorAll('svg image')].map(async(el)=>{
        const image=new Image(); image.src=el.getAttribute('href'); await image.decode();
        return {width:image.naturalWidth,height:image.naturalHeight};
      }));
      const labels=[...document.querySelectorAll('.ko,.en')].every((el)=>el.scrollWidth<=el.clientWidth);
      return {images:images.length,labelsFit:labels,contentHeight:document.querySelector('footer').getBoundingClientRect().bottom};
    });
    if(checks.images!==18 || !checks.labelsFit || checks.contentHeight>1120) throw new Error('Catalog layout validation failed: '+JSON.stringify(checks));
    for(const extension of ['png','jpg']) {
      const filename=basename+'.'+extension;
      const target=path.join(directory,filename);
      if(fs.existsSync(target)) throw new Error('Do not overwrite an existing image version: '+filename);
      const bytes=await page.screenshot({path:target,fullPage:false,type:extension==='png'?'png':'jpeg',...(extension==='jpg'?{quality:95}:{})});
      outputs.push({file:filename,page:number,size:bytes.length,sha256:hash(bytes),pixelSize:[1800,1120]});
    }
  }
} finally {
  await browser.close();
}
for(const species of catalog.species) {
  if(hash(fs.readFileSync(path.join(project,species.source)))!==species.sourceSha256) throw new Error('Source artwork changed: '+species.id);
  if(hash(fs.readFileSync(path.join(project,species.gameAsset)))!==species.gameAssetSha256) throw new Error('Game artwork changed: '+species.id);
}
fs.writeFileSync(path.join(directory,'manifest.json'),JSON.stringify({artVersion:1,modVersion:'0.6.0',species:36,pages:2,outputs,sourceImagesUnchanged:true,gameImagesUnchanged:true},null,2),'utf8');
console.log(JSON.stringify({outputDirectory:directory,outputs},null,2));
