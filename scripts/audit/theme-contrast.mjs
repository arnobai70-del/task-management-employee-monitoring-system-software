// Usage: PW=$(npm root -g)/playwright AXE=<path to axe-core/axe.min.js> \
//   RT_ADMIN_EMAIL=... RT_ADMIN_PASSWORD=... \
//   node scripts/audit/theme-contrast.mjs http://localhost:5173 <label> <outDir> light dark
// Runs axe-core's color-contrast rule on /login and every admin route for each colour scheme,
// against a local API seeded with fictional data. Credentials come from the environment only.
import { createRequire } from 'module';
import { readFileSync } from 'fs';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PW);
const axeSource = readFileSync(process.env.AXE, 'utf8');
const [,, web, label, outDir, ...themes] = process.argv;
const ADMIN = { email: process.env.RT_ADMIN_EMAIL, password: process.env.RT_ADMIN_PASSWORD };
const routes = ['/dashboard','/productivity','/sla-analytics','/operations','/security-alerts','/employees','/departments','/shifts','/attendance','/projects','/tasks','/website-work','/follow-ups','/surveys','/access/rdp','/access/ip','/access/websites','/audit-logs'];
const browser = await chromium.launch();
const results = [];
for (const theme of themes) {
  const ctx = await browser.newContext({ viewport: { width: 1366, height: 900 }, colorScheme: theme });
  const page = await ctx.newPage();
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.goto(`${web}/login`);
  await page.waitForTimeout(500);
  const loginTheme = await page.evaluate(() => document.documentElement.dataset.theme ?? '(none)');
  const loginAxe = await page.evaluate(async src => { eval(src); const r = await axe.run(document, { runOnly: ['color-contrast'] }); return r.violations.reduce((n, v) => n + v.nodes.length, 0); }, axeSource);
  await page.screenshot({ path: `${outDir}/${label}-${theme}-login.png` });
  results.push(`${label} ${theme} /login data-theme=${loginTheme} contrastNodes=${loginAxe}`);
  await page.getByLabel(/email/i).fill(ADMIN.email);
  await page.getByLabel(/password/i).fill(ADMIN.password);
  await page.getByRole('button', { name: /sign in|log in/i }).click();
  await page.waitForURL(u => !u.pathname.startsWith('/login'));
  for (const route of routes) {
    await page.locator('nav a[href="' + route + '"]').first().click().catch(async () => { await page.goto(web + route); });
    await page.waitForSelector('h1');
    await page.waitForTimeout(1200);
    const v = await page.evaluate(async src => {
      if (!window.axe) eval(src);
      const r = await axe.run(document, { runOnly: ['color-contrast'] });
      return r.violations.flatMap(x => x.nodes.map(n => `${n.target.join(' ')} :: ${(n.any[0]?.message || '').replace(/\s+/g, ' ').slice(0, 140)}`));
    }, axeSource);
    const theme0 = await page.evaluate(() => document.documentElement.dataset.theme);
    results.push(`${label} ${theme} ${route} data-theme=${theme0} contrastNodes=${v.length}${v.length ? '\n    ' + v.slice(0, 6).join('\n    ') : ''}`);
    await page.screenshot({ path: `${outDir}/${label}-${theme}${route.replace(/\//g, '_')}.png` });
  }
  results.push(`${label} ${theme} pageErrors=${errors.length}`);
  await ctx.close();
}
await browser.close();
console.log(results.join('\n'));
