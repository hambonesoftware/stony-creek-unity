// Frame-rate probe: loads the build, starts walking, samples fps at a few places.  STONY_GPU=1 node test/perf.mjs [query]
import { launchBrowser } from './browser.mjs';
import { serve } from './serve.mjs';
const srv = await serve();
const browser = await launchBrowser();
const page = await browser.newPage({ viewport: { width: 1280, height: 720 } });
await page.goto(`http://127.0.0.1:${srv.address().port}/${process.argv[2] || ''}`);
await page.waitForFunction(() => window.stonyState && window.stonyState.ready, null, { timeout: 240000 });
const gpu = await page.evaluate(() => { const gl = document.createElement('canvas').getContext('webgl2'); const e = gl.getExtension('WEBGL_debug_renderer_info'); return e ? gl.getParameter(e.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER); });
console.log('renderer:', gpu);
await page.evaluate(() => window.unityInstance.SendMessage('Explorer', 'Command', 'start'));
for (const place of [0, 3, 1]) {
  await page.evaluate(p => window.unityInstance.SendMessage('Explorer', 'Command', 'travel ' + p), place);
  for (let k = 0; k < 5; k++) {
    await page.waitForTimeout(2500);
    const s = await page.evaluate(() => window.stonyState);
    console.log(`place ${place} t${k}: x ${s.x} z ${s.z}  fps ${s.fps}  near ${s.near}  far ${s.far}  grass ${s.grass}`);
  }
}
await page.screenshot({ path: 'test-results/perf.png' });
await browser.close(); srv.close();
