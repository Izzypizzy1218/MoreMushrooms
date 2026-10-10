const fs = require('fs');
const path = require('path');
const {pathToFileURL} = require('url');
const {chromium} = require('C:/Users/집/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const patch = 'C:/Users/집/Desktop/codex/MoreMushrooms/Art/ExpansionMushrooms/v0.1.1-2026-10-09';
const expected = {
  Cubensis: {name: '주사위환각버섯', scientific: 'Psilocybe cubensis'},
  ZombieAntFungus: {name: '개미동충하초', scientific: 'Ophiocordyceps unilateralis s.l.'}
};
(async () => {
  const browser = await chromium.launch({headless: true, executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe'});
  try {
    const page = await browser.newPage({viewport: {width: 768, height: 1024}});
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    await page.goto(pathToFileURL(path.join(patch, 'preview.html')).href);
    const frame = page.frameLocator('iframe');
    const root = frame.locator('#more-mushrooms-examples-v011');
    await frame.locator('#mm-example-species option').last().waitFor({state: 'attached'});
    const options = await frame.locator('#mm-example-species option').evaluateAll(os => os.map(o => ({id: o.value, name: o.textContent})));
    if (options.length !== 31) throw Error('Species choices ' + options.length);
    const readState = async () => {
      await root.evaluate(async el => Promise.all([...el.querySelectorAll('img')].map(im => im.decode())));
      await page.waitForTimeout(100);
      return root.evaluate(el => ({
        name: el.querySelector('#mm-example-name').textContent,
        images: [...el.querySelectorAll('img')].every(im => im.complete && im.naturalWidth > 0),
        stages: [...el.querySelectorAll('canvas')].map(c => ({aria: c.getAttribute('aria-label'), pixels: [...c.getContext('2d').getImageData(0, 0, c.width, c.height).data].filter((v, i) => i % 4 === 3 && v > 0).length})),
        overflow: document.documentElement.scrollWidth > document.documentElement.clientWidth + 1,
        text: el.innerText
      }));
    };
    const results = [];
    for (const option of options) {
      await frame.locator('#mm-example-species').selectOption(option.id);
      const state = await readState();
      if (!state.images || state.stages.length !== 4 || state.stages.some(s => !s.aria || s.pixels < 100) || state.name !== option.name || state.overflow) throw Error('Invalid render ' + option.id);
      if (expected[option.id] && (state.name !== expected[option.id].name || !state.text.includes(expected[option.id].scientific) || !state.text.includes('국내 사용명'))) throw Error('Name or taxon mismatch ' + option.id);
      results.push({id: option.id, name: state.name, images: true, growth_stages: 4});
    }
    const layouts = [];
    for (const width of [320, 360, 736]) {
      await page.setViewportSize({width, height: 1300});
      for (const id of Object.keys(expected)) {
        await frame.locator('#mm-example-species').selectOption(id);
        const state = await readState();
        if (state.overflow) throw Error('Overflow ' + width + '/' + id);
        layouts.push({width, id, overflow: false});
      }
    }
    await page.setViewportSize({width: 768, height: 1024});
    for (const id of Object.keys(expected)) {
      await frame.locator('#mm-example-species').selectOption(id);
      await readState();
      await page.screenshot({path: path.join(__dirname, 'mushroom-examples-v011-' + id + '.png'), fullPage: true});
    }
    if (errors.length) throw Error('JS errors ' + JSON.stringify(errors));
    const report = {version: '0.1.1', species: 31, displayed_images_checked: 124, growth_stages_checked: 124, layouts, js_errors: errors, results};
    fs.writeFileSync(path.join(patch, 'GALLERY-QA.json'), JSON.stringify(report, null, 2));
    console.log(JSON.stringify({species: 31, all_images_ready: true, all_growth_stages_drawn: true, layouts, js_errors: errors}));
  } finally {
    await browser.close();
  }
})().catch(error => {console.error(error); process.exitCode = 1;});
