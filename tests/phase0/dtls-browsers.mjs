// Phase 0 / Z3: open the DtlsProbe page in Chromium and Firefox with fake audio devices and record what both the
// browser (getStats) and the server (OpenSSL session, SRTP check) saw. Start the probe first:
//   dotnet run --project tools/DtlsProbe
// then: npm run dtls   (writes out/dtls-<browser>.json, exits 1 unless every browser passed)
import { chromium, firefox } from 'playwright';
import { mkdirSync, writeFileSync } from 'node:fs';

const url = process.env.PROBE_URL ?? 'http://127.0.0.1:5199/';
mkdirSync('out', { recursive: true });

const browsers = [
  ['chromium', () => chromium.launch({ args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] })],
  ['firefox', () => firefox.launch({ firefoxUserPrefs: {
    'media.navigator.streams.fake': true, 'media.navigator.permission.disabled': true,
    // Firefox hides host candidates on loopback/private paths unless told otherwise; the probe answers any.
    'media.peerconnection.ice.obfuscate_host_addresses': false } })],
];

let ok = true;
for (const [name, launch] of browsers) {
  const browser = await launch();
  try {
    const page = await browser.newPage();
    await page.goto(url);
    const result = await page.evaluate(() => window.probeResult);
    result.browser = `${name} ${browser.version()}`;
    writeFileSync(`out/dtls-${name}.json`, JSON.stringify(result, null, 2));
    const pass = result.connectionState === 'connected' && result.server?.state === 'Connected'
      && String(result.server?.srtp ?? '').startsWith('opened');
    ok &&= pass;
    console.log(`${pass ? 'PASS' : 'FAIL'} ${result.browser}: pc=${result.connectionState} server=${result.server?.state}`
      + ` ${result.server?.version} ${result.server?.cipher} ${result.server?.srtpProfile} srtp=${result.server?.srtp}`
      + (result.server?.failure ? ` failure=${result.server.failure}` : '') + (result.error ? ` error=${result.error}` : ''));
  } finally {
    await browser.close();
  }
}
process.exit(ok ? 0 : 1);
