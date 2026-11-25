namespace DtlsProbe;

internal static class Page
{
    public const string Html = """
        <!doctype html>
        <meta charset="utf-8">
        <title>BlueChat DTLS probe</title>
        <pre id="out">running…</pre>
        <script type="module">
        const out = document.getElementById('out');
        async function run() {
          const pc = new RTCPeerConnection();
          const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
          stream.getTracks().forEach(t => pc.addTrack(t, stream));
          await pc.setLocalDescription(await pc.createOffer());
          const res = await fetch('/offer', { method: 'POST', body: pc.localDescription.sdp });
          if (!res.ok) throw new Error('offer rejected: ' + await res.text());
          await pc.setRemoteDescription({ type: 'answer', sdp: await res.text() });
          const deadline = Date.now() + 20000;
          while (!['connected', 'failed'].includes(pc.connectionState) && Date.now() < deadline) {
            await new Promise(r => setTimeout(r, 100));
          }
          await new Promise(r => setTimeout(r, 2000)); // let a few SRTP packets reach the server
          const transports = [];
          (await pc.getStats()).forEach(s => {
            if (s.type === 'transport') transports.push({ dtlsState: s.dtlsState, tlsVersion: s.tlsVersion,
              dtlsCipher: s.dtlsCipher, srtpCipher: s.srtpCipher, dtlsRole: s.dtlsRole });
          });
          const server = await (await fetch('/status')).json();
          const connectionState = pc.connectionState;
          pc.close();
          return { userAgent: navigator.userAgent, connectionState,
                   browserTransport: transports, server };
        }
        window.probeResult = run().then(r => { out.textContent = JSON.stringify(r, null, 2); return r; },
                                       e => { out.textContent = 'error: ' + e; return { error: String(e) }; });
        </script>
        """;
}
