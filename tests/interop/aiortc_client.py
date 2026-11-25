"""aiortc WebRTC client for interop checks: audio-only offer to an HTTP endpoint (body = SDP, answer = SDP),
wait for "connected", print one JSON line. Exit code 0 only when connected."""
import argparse
import asyncio
import json
import sys

import aiohttp
from aiortc import RTCPeerConnection, RTCSessionDescription
from aiortc.mediastreams import AudioStreamTrack


async def main(url: str, timeout: float) -> int:
    pc = RTCPeerConnection()
    pc.addTrack(AudioStreamTrack())  # silence
    done = asyncio.get_running_loop().create_future()

    @pc.on("connectionstatechange")
    def on_state() -> None:
        if pc.connectionState in ("connected", "failed") and not done.done():
            done.set_result(pc.connectionState)

    await pc.setLocalDescription(await pc.createOffer())  # aiortc gathers before returning
    async with aiohttp.ClientSession() as http:
        async with http.post(url, data=pc.localDescription.sdp) as resp:
            body = await resp.text()
            if resp.status != 200:
                print(json.dumps({"client": "aiortc", "state": "rejected", "detail": body}))
                return 1
    await pc.setRemoteDescription(RTCSessionDescription(sdp=body, type="answer"))
    try:
        state = await asyncio.wait_for(done, timeout)
    except asyncio.TimeoutError:
        state = "timeout"
    if state == "connected":
        await asyncio.sleep(2)
    print(json.dumps({"client": "aiortc", "state": state, "detail": ""}))
    await pc.close()
    return 0 if state == "connected" else 1


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--offer-url", default="http://probe:5199/offer")
    p.add_argument("--timeout", type=float, default=20)
    a = p.parse_args()
    sys.exit(asyncio.run(main(a.offer_url, a.timeout)))
