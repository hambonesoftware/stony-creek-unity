// Smoke test against the published site: loads, starts, walks, opens the map and sets a waypoint, no errors.
// Usage: STONY_GPU=1 node test/live.mjs [url]
import { launchBrowser } from './browser.mjs';
const url = process.argv[2] || 'https://hambonesoftware.github.io/stony-creek-unity/';
const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
const errors = [];
page.on('pageerror', e => errors.push(e.message));
page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
await page.goto(url);
await page.waitForFunction(() => window.stonyState && window.stonyState.ready, null, { timeout: 300000 });
const frames = async n => { const f = await page.evaluate(() => window.stonyState.frame); await page.waitForFunction(t => window.stonyState.frame >= t, f + n, { timeout: 120000 }); };
await page.mouse.click(640, 400);
await page.waitForFunction(() => window.stonyState.started, null, { timeout: 30000 });
const a = await page.evaluate(() => window.stonyState);
await page.keyboard.down('KeyW'); await frames(30); await page.keyboard.up('KeyW'); await frames(2);
const b = await page.evaluate(() => window.stonyState);
await page.keyboard.down('KeyM'); await frames(2); await page.keyboard.up('KeyM');
await page.waitForFunction(() => window.stonyState.mapOpen, null, { timeout: 30000 });
await page.mouse.click(800, 300);
await page.waitForFunction(() => window.stonyState.waypoint && window.stonyState.route > 0, null, { timeout: 30000 });
const s = await page.evaluate(() => window.stonyState);
console.log(JSON.stringify({ url, walked: +Math.hypot(b.x - a.x, b.z - a.z).toFixed(1), route: s.route, trees: s.trees, fps: s.fps, errors }));
await browser.close();
process.exit(errors.length || Math.hypot(b.x - a.x, b.z - a.z) < 0.5 ? 1 : 0);
