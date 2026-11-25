namespace MediaLoopback;

internal static class LoopbackPage
{
    // Query: ?mode=udp|tcp|both&restart=1. window.loopResult resolves with what the browser measured.
    public const string Html = """
        <!doctype html>
        <meta charset="utf-8">
        <title>BlueChat media loopback</title>
        <video id="v" autoplay playsinline muted width="320"></video>
        <pre id="out">running…</pre>
        <script type="module">
        const q = new URLSearchParams(location.search);
        const mode = q.get('mode') ?? 'udp';
        const sleep = ms => new Promise(r => setTimeout(r, ms));

        async function stats(pc) {
          const s = { audio: {}, video: {}, pair: null };
          const all = await pc.getStats();
          all.forEach(r => {
            if (r.type === 'inbound-rtp') {
              const k = r.kind ?? r.mediaType;
              s[k] = { packetsReceived: r.packetsReceived, packetsLost: r.packetsLost, bytesReceived: r.bytesReceived,
                       framesDecoded: r.framesDecoded, frameWidth: r.frameWidth, frameHeight: r.frameHeight,
                       totalAudioEnergy: r.totalAudioEnergy, keyFramesDecoded: r.keyFramesDecoded };
            }
          });
          // Selected pair: Chromium names it on the transport (selectedCandidatePairId), Firefox flags the pair.
          let pair = null;
          all.forEach(r => { if (r.type === 'transport' && r.selectedCandidatePairId) pair = all.get(r.selectedCandidatePairId); });
          if (!pair) all.forEach(r => { if (r.type === 'candidate-pair' && r.selected) pair = r; });
          if (pair) {
            const local = all.get(pair.localCandidateId);
            s.pair = { protocol: local?.protocol, localType: local?.candidateType };
          }
          return s;
        }

        async function negotiate(pc, session) {
          await pc.setLocalDescription(await pc.createOffer());
          const url = `/offer?mode=${mode}` + (session ? `&session=${session}` : '');
          const res = await fetch(url, { method: 'POST', body: pc.localDescription.sdp });
          if (!res.ok) throw new Error('offer rejected: ' + await res.text());
          const id = res.headers.get('X-Session');
          await pc.setRemoteDescription({ type: 'answer', sdp: await res.text() });
          return id;
        }

        async function waitConnected(pc, ms) {
          const end = Date.now() + ms;
          while (pc.connectionState !== 'connected' && Date.now() < end) {
            if (pc.connectionState === 'failed') return false;
            await sleep(100);
          }
          return pc.connectionState === 'connected';
        }

        async function run() {
          const pc = new RTCPeerConnection({ bundlePolicy: 'max-bundle', rtcpMuxPolicy: 'require' });
          const media = await navigator.mediaDevices.getUserMedia({ audio: true, video: { width: 640, height: 360 } });
          media.getTracks().forEach(t => pc.addTrack(t, media));
          const remote = new MediaStream();
          pc.ontrack = e => { remote.addTrack(e.track); document.getElementById('v').srcObject = remote; };

          const t0 = performance.now();
          const session = await negotiate(pc, null);
          const connected = await waitConnected(pc, 20000);
          const connectMs = Math.round(performance.now() - t0);
          if (!connected) {
            // Diagnostics: every candidate pair the browser tried, with its local candidate.
            const all = await pc.getStats();
            const pairs = [];
            all.forEach(r => { if (r.type === 'candidate-pair') {
              const l = all.get(r.localCandidateId), rem = all.get(r.remoteCandidateId);
              pairs.push({ state: r.state, nominated: r.nominated, requestsSent: r.requestsSent, responsesReceived: r.responsesReceived,
                           local: l && `${l.protocol} ${l.candidateType} ${l.tcpType ?? ''} ${l.address ?? l.ip}:${l.port}`,
                           remote: rem && `${rem.protocol} ${rem.address ?? rem.ip}:${rem.port}` }); } });
            const server = await (await fetch(`/status?session=${session}`)).json();
            const node = await (await fetch('/node')).json();
            return { node, userAgent: navigator.userAgent, mode, session, connected, connectMs, state: pc.connectionState,
                     iceState: pc.iceConnectionState, pairs, server };
          }
          await sleep(5000);
          const first = await stats(pc);
          const result = { userAgent: navigator.userAgent, mode, session, connected, connectMs, first };

          if (q.get('restart') === '1' && connected) {
            pc.restartIce();
            await negotiate(pc, session);
            result.restartConnected = await waitConnected(pc, 20000);
            await sleep(4000);
            result.afterRestart = await stats(pc);
          }

          result.server = await (await fetch(`/status?session=${session}`)).json();
          pc.close();
          media.getTracks().forEach(t => t.stop());
          return result;
        }

        window.loopResult = run().then(r => { document.getElementById('out').textContent = JSON.stringify(r, null, 2); return r; },
                                       e => ({ error: String(e) }));
        </script>
        """;
}
