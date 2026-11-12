// Browser trial for the Unity web build: loads the park, walks, turns, checks the compass maths,
// uses the radar and full map (waypoint + GPS route), fast travel, and the same on a touch phone.
// Usage: node test/trial.mjs   (after building Builds/Web)
import fs from 'node:fs';
import { launchBrowser } from './browser.mjs';
import { serve } from './serve.mjs';

const OUT = 'test-results';
fs.mkdirSync(OUT, { recursive: true });
const results = [];
let failed = 0;
async function check(name, fn) {
  const t = Date.now();
  try { await fn(); results.push({ name, ok: true }); console.log(`  PASS  ${name}  (${((Date.now() - t) / 1000).toFixed(1)}s)`); }
  catch (e) { failed++; results.push({ name, ok: false, error: e.message }); console.log(`  FAIL  ${name}\n        ${e.message.split('\n')[0]}`); }
}
const assert = (c, m) => { if (!c) throw new Error(m); };
const state = page => page.evaluate(() => window.stonyState);
const cmd = (page, c) => page.evaluate(c => window.unityInstance.SendMessage('Explorer', 'Command', c), c);
async function until(page, fn, arg, ms, what) {
  try { await page.waitForFunction(fn, arg, { timeout: ms, polling: 100 }); }
  catch { throw new Error(`timed out waiting for ${what}; state=${JSON.stringify(await state(page))}`); }
}
// Wait for n rendered frames: timings must not depend on how fast the machine renders.
async function frames(page, n) {
  const f0 = (await state(page)).frame;
  await page.waitForFunction(t => window.stonyState.frame >= t, f0 + n, { timeout: 120000, polling: 50 });
}
async function holdKeys(page, keys, n) {
  for (const k of keys) await page.keyboard.down(k);
  await frames(page, n);
  for (const k of [...keys].reverse()) await page.keyboard.up(k);
  await frames(page, 2);
}
async function mouseDrag(page, x, y, dx, dy, steps) {
  await page.mouse.move(x, y); await page.mouse.down(); await frames(page, 1);
  for (let i = 1; i <= steps; i++) { await page.mouse.move(x + dx * i / steps, y + dy * i / steps); await frames(page, 1); }
  await page.mouse.up(); await frames(page, 2);
}
const QUERY = process.env.STONY_GPU ? '' : '?q=0';
// GUI pixels (top-left origin, canvas pixels) to CSS pixels
const css = (page, x, y) => page.evaluate(([x, y]) => { const c = document.getElementById('unity-canvas'); const k = c.clientWidth / c.width; return { x: x * k, y: y * k }; }, [x, y]);

async function open(browser, opts) {
  const ctx = await browser.newContext(opts);
  const page = await ctx.newPage();
  page.errors = [];
  page.on('pageerror', e => page.errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error' && !/favicon/.test(m.text())) page.errors.push(m.text()); });
  return { ctx, page };
}

const srv = await serve();
const base = `http://127.0.0.1:${srv.address().port}/`;
const browser = await launchBrowser();
const started = Date.now();
console.log('Stony Creek Unity trial');

// ---------------- desktop ----------------
{
  const { ctx, page } = await open(browser, { viewport: { width: 1280, height: 720 } });
  await check('desktop: the Unity build loads and reports ready', async () => {
    await page.goto(base + QUERY);
    await until(page, () => window.stonyState && window.stonyState.ready, null, 240000, 'ready');
    await frames(page, 5);
    await page.screenshot({ path: `${OUT}/01-start.png`, timeout: 120000 });
  });
  await check('desktop: click starts the walk', async () => {
    await page.mouse.click(640, 400);
    await until(page, () => window.stonyState.started, null, 30000, 'started');
  });
  await check('desktop: trees are placed and drawn near and far', async () => {
    await frames(page, 20);
    const s = await state(page);
    assert(s.trees > 200000, `only ${s.trees} trees loaded`);
    assert(s.far > (QUERY ? 300 : 1000), `only ${s.far} distant trees drawn`);
    assert(s.grass > 100, `only ${s.grass} grass clumps drawn`);
    await page.screenshot({ path: `${OUT}/02-view.png`, timeout: 120000 });
  });
  await check('desktop: W walks forward along the camera direction', async () => {
    const a = await state(page);
    await holdKeys(page, ['KeyW'], 30);
    const b = await state(page);
    const dx = b.x - a.x, dz = b.z - a.z, d = Math.hypot(dx, dz);
    assert(d > 0.6, `moved only ${d.toFixed(2)} m`);
    const along = (dx * a.fwdX + dz * a.fwdZ) / (d * Math.hypot(a.fwdX, a.fwdZ));
    assert(along > 0.9, `moved off the view direction (cos ${along.toFixed(2)})`);
  });
  await check('desktop: mouse drag turns right, and W still walks where you look', async () => {
    const a = await state(page);
    await mouseDrag(page, 640, 400, 300, 0, 10);
    const b = await state(page);
    const turned = ((b.yaw - a.yaw) + 540) % 360 - 180;
    assert(turned > 30, `turned ${turned.toFixed(1)}°, expected a clockwise turn`);
    // the compass bearing must match the camera: forward = (sin yaw, cos yaw) in (east, north)
    const yr = b.yaw * Math.PI / 180;
    assert(Math.abs(Math.sin(yr) - b.fwdX) < 0.05 && Math.abs(Math.cos(yr) - b.fwdZ) < 0.05, `compass ${b.yaw}° disagrees with camera (${b.fwdX}, ${b.fwdZ})`);
    await holdKeys(page, ['KeyW'], 30);
    const c = await state(page);
    const dx = c.x - b.x, dz = c.z - b.z, d = Math.hypot(dx, dz);
    assert(d > 0.6 && (dx * b.fwdX + dz * b.fwdZ) / d > 0.9, 'after turning, W did not walk toward the view');
  });
  await check('desktop: A/D strafe and S backs up', async () => {
    const a = await state(page);
    await holdKeys(page, ['KeyD'], 25);
    const b = await state(page);
    const rx = a.fwdZ, rz = -a.fwdX; // right of forward
    const d = Math.hypot(b.x - a.x, b.z - a.z);
    assert(d > 0.5 && ((b.x - a.x) * rx + (b.z - a.z) * rz) / d > 0.85, 'D did not strafe right');
    await holdKeys(page, ['KeyS'], 25);
    const c = await state(page);
    const e = Math.hypot(c.x - b.x, c.z - b.z);
    assert(e > 0.5 && ((c.x - b.x) * b.fwdX + (c.z - b.z) * b.fwdZ) / e < -0.85, 'S did not walk backwards');
  });
  await check('desktop: Shift jogs faster than walking', async () => {
    const a = await state(page);
    await holdKeys(page, ['KeyW'], 20);
    const b = await state(page);
    await holdKeys(page, ['ShiftLeft', 'KeyW'], 20);
    const c = await state(page);
    const walk = Math.hypot(b.x - a.x, b.z - a.z), jog = Math.hypot(c.x - b.x, c.z - b.z);
    assert(jog > walk * 1.8, `jog ${jog.toFixed(2)} m vs walk ${walk.toFixed(2)} m over the same frames`);
  });
  await check('desktop: radar minimap and compass render', async () => {
    const s = await state(page);
    const c = await css(page, s.mini[0], s.mini[1]);
    const r = (await css(page, s.mini[2], 0)).x / 2;
    await page.screenshot({ path: `${OUT}/03-radar.png`, clip: { x: Math.max(0, c.x - r - 10), y: Math.max(0, c.y - r - 10), width: r * 2 + 320, height: r * 2 + 70 } });
  });
  await check('desktop: M opens the map, a click sets a waypoint with a GPS route, Esc closes', async () => {
    await holdKeys(page, ['KeyM'], 2);
    await until(page, () => window.stonyState.mapOpen, null, 30000, 'map open');
    await frames(page, 2);
    await page.mouse.click(790, 300);
    await until(page, () => window.stonyState.waypoint && window.stonyState.route > 50, null, 30000, 'waypoint + route');
    await frames(page, 3);
    await page.screenshot({ path: `${OUT}/04-map.png`, timeout: 120000 });
    await holdKeys(page, ['Escape'], 2);
    await until(page, () => !window.stonyState.mapOpen, null, 30000, 'map closed');
    await frames(page, 6);
    await page.screenshot({ path: `${OUT}/05-route-radar.png`, timeout: 120000 });
  });
  await check('desktop: map zooms with the wheel and pans with a drag', async () => {
    await holdKeys(page, ['KeyM'], 2);
    await until(page, () => window.stonyState.mapOpen, null, 30000, 'map open');
    await page.mouse.move(640, 360);
    await page.mouse.wheel(0, -500);
    await frames(page, 3);
    await mouseDrag(page, 640, 360, -200, 100, 8);
    const s = await state(page);
    assert(s.mapOpen && s.waypoint, 'dragging the map should not clear the waypoint or close the map');
    await page.screenshot({ path: `${OUT}/06-map-zoomed.png`, timeout: 120000 });
  });
  await check('desktop: map buttons work (Clear waypoint, Close)', async () => {
    let s = await state(page);
    const clear = s.buttons.find(b => b.label === 'Clear waypoint');
    assert(clear, 'no Clear waypoint button: ' + JSON.stringify(s.buttons));
    let p = await css(page, clear.x, clear.y);
    await page.mouse.click(p.x, p.y);
    await until(page, () => !window.stonyState.waypoint, null, 30000, 'waypoint cleared');
    s = await state(page);
    const close = s.buttons.find(b => b.label === 'Close');
    p = await css(page, close.x, close.y);
    await page.mouse.click(p.x, p.y);
    await until(page, () => !window.stonyState.mapOpen, null, 30000, 'map closed by button');
  });
  await check('desktop: MAP and WALK/JOG buttons', async () => {
    let s = await state(page);
    let p = await css(page, s.jogBtn[0], s.jogBtn[1]);
    await page.mouse.click(p.x, p.y);
    await until(page, () => window.stonyState.jog, null, 30000, 'jog on');
    await page.mouse.click(p.x, p.y);
    await until(page, () => !window.stonyState.jog, null, 30000, 'jog off');
    p = await css(page, s.mapBtn[0], s.mapBtn[1]);
    await page.mouse.click(p.x, p.y);
    await until(page, () => window.stonyState.mapOpen, null, 30000, 'map by button');
    await holdKeys(page, ['Escape'], 2);
  });
  await check('desktop: fast travel to each named place', async () => {
    for (let i = 0; i < 5; i++) {
      const a = await state(page);
      await cmd(page, `travel ${i}`);
      await frames(page, 2);
      const b = await state(page);
      if (i > 0) assert(Math.hypot(b.x - a.x, b.z - a.z) > 100, `travel ${i} did not move`);
    }
    await cmd(page, 'travel 1');
    await frames(page, 40);
    await page.screenshot({ path: `${OUT}/07-eastwood.png`, timeout: 120000 });
    await cmd(page, 'travel 3');
    await frames(page, 40);
    await page.screenshot({ path: `${OUT}/08-shelden.png`, timeout: 120000 });
  });
  await check('desktop: water blocks walking into a lake', async () => {
    // stand beside Stony Creek Lake facing the water and jog straight at it
    await cmd(page, 'shore');
    await frames(page, 3);
    const a = await state(page);
    await holdKeys(page, ['ShiftLeft', 'KeyW'], 80);
    const s = await state(page);
    assert(s.y > 0, 'walker fell below the terrain');
    assert(!s.wet, 'walked into the lake');
    assert(Math.hypot(s.x - a.x, s.z - a.z) < 40, `slid ${Math.hypot(s.x - a.x, s.z - a.z).toFixed(0)} m along the shore`);
  });
  await check('desktop: mapped beaches are sand and mapped parking lots are paved', async () => {
    for (const i of [0, 1]) {
      await cmd(page, `goto beach ${i}`); await frames(page, 3);
      const s = await state(page);
      assert(s.feature === 2 && !s.wet, `beach ${i}: ground type ${s.feature}, wet ${s.wet}`);
      if (i === 0) { await frames(page, 30); await page.screenshot({ path: `${OUT}/09-beach.png`, timeout: 120000 }); }
    }
    for (const i of [0, 5, 20]) {
      await cmd(page, `goto parking ${i}`); await frames(page, 3);
      const s = await state(page);
      assert(s.feature === 3, `parking ${i}: ground type ${s.feature}`);
      if (i === 5) { await frames(page, 30); await page.screenshot({ path: `${OUT}/10-parking.png`, timeout: 120000 }); }
    }
  });
  await check('desktop: buildings stand on their footprints and block walking', async () => {
    for (const i of [0, 3]) {
      await cmd(page, `goto building ${i}`); await frames(page, 3);
      const a = await state(page);
      if (i === 0) { await frames(page, 30); await page.screenshot({ path: `${OUT}/11-building.png`, timeout: 120000 }); }
      await holdKeys(page, ['ShiftLeft', 'KeyW'], 70);
      const s = await state(page);
      assert(!s.inBuilding, `walked into building ${i}`);
      assert(Math.hypot(s.x - a.x, s.z - a.z) > 1, `did not move toward building ${i}`);
    }
  });
  await check('props: picnic tables, benches, playground, canoes, grills and parked cars are placed', async () => {
    const s = await state(page);
    for (const [kind, i, shot] of [['car', 0, '12-cars'], ['picnic', 0, '13-picnic'], ['bench', 2, '14-bench'], ['slide', 0, '15-playground'], ['grill', 0, null], ['canoe', 0, '16-canoes']]) {
      await cmd(page, `prop ${kind} ${i} 9 20`); await frames(page, 25);
      if (shot) await page.screenshot({ path: `${OUT}/${shot}.png`, timeout: 120000 });
    }
    assert(s.props > 400, `only ${s.props} props placed`);
  });
  await check('props: parked cars sit in the lots and the camera really sees them', async () => {
    await cmd(page, 'prop car 0 6 20'); await frames(page, 40);
    const s = await state(page);
    assert(s.propsNear >= 3, `only ${s.propsNear} props visible beside a row of parked cars`);
  });
  await check('buildings: the lodge, brick bathhouses and shelters stand on their footprints', async () => {
    // the park office is shot from its front (west-south-west, toward the path) and from behind, to check the roof falls away from the front
    for (const [style, idx, bearing, shot] of [[1, 0, '', '17-nature-center'], [2, 0, '', '18-bathhouse'], [2, 3, '', null], [3, 0, '', '19-shelter'], [4, 0, ' 255', '20-park-office-front'], [4, 0, ' 75', '21-park-office-rear']]) {
      await cmd(page, `goto style ${style} ${idx}${bearing}`); await frames(page, 30);
      const s = await state(page);
      assert(!s.inBuilding && !s.wet, `style ${style} #${idx}: bad spot ${JSON.stringify([s.x, s.z])}`);
      if (shot) await page.screenshot({ path: `${OUT}/${shot}.png`, timeout: 120000 });
    }
  });
  await check('desktop: no console errors', async () => {
    assert(page.errors.length === 0, page.errors.slice(0, 3).join(' | '));
  });
  const s = await state(page);
  console.log(`  info  desktop fps ${s.fps} (${process.env.STONY_GPU ? 'GPU' : 'software rendering, light quality'}), trees near ${s.near} far ${s.far} out to ${s.farDistance} m`);
  await ctx.close();
}

// ---------------- phone ----------------
const UA = 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Mobile Safari/537.36';
for (const [w, h, label] of [[844, 390, 'landscape'], [390, 844, 'portrait']]) {
  const { ctx, page } = await open(browser, { viewport: { width: w, height: h }, deviceScaleFactor: 2, isMobile: true, hasTouch: true, userAgent: UA });
  const cdp = await ctx.newCDPSession(page);
  const touch = async (type, pts) => cdp.send('Input.dispatchTouchEvent', { type, touchPoints: pts });
  const tap = async (x, y) => { await touch('touchStart', [{ x, y, id: 1 }]); await page.waitForTimeout(40); await touch('touchEnd', []); await frames(page, 2); };
  const drag = async (x, y, dx, dy, steps = 12, id = 1, holdFrames = 0) => {
    await touch('touchStart', [{ x, y, id }]); await frames(page, 1);
    for (let i = 1; i <= steps; i++) { await touch('touchMove', [{ x: x + dx * i / steps, y: y + dy * i / steps, id }]); await frames(page, 1); }
    if (holdFrames) await frames(page, holdFrames);
    await touch('touchEnd', []); await frames(page, 2);
  };
  await check(`phone ${label}: loads in touch mode`, async () => {
    await page.goto(base + QUERY);
    await until(page, () => window.stonyState && window.stonyState.ready, null, 240000, 'ready');
    const s = await state(page);
    assert(s.touch, 'not in touch mode');
  });
  await check(`phone ${label}: tap starts`, async () => {
    await tap(w / 2, h / 2);
    await until(page, () => window.stonyState.started, null, 30000, 'started');
    await frames(page, 10);
  });
  await check(`phone ${label}: left thumbstick walks forward`, async () => {
    const a = await state(page);
    await drag(w * 0.2, h * 0.75, 0, -60, 6, 1, 30);
    const b = await state(page);
    const dx = b.x - a.x, dz = b.z - a.z, d = Math.hypot(dx, dz);
    assert(d > 0.6, `thumbstick moved only ${d.toFixed(2)} m`);
    assert((dx * a.fwdX + dz * a.fwdZ) / d > 0.85, 'thumbstick did not walk forward');
  });
  await check(`phone ${label}: drag on the right turns the view`, async () => {
    const a = await state(page);
    await drag(w * 0.7, h * 0.5, 120, 0, 8);
    const b = await state(page);
    const turned = ((b.yaw - a.yaw) + 540) % 360 - 180;
    assert(turned > 15, `turned ${turned.toFixed(1)}°`);
  });
  await check(`phone ${label}: walk and look at the same time (two fingers)`, async () => {
    const a = await state(page);
    await touch('touchStart', [{ x: w * 0.2, y: h * 0.75, id: 1 }]); await frames(page, 1);
    await touch('touchMove', [{ x: w * 0.2, y: h * 0.75 - 60, id: 1 }]); await frames(page, 1);
    await touch('touchStart', [{ x: w * 0.2, y: h * 0.75 - 60, id: 1 }, { x: w * 0.7, y: h * 0.5, id: 2 }]); await frames(page, 1);
    for (let i = 1; i <= 10; i++) { await touch('touchMove', [{ x: w * 0.2, y: h * 0.75 - 60, id: 1 }, { x: w * 0.7 + i * 10, y: h * 0.5, id: 2 }]); await frames(page, 1); }
    await frames(page, 15);
    await touch('touchEnd', []); await frames(page, 2);
    const b = await state(page);
    const turned = ((b.yaw - a.yaw) + 540) % 360 - 180;
    assert(Math.hypot(b.x - a.x, b.z - a.z) > 0.6 && turned > 10, `moved ${Math.hypot(b.x - a.x, b.z - a.z).toFixed(2)} m, turned ${turned.toFixed(1)}°`);
  });
  await check(`phone ${label}: JOG button toggles`, async () => {
    const s = await state(page);
    const p = await css(page, s.jogBtn[0], s.jogBtn[1]);
    await tap(p.x, p.y);
    await until(page, () => window.stonyState.jog, null, 30000, 'jog on');
    await tap(p.x, p.y);
    await until(page, () => !window.stonyState.jog, null, 30000, 'jog off');
  });
  await check(`phone ${label}: tapping the radar opens the map`, async () => {
    const s = await state(page);
    const p = await css(page, s.mini[0], s.mini[1]);
    await page.screenshot({ path: `${OUT}/phone-${label}-walk.png`, timeout: 120000 });
    await tap(p.x, p.y);
    await until(page, () => window.stonyState.mapOpen, null, 30000, 'map open');
  });
  await check(`phone ${label}: tap the map sets a waypoint, pinch zooms, Close button closes`, async () => {
    await tap(w * 0.55, h * 0.4);
    await until(page, () => window.stonyState.waypoint, null, 30000, 'waypoint');
    // pinch out
    await touch('touchStart', [{ x: w / 2 - 20, y: h / 2, id: 1 }, { x: w / 2 + 20, y: h / 2, id: 2 }]);
    await frames(page, 1);
    for (let i = 1; i <= 8; i++) { await touch('touchMove', [{ x: w / 2 - 20 - i * 12, y: h / 2, id: 1 }, { x: w / 2 + 20 + i * 12, y: h / 2, id: 2 }]); await frames(page, 1); }
    await touch('touchEnd', []); await frames(page, 3);
    let s = await state(page);
    assert(s.waypoint, 'pinch cleared the waypoint');
    await page.screenshot({ path: `${OUT}/phone-${label}-map.png`, timeout: 120000 });
    const close = s.buttons.find(b => b.label === 'Close');
    const p = await css(page, close.x, close.y);
    await tap(p.x, p.y);
    await until(page, () => !window.stonyState.mapOpen, null, 30000, 'closed');
  });
  await check(`phone ${label}: page does not scroll or zoom`, async () => {
    const r = await page.evaluate(() => ({ sx: scrollX, sy: scrollY, z: visualViewport ? visualViewport.scale : 1 }));
    assert(r.sx === 0 && r.sy === 0 && Math.abs(r.z - 1) < 0.01, JSON.stringify(r));
  });
  await check(`phone ${label}: no console errors`, async () => {
    assert(page.errors.length === 0, page.errors.slice(0, 3).join(' | '));
  });
  await ctx.close();
}

await browser.close();
srv.close();
fs.writeFileSync(`${OUT}/checks.json`, JSON.stringify(results, null, 1));
console.log(`${results.length - failed}/${results.length} checks passed in ${Math.round((Date.now() - started) / 1000)}s. Screenshots in ${OUT}/.`);
process.exit(failed ? 1 : 0);
