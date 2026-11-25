// Real browser SDP for the BlueChat.Media.Sdp tests. Run from tests/phase0 (it provides Playwright):
//   cd tests/phase0
//   node ../BlueChat.Media.Sdp.Tests/Corpus/generate.mjs gen                 → writes Corpus/<browser>-<case>.sdp
//   node ../BlueChat.Media.Sdp.Tests/Corpus/generate.mjs check <answersDir>  → feeds our answers back to the
//        browsers (setRemoteDescription) and writes <answersDir>/browser-check.json; exit 1 if any was refused.
// Offers are built with addTransceiver/createDataChannel, so no camera or microphone is needed; the same builder
// is used for `check`, so the browser's own offer matches the stored one (same mids, payload types and rids).
import { createRequire } from 'node:module';
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(join(process.cwd(), 'package.json'));
const { chromium, firefox, webkit } = require('playwright');
const here = dirname(fileURLToPath(import.meta.url));
const cases = ['audio', 'audio-video', 'simulcast', 'datachannel', 'gathered'];
// BROWSERS=webkit limits the run (WebKit has WebRTC only in Playwright's Linux build: run that one in Docker).
const only = process.env.BROWSERS?.split(',');
const browsers = Object.fromEntries(Object.entries({ chromium, firefox, webkit }).filter(([n]) => !only || only.includes(n)));

// Runs in the page: builds a peer connection for one case and returns its offer (local description).
async function buildOffer(kase) {
  const pc = new RTCPeerConnection();
  window.__pc = pc;
  if (kase === 'audio' || kase === 'audio-video' || kase === 'gathered') pc.addTransceiver('audio', { direction: 'sendrecv' });
  if (kase === 'audio-video' || kase === 'gathered') pc.addTransceiver('video', { direction: 'sendrecv' });
  if (kase === 'simulcast') {
    pc.addTransceiver('audio', { direction: 'sendrecv' });
    pc.addTransceiver('video', { direction: 'sendrecv', sendEncodings: [
      { rid: 'q', scaleResolutionDownBy: 4 }, { rid: 'h', scaleResolutionDownBy: 2 }, { rid: 'f' }] });
  }
  if (kase === 'datachannel') pc.createDataChannel('bluechat');
  await pc.setLocalDescription(await pc.createOffer());
  if (kase === 'gathered') {
    await new Promise(resolve => {
      if (pc.iceGatheringState === 'complete') return resolve();
      pc.addEventListener('icegatheringstatechange', () => pc.iceGatheringState === 'complete' && resolve());
      setTimeout(resolve, 8000);
    });
  }
  return pc.localDescription.sdp;
}

async function applyAnswer(sdp) {
  const pc = window.__pc;
  try {
    await pc.setRemoteDescription({ type: 'answer', sdp });
    return { ok: true, signalingState: pc.signalingState,
             directions: pc.getTransceivers().map(t => t.currentDirection) };
  } catch (e) {
    return { ok: false, error: String(e) };
  }
}

const [mode, answersDir] = process.argv.slice(2);
const results = [];
let failed = false;
for (const [name, type] of Object.entries(browsers)) {
  let browser;
  try {
    browser = await type.launch();
  } catch (e) {
    console.log(`${name}: cannot launch (${String(e).split('\n')[0]})`);
    results.push({ browser: name, error: 'launch: ' + String(e).split('\n')[0] });
    continue;
  }
  const version = browser.version();
  try {
    for (const kase of cases) {
      const page = await browser.newPage();
      await page.goto('about:blank');
      let offer;
      try {
        offer = await page.evaluate(buildOffer, kase);
      } catch (e) {
        console.log(`${name} ${version} ${kase}: no offer (${String(e).split('\n')[0]})`);
        results.push({ browser: name, version, case: kase, offer: false, error: String(e).split('\n')[0] });
        await page.close();
        continue;
      }
      if (mode === 'gen') {
        writeFileSync(join(here, `${name}-${kase}.sdp`), offer);
        console.log(`${name} ${version} ${kase}: ${offer.length} bytes`);
      } else {
        const file = join(answersDir, `${name}-${kase}.answer.sdp`);
        if (!existsSync(file)) {
          await page.close();
          continue;
        }
        const r = await page.evaluate(applyAnswer, readFileSync(file, 'utf8'));
        results.push({ browser: name, version, case: kase, ...r });
        failed ||= !r.ok;
        console.log(`${r.ok ? 'ACCEPTED' : 'REFUSED '} ${name} ${version} ${kase}` + (r.ok ? ` ${JSON.stringify(r.directions)}` : ` ${r.error}`));
      }
      await page.close();
    }
  } finally {
    await browser.close();
  }
}
if (mode === 'check') {
  writeFileSync(join(answersDir, 'browser-check.json'), JSON.stringify(results, null, 2));
  process.exit(failed ? 1 : 0);
}
