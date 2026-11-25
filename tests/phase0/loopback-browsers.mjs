// Phase 5 acceptance (spec §14): the browser sends audio + video to the media node and gets its own streams back.
// Start the node first: dotnet run --project tools/MediaLoopback
// Then: node loopback-browsers.mjs   → out/loopback-<browser>-<case>.json, exit 1 unless every case passed.
import { chromium, firefox } from 'playwright';
import { mkdirSync, writeFileSync } from 'node:fs';

const base = process.env.LOOPBACK_URL ?? 'http://127.0.0.1:5299/';
mkdirSync('out', { recursive: true });

const browsers = [
  ['chromium', () => chromium.launch({ args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream'] })],
  ['firefox', () => firefox.launch({ firefoxUserPrefs: {
    'media.navigator.streams.fake': true, 'media.navigator.permission.disabled': true,
    'media.peerconnection.ice.obfuscate_host_addresses': false } })],
];
const cases = [
  ['udp', 'mode=udp'],
  ['tcp', 'mode=tcp'],
  ['udp-ice-restart', 'mode=udp&restart=1'],
];

const MIN_FRAMES = 60; // ≥ 2 s of decoded echo video at 30 fps after 5 s
const MIN_AUDIO_PACKETS = 150; // ≥ 3 s of 20 ms Opus packets

function judge(r, name) {
  const v = r.first?.video ?? {};
  const a = r.first?.audio ?? {};
  const problems = [];
  if (!r.connected) problems.push('not connected');
  if (!(v.framesDecoded >= MIN_FRAMES)) problems.push(`video frames ${v.framesDecoded}`);
  if (!(v.frameWidth > 0)) problems.push('no video size');
  if (!(a.packetsReceived >= MIN_AUDIO_PACKETS)) problems.push(`audio packets ${a.packetsReceived}`);
  const wantProtocol = name.startsWith('tcp') ? 'tcp' : 'udp';
  if (r.first?.pair?.protocol !== wantProtocol) problems.push(`path ${r.first?.pair?.protocol}`);
  if (name.includes('restart')) {
    if (!r.restartConnected) problems.push('not connected after ICE restart');
    const after = r.afterRestart?.video?.framesDecoded ?? 0;
    if (!(after >= v.framesDecoded + MIN_FRAMES)) problems.push(`video stalled after restart (${v.framesDecoded} → ${after})`);
  }
  const s = r.server?.[0];
  if (!s || s.srtpRejected > 0) problems.push(`server srtpRejected ${s?.srtpRejected}`);
  return problems;
}

// ONLY=firefox:tcp,chromium:udp limits the run; REPEAT=n runs each selected case n times.
const only = (process.env.ONLY ?? '').split(',').filter(Boolean);
const repeat = Number(process.env.REPEAT ?? 1);
const selected = (b, c) => only.length === 0 || only.includes(`${b}:${c}`) || only.includes(b);

let ok = true;
for (const [bname, launch] of browsers) {
  if (!cases.some(([c]) => selected(bname, c))) continue;
  const browser = await launch();
  try {
    let run = 0;
    for (const [cname, query] of cases.flatMap(c => Array(repeat).fill(c))) {
      run++;
      if (!selected(bname, cname)) continue;
      const page = await browser.newPage();
      await page.goto(`${base}?${query}`);
      const r = await page.evaluate(() => window.loopResult);
      r.browser = `${bname} ${browser.version()}`;
      writeFileSync(`out/loopback-${bname}-${cname}${repeat > 1 ? `-${run}` : ''}.json`, JSON.stringify(r, null, 2));
      const problems = r.error ? [r.error] : judge(r, cname);
      ok &&= problems.length === 0;
      const v = r.first?.video ?? {};
      const s = r.server?.[0] ?? {};
      console.log(`${problems.length ? 'FAIL' : 'PASS'} ${r.browser} ${cname}: connect ${r.connectMs} ms, path ${r.first?.pair?.protocol},`
        + ` video ${v.framesDecoded} frames ${v.frameWidth}x${v.frameHeight}, audio ${r.first?.audio?.packetsReceived} pkts,`
        + ` server echoed ${s.echoed} srtp ${s.srtp} pli ${s.pliForwarded} nack ${s.nackForwarded}`
        + (r.afterRestart ? `, after restart ${r.afterRestart.video?.framesDecoded} frames` : '')
        + (problems.length ? ` — ${problems.join('; ')}` : ''));
      if (r.pairs) console.log('  ice', r.iceState, JSON.stringify(r.pairs), 'server', JSON.stringify(r.server), 'node', JSON.stringify(r.node));
      await page.close();
    }
  } finally {
    await browser.close();
  }
}
process.exit(ok ? 0 : 1);
