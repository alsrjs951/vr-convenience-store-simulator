// index.html의 각 장을 그림으로 찍어 PPTX로 만든다(make-slide 스킬의 screenshot 방식).
// 글자는 PowerPoint에서 고칠 수 없다. 고치려면 index.html을 고치고 다시 실행한다.
//
//   npm install pptxgenjs puppeteer-core   (아무 폴더에나, 그 폴더의 node_modules를 NODE_PATH로 준다)
//   NODE_PATH=<그 폴더>/node_modules node docs/p5/slides/make_pptx.cjs
//
// 결과: docs/p5/slides/동아편의점_발표.pptx, 확인용 그림 docs/p5/slides/shots/slide_NN.png
const fs = require('fs');
const path = require('path');
const { pathToFileURL } = require('url');
const puppeteer = require('puppeteer-core');
const PptxGenJS = require('pptxgenjs');

const BROWSERS = [
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
];

(async () => {
  const here = __dirname;
  const html = path.join(here, 'index.html');
  const shots = path.join(here, 'shots');
  fs.mkdirSync(shots, { recursive: true });
  const executablePath = BROWSERS.find((p) => fs.existsSync(p));
  if (!executablePath) throw new Error('Chrome이나 Edge를 찾지 못했다');

  const browser = await puppeteer.launch({ executablePath, headless: true, args: ['--allow-file-access-from-files'] });
  const page = await browser.newPage();
  await page.setViewport({ width: 1280, height: 720, deviceScaleFactor: 2 });
  await page.goto(pathToFileURL(html).href, { waitUntil: 'networkidle0', timeout: 60000 });

  // 애니메이션을 끄고, 화면에 떠 있는 조작 안내는 숨긴다
  await page.addStyleTag({
    content: '*, *::before, *::after { animation: none !important; transition: none !important; animation-delay: 0s !important; } ' +
      '.slide.active .a, .a { opacity: 1 !important; transform: none !important; } ' +
      '.hint, .fullscreen-btn, .progress-bar, .slide-counter { display: none !important; }',
  });
  await page.evaluateHandle('document.fonts.ready');

  const notes = await page.$$eval('.slide', (els) => els.map((el) => el.dataset.notes || ''));
  const report = [];
  const pptx = new PptxGenJS();
  pptx.layout = 'LAYOUT_WIDE'; // 13.33 x 7.5 in, 16:9
  pptx.title = '동아편의점';

  for (let index = 0; index < notes.length; index += 1) {
    await page.evaluate((idx) => {
      document.querySelectorAll('.slide').forEach((s, i) => {
        if (i === idx) {
          s.style.display = 'flex'; s.style.opacity = '1'; s.style.visibility = 'visible'; s.classList.add('active');
        } else {
          s.style.display = 'none'; s.style.opacity = '0'; s.style.visibility = 'hidden'; s.classList.remove('active');
        }
      });
    }, index);
    await new Promise((r) => setTimeout(r, 500));

    // 넘친 곳과 안 뜬 그림을 적어 둔다
    const check = await page.evaluate((idx) => {
      const slide = document.querySelectorAll('.slide')[idx];
      const box = slide.getBoundingClientRect();
      let lowest = 0;
      let widest = 0;
      slide.querySelectorAll('*').forEach((el) => {
        const r = el.getBoundingClientRect();
        if (r.width === 0 && r.height === 0) return;
        if (el.closest('.shot')) return; // 일부러 잘라 보이는 그림
        lowest = Math.max(lowest, r.bottom - box.top);
        widest = Math.max(widest, r.right - box.left);
      });
      const broken = [...slide.querySelectorAll('img')].filter((img) => !img.complete || img.naturalWidth === 0).map((img) => img.getAttribute('src'));
      return { lowest: Math.round(lowest), widest: Math.round(widest), broken };
    }, index);
    report.push({ slide: index + 1, ...check });

    const file = path.join(shots, `slide_${String(index + 1).padStart(2, '0')}.png`);
    const png = await page.screenshot({ path: file, type: 'png' });
    const slide = pptx.addSlide();
    slide.addImage({ data: `image/png;base64,${Buffer.from(png).toString('base64')}`, x: 0, y: 0, w: '100%', h: '100%' });
    if (notes[index]) slide.addNotes(notes[index]);
  }

  await browser.close();
  const out = path.join(here, '동아편의점_발표.pptx');
  await pptx.writeFile({ fileName: out });
  for (const r of report) {
    const over = r.lowest > 720 || r.widest > 1280 ? '  <-- 넘침' : '';
    const broken = r.broken.length ? `  <-- 안 뜬 그림: ${r.broken.join(', ')}` : '';
    console.log(`slide ${r.slide}: bottom ${r.lowest}/720, right ${r.widest}/1280${over}${broken}`);
  }
  console.log(`${notes.length} slides -> ${out}`);
})().catch((err) => { console.error(err); process.exit(1); });
