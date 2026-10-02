const { test: base, expect, chromium } = require('@playwright/test');
const fs = require('node:fs/promises');
const path = require('node:path');
const http = require('node:http');

const html = `<!doctype html><meta charset="utf-8"><title>AirBridge video QA</title>
<style>body{font:16px sans-serif;background:#eee}video{width:320px;height:180px}#source{display:none}</style>
<h1>Generated local video</h1><div id="player"><video id="video" muted playsinline style="opacity:0.75" aria-hidden="false"></video></div>
<canvas id="source" width="160" height="90"></canvas>
<script>
const source = document.querySelector('#source'), paint = source.getContext('2d');
window.sourceStarted = performance.now();
function draw() {
  const frame = Math.floor((performance.now()-sourceStarted)/10);
  paint.fillStyle = 'rgb('+frame%256+','+Math.floor(frame/256)%256+',80)';
  paint.fillRect(0,0,160,90);
  // High-contrast timestamp bits survive the video's RGB/YUV conversion.
  for(let bit=0;bit<16;bit++) { paint.fillStyle=(frame & (1<<bit))?'white':'black'; paint.fillRect(bit*10,0,10,20); }
  requestAnimationFrame(draw);
}
draw();
window.attachSource = async video => { video.muted=true; video.srcObject=source.captureStream(30); await video.play(); };
attachSource(document.querySelector('#video'));
</script>`;

const test = base.extend({
  session: async ({}, use, testInfo) => {
    // Chromium appends long storage paths (including extension IDs). Keep its
    // owned runtime short even when the verification evidence path is nested.
    const runtimeRoot = path.resolve(__dirname, '../../artifacts/browser-runtime');
    await fs.mkdir(runtimeRoot, { recursive: true });
    const runtime = await fs.mkdtemp(path.join(runtimeRoot, 'session-'));
    const extension = path.join(runtime, 'extension');
    const profile = path.join(runtime, 'profile');
    await fs.writeFile(testInfo.outputPath('runtime.json'), JSON.stringify({ runtime, extension, profile }, null, 2));
    await fs.cp(path.resolve(__dirname, '../../src/AirBridge.BrowserExtension'), extension, { recursive: true });
    // Only the QA copy gains a worker, to discover its generated ID and read actual chrome.storage.
    const manifestPath = path.join(extension, 'manifest.json');
    const manifest = JSON.parse(await fs.readFile(manifestPath, 'utf8'));
    manifest.background = { service_worker: 'qa-worker.js' };
    await fs.writeFile(manifestPath, JSON.stringify(manifest));
    await fs.writeFile(path.join(extension, 'qa-worker.js'), 'chrome.runtime.onInstalled.addListener(() => {});');
    // Headless Chromium's action.openPopup does not expose a Playwright page target.
    // This QA-only initialization seam supplies only the actual target tab selection;
    // popup.js, storage, content scripts and media/canvas APIs remain production code.
    await fs.writeFile(path.join(extension, 'qa-popup-tab.js'), `
      const qaTarget = new URLSearchParams(location.search);
      const realQuery = chrome.tabs.query.bind(chrome.tabs);
      chrome.tabs.query = async query => {
        const result = await realQuery(query);
        if (query.active === true && query.currentWindow === true)
          return [{ ...(result[0] || {}), id: Number(qaTarget.get('qaTabId')), url: qaTarget.get('qaUrl') }];
        return result;
      };
    `);
    const popupHtmlPath = path.join(extension, 'popup.html');
    const popupHtml = await fs.readFile(popupHtmlPath, 'utf8');
    const popupScript = '<script src="popup.js"></script>';
    expect(popupHtml).toContain(popupScript);
    await fs.writeFile(popupHtmlPath, popupHtml.replace(popupScript, '<script src="qa-popup-tab.js"></script><script src="popup.js"></script>'));
    const server = http.createServer((request, response) => {
      response.writeHead(200, { 'Content-Type': 'text/html' }); response.end(html);
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const origin = `http://127.0.0.1:${server.address().port}`;
    let context, video, popup;
    try {
      context = await chromium.launchPersistentContext(profile, {
        channel: 'chromium', headless: true,
        args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`],
      });
      await context.tracing.start({ screenshots: true, snapshots: true });
      const worker = context.serviceWorkers()[0] || await context.waitForEvent('serviceworker');
      const extensionId = worker.url().split('/')[2];
      video = await context.newPage();
      await video.goto(origin);
      await expect.poll(() => video.locator('video').evaluate(element => element.readyState)).toBeGreaterThanOrEqual(2);
      await video.bringToFront();
      const [targetTab] = await worker.evaluate(() => chrome.tabs.query({ active: true, currentWindow: true }));
      expect(targetTab.id).toBeGreaterThan(0);
      popup = await context.newPage();
      await popup.goto(`chrome-extension://${extensionId}/popup.html?qaTabId=${targetTab.id}&qaUrl=${encodeURIComponent(video.url())}`);
      await expect(popup.locator('#site')).toHaveText(origin);
      await testInfo.attach('coverage', { contentType: 'application/json', body: Buffer.from(JSON.stringify({
        actual: ['production popup.js', 'chrome.storage.sync', 'production content scripts', 'canvas.captureStream', 'video frame capture', 'delayed canvas output'],
        seams: ['QA-only service worker for extension ID/storage observation', 'QA-only chrome.tabs.query active-tab selection'],
        notCovered: ['toolbar action invocation', 'activeTab permission grant', 'Firefox runtime'], targetTabId: targetTab.id,
      }, null, 2)) });
      await use({ context, worker, video, popup, origin });
    } finally {
      if (context) {
        await video?.screenshot({ path: testInfo.outputPath('video.png') }).catch(() => {});
        await popup?.screenshot({ path: testInfo.outputPath('popup.png') }).catch(() => {});
        await context.tracing.stop({ path: testInfo.outputPath('trace.zip') }).catch(() => {});
        await context.close();
      }
      await new Promise(resolve => server.close(resolve));
      // Preserve failed runtimes for storage/debug inspection. Never remove a
      // computed path without checking it is the exact mkdtemp child we own.
      if (testInfo.status === testInfo.expectedStatus) {
        if (path.dirname(runtime) !== runtimeRoot || !path.basename(runtime).startsWith('session-'))
          throw new Error('Browser runtime cleanup target escaped its owned directory.');
        await fs.rm(runtime, { recursive: true, force: true });
      }
    }
  },
});

async function renderedFrame(page) {
  return page.evaluate(() => {
    const canvas = document.querySelector('canvas[data-airbridge-picture-delay]');
    if (!canvas || canvas.style.display !== 'block' || canvas.width !== 160 || canvas.height !== 90) return null;
    const context = canvas.getContext('2d');
    const sentinel = context.getImageData(155, 85, 1, 1).data;
    if (sentinel[2] < 65 || sentinel[2] > 95) return null;
    let frame = 0;
    for (let bit = 0; bit < 16; bit++) {
      if (context.getImageData(bit * 10 + 5, 10, 1, 1).data[0] > 127) frame |= 1 << bit;
    }
    return { frame, age: (performance.now() - sourceStarted) - frame * 10 };
  });
}

async function expectRenderedAge(page, minimum, maximum) {
  await expect.poll(async () => {
    const rendered = await renderedFrame(page);
    return rendered !== null && rendered.age > minimum && rendered.age < maximum;
  }).toBe(true);
}

async function configure(session, enabled, delayMs) {
  await session.popup.locator('#enabled').setChecked(enabled);
  await session.popup.locator('#delay').fill(String(delayMs));
  await session.popup.locator('#save').click();
  await expect(session.popup.locator('#status')).toHaveText(enabled ? `Delaying picture by ${Math.min(delayMs, 4000)} ms` : 'Disabled for this site');
}

test('popup persists settings and delays real captured pictures without pausing media', async ({ session }) => {
  const before = await session.video.locator('video').evaluate(video => video.currentTime);
  await configure(session, true, 300);
  await expect(session.video.locator('canvas[data-airbridge-picture-delay]')).toBeVisible();
  await expect(session.video.locator('video')).toHaveCSS('opacity', '0');
  const stored = await session.worker.evaluate(() => chrome.storage.sync.get('siteSettings'));
  expect(stored.siteSettings[session.origin]).toEqual({ enabled: true, delayMs: 300 });
  await expect.poll(() => session.video.locator('video').evaluate(video => video.currentTime)).toBeGreaterThan(before + 0.2);
  await expectRenderedAge(session.video, 150, 650);
  expect(await session.video.locator('video').evaluate(video => ({ rate: video.playbackRate, paused: video.paused, muted: video.muted })))
    .toEqual({ rate: 1, paused: false, muted: true });
});

test('pause/resume, dynamic video replacement and disabling restore original presentation', async ({ session }) => {
  await configure(session, true, 200);
  const canvas = session.video.locator('canvas[data-airbridge-picture-delay]');
  await expect(canvas).toBeVisible();
  await expectRenderedAge(session.video, 100, 550);
  await session.video.locator('video').evaluate(video => video.pause());
  expect(await session.video.locator('video').evaluate(video => video.paused)).toBe(true);
  const pausedFrame = await renderedFrame(session.video);
  await session.video.waitForTimeout(150);
  expect((await renderedFrame(session.video)).frame).toBe(pausedFrame.frame);
  await session.video.locator('video').evaluate(video => video.play());
  await expect.poll(async () => (await renderedFrame(session.video))?.frame).toBeGreaterThan(pausedFrame.frame);
  const oldVideo = await session.video.locator('video').elementHandle();
  const oldCanvas = await canvas.elementHandle();
  await session.video.evaluate(async () => {
    const old = document.querySelector('video'); old.remove();
    const replacement = document.createElement('video'); replacement.id='video'; replacement.style.opacity='0.75'; replacement.setAttribute('aria-hidden','false');
    document.querySelector('#player').append(replacement); await attachSource(replacement);
  });
  await expect.poll(() => oldCanvas.evaluate(element => element.isConnected)).toBe(false);
  expect(await oldVideo.evaluate(element => ({ opacity: element.style.opacity, aria: element.getAttribute('aria-hidden') })))
    .toEqual({ opacity: '0.75', aria: 'false' });
  await expect(canvas).toHaveCount(1);
  await expect(canvas).toBeVisible();
  await expectRenderedAge(session.video, 100, 550);
  await configure(session, false, 200);
  await expect(canvas).toHaveCount(0);
  await expect(session.video.locator('video')).toHaveCSS('opacity', '0.75');
  await expect(session.video.locator('video')).toHaveAttribute('aria-hidden', 'false');
});

test('zero delay, maximum delay and source removal keep one bounded overlay lifecycle', async ({ session }) => {
  await configure(session, true, 0);
  const canvas = session.video.locator('canvas[data-airbridge-picture-delay]');
  await expect(canvas).toBeVisible();
  await configure(session, true, 5000);
  await expect(canvas).toHaveCount(1);
  await expect(canvas).toBeVisible();
  await expectRenderedAge(session.video, 3500, 4700);
  await configure(session, true, 0);
  await expect(canvas).toBeVisible();
  await session.video.locator('video').evaluate(video => video.remove());
  await expect(canvas).toHaveCount(0);
});
