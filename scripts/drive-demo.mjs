// Drives the demo page in a real Chrome against the running Compose stack: two live turns
// against the configured provider, sampling the DOM every 120 ms to record when retrieval,
// the tool pill, the first word of the answer and the usage card appear; then a request the
// server refuses before committing, to see the page render a problem rather than nothing.
// Records console errors, page errors and failed requests, checks the rendered markdown
// against the raw text, and screenshots each turn. Run by hand; see docs/demo-ui.md.
//
//   DEMO=http://localhost:8082 SHOTS=/tmp/shots \
//   PLAYWRIGHT_MODULE=/path/to/node_modules/playwright/index.mjs CHROME=/path/to/chrome \
//   node scripts/drive-demo.mjs
//
// Two turns cost real model calls. The first asks about an order so the tool runs; the
// second is in Chinese with no order, so only retrieval runs and the answer's language is
// the model's choice to make.
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const base = process.env.DEMO ?? 'http://localhost:8082';
const shots = process.env.SHOTS ?? '.';
const browser = await chromium.launch({
  executablePath: process.env.CHROME ?? '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  headless: true,
});
const page = await browser.newPage({ viewport: { width: 1440, height: 950 } });
const problems = [];
page.on('console', m => { if (m.type() === 'error' || m.type() === 'warning') problems.push(`console.${m.type()}: ${m.text()}`); });
page.on('pageerror', e => problems.push(`pageerror: ${e.message}`));
page.on('response', r => { if (r.status() >= 400) problems.push(`http ${r.status()} ${r.request().method()} ${r.url()}`); });
let failed = 0;
const check = (ok, what) => { console.log(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) failed++; };

// One snapshot of everything the page shows for the current turn.
const snapshot = () => page.evaluate(() => {
  const bots = [...document.querySelectorAll('#log .msg.bot')];
  const bot = bots[bots.length - 1];
  const cards = [...document.querySelectorAll('#panel .card')].map(c => c.querySelector('h2')?.textContent ?? '');
  return {
    retrieval: cards.find(t => t.startsWith('Retrieved')) ?? null,
    pills: [...document.querySelectorAll('#panel .pill')].map(p => p.textContent),
    usage: cards.includes('What the turn cost'),
    // innerText, not textContent: block boundaries become newlines, so a paragraph break
    // between two model calls is visible and a missing one shows as a run-together sentence.
    text: bot?.innerText ?? '',
    strong: bot?.querySelectorAll('strong').length ?? 0,
    li: bot?.querySelectorAll('li').length ?? 0,
    code: bot?.querySelectorAll('code').length ?? 0,
    paragraphs: bot?.querySelectorAll('p').length ?? 0,
    errors: [...document.querySelectorAll('#log .msg.err')].map(e => e.textContent),
    // Whether the last error bubble is inside the log's visible box, not below its fold.
    errorVisible: (() => {
      const errs = document.querySelectorAll('#log .msg.err');
      if (!errs.length) return false;
      const e = errs[errs.length - 1].getBoundingClientRect(), l = document.querySelector('#log').getBoundingClientRect();
      return e.top >= l.top && e.bottom <= l.bottom + 1;
    })(),
    sendDisabled: document.querySelector('#send').disabled,
    kv: Object.fromEntries([...document.querySelectorAll('#panel .kv')].map(r => [...r.children].map(c => c.textContent))),
    jaeger: document.querySelector('#panel a')?.href ?? null,
    scores: [...document.querySelectorAll('#panel .score')].map(s => s.textContent),
    bars: [...document.querySelectorAll('#panel .bar')].map(b => b.style.width),
  };
});

async function turn(name, message) {
  console.log(`\n== ${name}: ${message}`);
  await page.fill('#input', message);
  const t0 = Date.now();
  await page.click('#send');
  const seen = {};
  let last;
  for (;;) {
    last = await snapshot();
    const t = Date.now() - t0;
    if (last.retrieval && seen.retrieval === undefined) seen.retrieval = t;
    if (last.pills.length && seen.tool === undefined) seen.tool = t;
    if (last.text.trim() && seen.firstWord === undefined) seen.firstWord = t;
    if (last.usage && seen.usage === undefined) seen.usage = t;
    if (!last.sendDisabled && t > 500) break;
    if (t > 120_000) { check(false, 'turn finished within 120 s'); break; }
    await page.waitForTimeout(120);
  }
  for (const [k, v] of Object.entries(seen)) console.log(`${String(v).padStart(6)} ms  ${k}`);
  await page.screenshot({ path: `${shots}/${name}.png`, fullPage: true });
  return { seen, last };
}

await page.goto(base + '/');
check((await page.title()).length > 0, `page title: ${await page.title()}`);

if (process.env.SKIP_LIVE) console.log('SKIP_LIVE set: no model calls, only the refused request');
else {
// Turn 1: an order question. The tool must run, retrieval must show first, the usage card
// must count two model calls, and the answer must not run two model calls' text together.
const one = await turn('order', 'Where is my order ORD-10042, and is there a time limit on returns?');
check(one.last.retrieval?.includes('8 passages'), `retrieval card: ${one.last.retrieval}`);
check(one.last.pills.some(p => p.startsWith('lookup_order_status →')), `tool pills: ${JSON.stringify(one.last.pills)}`);
check(one.seen.retrieval < one.seen.firstWord, 'retrieval appeared before the first word');
// The model may narrate before it calls the tool ("I'll look up your order now."), in which
// case the first word is the first call's and precedes the pill. Either order is right; what
// must hold is that the pill is on screen before the turn's cost is.
console.log(one.seen.tool < one.seen.firstWord ? 'tool pill before the first word: the model went straight to the tool' : 'first word before the tool pill: the model narrated before calling the tool');
check(one.seen.tool < one.seen.usage, 'tool pill appeared before the usage card');
check(one.last.usage, 'usage card present');
check(Number(one.last.kv['model calls']) >= 2, `model calls: ${one.last.kv['model calls']}`);
check(one.last.text.includes('SP884213906SG') || /transit|途中|運送|运输/i.test(one.last.text), 'answer carries the order state or tracking number');
check(!/\*\*|^- |\n- /.test(one.last.text), 'no raw markdown markers in the rendered text');
check(!/[a-z][.!?][A-Z]/.test(one.last.text), 'no two sentences run together (the seam between two model calls is a paragraph break)');
check(one.last.paragraphs >= 2 || one.last.li > 0, `answer rendered as ${one.last.paragraphs} paragraphs, ${one.last.li} list items, ${one.last.strong} bold spans`);
check(one.last.bars.length === 8 && one.last.bars.includes('100%') && one.last.bars.some(b => b !== '100%'), `score bars relative: ${one.last.bars.join(' ')}`);
check(one.last.jaeger?.startsWith('http://localhost:16688/trace/'), `jaeger link: ${one.last.jaeger}`);
if (one.last.jaeger) {
  const id = one.last.jaeger.split('/').pop();
  const r = await fetch(`http://localhost:16688/api/traces/${id}`).catch(() => null);
  const spans = r?.ok ? (await r.json()).data?.[0]?.spans?.length ?? 0 : 0;
  check(spans > 0, `the linked trace exists in Jaeger with ${spans} spans`);
}
console.log('answer:', JSON.stringify(one.last.text.slice(0, 400)));

// Turn 2: Chinese, same conversation, nothing for a tool to do.
const two = await turn('chinese', '退货有时间限制吗？');
check(two.last.retrieval?.includes('8 passages'), `retrieval card: ${two.last.retrieval}`);
check(two.last.pills.length === 0, 'no tool pill on a retrieval-only turn');
check(/[一-鿿]/.test(two.last.text), 'answer contains Chinese');
check(!/\*\*|^- |\n- /.test(two.last.text), 'no raw markdown markers in the rendered text');
check(Number(two.last.kv['model calls']) === 1, `model calls: ${two.last.kv['model calls']}`);
check(two.last.kv['cost']?.startsWith('$'), `cost shown: ${two.last.kv['cost']}`);
console.log('answer:', JSON.stringify(two.last.text.slice(0, 400)));
}

// A request the server refuses before committing: the page must show the problem, not a
// blank bubble, and the send button must come back.
const limit = Number(process.env.MAX_MESSAGE ?? 4000) + 1;
await page.fill('#input', 'x'.repeat(limit));
await page.click('#send');
await page.waitForFunction(() => !document.querySelector('#send').disabled && document.querySelectorAll('#log .msg.err').length > 0, null, { timeout: 15_000 }).catch(() => {});
const three = await snapshot();
check(three.errors.length === 1, `refused request rendered as an error: ${JSON.stringify(three.errors)}`);
check(three.errorVisible, 'the refusal is inside the visible part of the log, not below the fold of a long message');
check(page.url().startsWith(base), 'still on the page');
await page.screenshot({ path: `${shots}/refused.png`, fullPage: true });
// The refused request is the one 400 this run provokes; Chrome also logs it to the console.
const provoked = problems.filter(p => (p.startsWith('http 400') && p.includes('/api/v1/chat/stream')) || (p.startsWith('console.error') && p.includes('status of 400')));
check(provoked.length === 2, `the provoked 400 was seen exactly once on the network and once in the console: ${JSON.stringify(provoked)}`);
for (const p of provoked) problems.splice(problems.indexOf(p), 1);

check(problems.length === 0, `console/network problems: ${JSON.stringify(problems)}`);
await browser.close();
console.log(failed ? `\n${failed} check(s) failed` : '\nall checks passed');
process.exit(failed ? 1 : 0);
