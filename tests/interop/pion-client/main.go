// Pion WebRTC client for interop checks: sends an audio-only offer to an HTTP endpoint (body = SDP, answer = SDP),
// waits for the peer connection to reach "connected" and prints one JSON line. Exit code 0 only when connected.
package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"net/http"
	"os"
	"time"

	"github.com/pion/webrtc/v4"
	"github.com/pion/webrtc/v4/pkg/media"
)

func main() {
	url := flag.String("offer-url", "http://probe:5199/offer", "signalling endpoint")
	timeout := flag.Duration("timeout", 20*time.Second, "max wait for connected")
	flag.Parse()

	pc, err := webrtc.NewPeerConnection(webrtc.Configuration{})
	must(err)
	defer pc.Close()

	track, err := webrtc.NewTrackLocalStaticSample(webrtc.RTPCodecCapability{MimeType: webrtc.MimeTypeOpus, ClockRate: 48000, Channels: 2}, "audio", "pion")
	must(err)
	_, err = pc.AddTrack(track)
	must(err)

	connected := make(chan struct{}, 1)
	failed := make(chan struct{}, 1)
	pc.OnConnectionStateChange(func(s webrtc.PeerConnectionState) {
		switch s {
		case webrtc.PeerConnectionStateConnected:
			connected <- struct{}{}
		case webrtc.PeerConnectionStateFailed:
			failed <- struct{}{}
		}
	})

	offer, err := pc.CreateOffer(nil)
	must(err)
	gathered := webrtc.GatheringCompletePromise(pc)
	must(pc.SetLocalDescription(offer))
	<-gathered

	resp, err := http.Post(*url, "application/sdp", bytes.NewBufferString(pc.LocalDescription().SDP))
	must(err)
	body, _ := io.ReadAll(resp.Body)
	resp.Body.Close()
	if resp.StatusCode != 200 {
		report("rejected", string(body))
		os.Exit(1)
	}
	must(pc.SetRemoteDescription(webrtc.SessionDescription{Type: webrtc.SDPTypeAnswer, SDP: string(body)}))

	// Silence frames (Opus TOC for a 20 ms CELT frame with no payload) so SRTP flows once DTLS is up.
	go func() {
		for {
			_ = track.WriteSample(media.Sample{Data: []byte{0xF8, 0xFF, 0xFE}, Duration: 20 * time.Millisecond})
			time.Sleep(20 * time.Millisecond)
		}
	}()

	select {
	case <-connected:
		time.Sleep(2 * time.Second)
		report("connected", "")
	case <-failed:
		report("failed", "")
		os.Exit(1)
	case <-time.After(*timeout):
		report("timeout", pc.ConnectionState().String())
		os.Exit(1)
	}
}

func report(state, detail string) {
	b, _ := json.Marshal(map[string]string{"client": "pion", "state": state, "detail": detail})
	fmt.Println(string(b))
}

func must(err error) {
	if err != nil {
		report("error", err.Error())
		os.Exit(1)
	}
}
