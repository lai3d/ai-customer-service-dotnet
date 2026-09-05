// Drives the operations UI in a real Chrome against the running Compose stack: a refused
// sign-in, every page, a full ticket cycle through the real buttons and forms, a flag raised
// and closed, the role bounce, the audit and staff pages. Records console errors and failed
// requests and screenshots each page. Run by hand; see docs/operations-admin.md.
//
//   ADMIN_UI=http://localhost:8083 SUPPORT_PASSWORD=... ADMIN_PASSWORD=... \
//   PLAYWRIGHT_MODULE=/path/to/node_modules/playwright/index.mjs CHROME=/path/to/chrome \
//   node scripts/drive.mjs
//
// Expects a support account `alice` and an admin `root` to exist, and at least one ticket
// raised by the assistant.
const { chromium } = await import(process.env.PLAYWRIGHT_MODULE ?? 'playwright');
const base = process.env.ADMIN_UI ?? 'http://localhost:8083';
const supportPassword = process.env.SUPPORT_PASSWORD ?? 'alice-demo-password-2026';
const adminPassword = process.env.ADMIN_PASSWORD ?? 'ops-demo-password-2026';
const browser = await chromium.launch({
  executablePath: process.env.CHROME ?? '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
  headless: true,
});
const page = await browser.newPage({ viewport: { width: 1440, height: 950 } });
const problems = [];
page.on('console', m => { if (m.type() === 'error' || m.type() === 'warning') problems.push(`console.${m.type()}: ${m.text()}`); });
page.on('pageerror', e => problems.push(`pageerror: ${e.message}`));
page.on('response', r => { if (r.status() >= 400 && !r.url().endsWith('/session')) problems.push(`http ${r.status()} ${r.request().method()} ${r.url()}`); });
const shot = async (name) => { await page.screenshot({ path: `${process.env.SHOTS ?? '.'}/${name}.png`, fullPage: true }); console.log('shot', name); };

async function login(user, pw) {
  await page.goto(base + '/');
  await page.getByLabel('Username').fill(user);
  await page.getByLabel('Password').fill(pw);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForSelector('header.top');
}

// A wrong password first: the page must say so, not sit there.
await page.goto(base + '/');
await page.getByLabel('Username').fill('alice');
await page.getByLabel('Password').fill('wrong-password-here');
await page.getByRole('button', { name: 'Sign in' }).click();
await page.waitForSelector('[role=alert]');
console.log('wrong password ->', (await page.locator('[role=alert]').textContent()).trim());
await shot('01-login-refused');

await login('alice', supportPassword);
console.log('signed in as', (await page.locator('header .who').textContent()).trim());
console.log('nav:', (await page.locator('header nav a').allTextContents()).join(' | '));
await shot('02-overview');

await page.getByRole('link', { name: 'Tickets' }).click();
await page.waitForURL('**/tickets');
await page.waitForSelector('main .empty, main table tbody tr');
console.log('tickets (open):', (await page.locator('main .empty, main table tbody tr').allTextContents()).join(' || ').replace(/\s+/g, ' ').slice(0, 120));
await page.getByLabel('State').selectOption('all');
await page.waitForSelector('main table tbody tr');
const row = page.locator('main table tbody tr').first();
console.log('tickets (all) first row:', (await row.allTextContents()).join(' ').replace(/\s+/g, ' ').slice(0, 160));
await shot('03-tickets');
await row.click();
await page.waitForSelector('ul.history li');
console.log('ticket detail history:', (await page.locator('ul.history li').allTextContents()).map(t => t.replace(/\s+/g, ' ').trim()).join(' | ').slice(0, 400));
console.log('actions offered:', (await page.locator('.actions button').allTextContents()).join(', '));
await shot('04-ticket');

// Drive a whole cycle through the real page, whatever state the ticket is in now: every
// action is a click, a form when the rule needs text, and a wait for the version to move.
const version = async () => Number((await page.locator('.kv').textContent()).match(/version (\d+)/)[1]);
const state = async () => (await page.locator('h2 .pill').textContent()).trim();
async function act(name, text) {
  const before = await version();
  await page.getByRole('button', { name, exact: true }).click();
  if (text !== undefined) {
    await page.locator('form.action textarea').fill(text);
    await page.locator('form.action button[type=submit]').click();
  }
  await page.waitForFunction(v => /version (\d+)/.exec(document.querySelector('.kv')?.textContent ?? '')?.[1] > v, before);
  console.log(`  ${name} -> ${await state()} v${await version()}`);
}
console.log('cycle from', await state());
if (await state() === 'open') await act('claim');
if (await state() === 'claimed') { await act('note', 'Called the customer back.'); await act('resolve', 'Refund re-issued; confirmation emailed.'); }
if (await state() === 'resolved') await act('close', '');
await act('reopen', 'Customer says the refund still has not arrived.');
console.log('  owner after reopen:', (await page.locator('.kv').textContent()).includes('unowned') ? 'unowned' : 'owned?');
await act('claim');
await shot('05-ticket-cycle');

await page.locator('.kv a.mono').click();
await page.waitForSelector('.transcript');
console.log('conversation: messages', await page.locator('.transcript .msg').count(), '| turns', await page.locator('.turn').count(), '| bold rendered:', await page.locator('.msg.assistant strong').count() > 0, '| literal asterisks:', (await page.locator('.msg.assistant').allTextContents()).join('').includes('**'));
await page.locator('.turn details').first().locator('summary').click();
// Flag the answer through the page: issue, note, submit; the flag then shows on the turn.
const flagsBefore = await page.locator('.turn .note .pill').count();
await page.getByRole('button', { name: 'flag this answer' }).click();
await page.getByLabel('Issue').selectOption('incorrect');
await page.locator('form.action textarea').fill('Said "return in progress" as if it were a status the customer set.');
await page.locator('form.action button[type=submit]').click();
await page.waitForFunction(n => document.querySelectorAll('.turn .note .pill').length > n, flagsBefore);
console.log('flag raised through the page: flags now', await page.locator('.turn .note .pill').count());
await shot('06-conversation');

await page.getByRole('link', { name: 'Feedback' }).click();
await page.getByRole('heading', { name: 'Answer feedback' }).waitFor();
await page.waitForSelector('main table tbody tr, main .empty');
console.log('feedback rows:', await page.locator('main table tbody tr').count());
await page.getByRole('button', { name: 'close', exact: true }).first().click();
await page.locator('form.action textarea').fill('FAQ answer about the return window will be revised.');
await page.locator('form.action button[type=submit]').click();
await page.waitForFunction(() => document.querySelector('main .empty') !== null);
console.log('feedback after close (open filter):', (await page.locator('main .empty').textContent()).trim());
await shot('07-feedback');

// Support must not see Staff or Audit at all; typing the URL bounces to the overview.
await page.goto(base + '/audit');
await page.waitForSelector('main h2');
console.log('support at /audit sees:', (await page.locator('main h2').textContent()).trim());

await page.getByRole('button', { name: 'Sign out' }).click();
await page.waitForSelector('form.login');
await login('root', adminPassword);
await page.getByRole('link', { name: 'Audit' }).click();
await page.getByRole('heading', { name: 'Audit' }).waitFor();
await page.waitForSelector('main table tbody tr');
console.log('audit rows on page:', await page.locator('main table tbody tr').count());
console.log('audit first rows:', (await page.locator('main table tbody tr').allTextContents()).slice(0, 4).map(t => t.replace(/\s+/g, ' ').trim()).join(' || '));
await shot('08-audit');
await page.getByRole('link', { name: 'Staff' }).click();
await page.getByRole('heading', { name: 'Staff accounts' }).waitFor();
await page.waitForSelector('main table tbody tr');
console.log('staff rows:', (await page.locator('main table tbody tr').allTextContents()).map(t => t.replace(/\s+/g, ' ').trim()).join(' || '));
await shot('09-staff');

console.log('\nproblems:', problems.length === 0 ? 'none' : '\n  ' + problems.join('\n  '));
await browser.close();
