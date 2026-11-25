// Independent reference vectors for BlueChat.Media.Rtp, produced and checked by Pion (spec §12, lesson 5).
//
//	go run . gen    > ../pion-vectors.json   packets built by Pion packetizers/marshallers + Pion's own parse
//	go run . verify < ../ours.json           packets built by BlueChat writers, parsed by Pion and compared
//
// Run in Docker: see src/BlueChat.Media.Rtp/README.md ("Independent cross-check").
package main

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"math/rand"
	"os"
	"time"

	"github.com/pion/interceptor/pkg/twcc"
	"github.com/pion/rtcp"
	"github.com/pion/rtp"
	"github.com/pion/rtp/codecs"
)

type Vector struct {
	Kind   string         `json:"kind"`
	Name   string         `json:"name"`
	Hex    string         `json:"hex"`
	Expect map[string]any `json:"expect"`
}

func main() {
	if len(os.Args) > 1 && os.Args[1] == "verify" {
		verify()
		return
	}
	var out []Vector
	out = append(out, rtpVectors()...)
	out = append(out, vp8Vectors()...)
	out = append(out, vp9Vectors()...)
	out = append(out, h264Vectors()...)
	out = append(out, av1Vectors()...)
	out = append(out, rtcpVectors()...)
	out = append(out, twccVectors()...)
	enc := json.NewEncoder(os.Stdout)
	enc.SetIndent("", " ")
	must(enc.Encode(out))
}

func must(err error) {
	if err != nil {
		panic(err)
	}
}

func h(b []byte) string { return hex.EncodeToString(b) }

// ---------------- RTP ----------------

func rtpVectors() []Vector {
	var v []Vector
	add := func(name string, p *rtp.Packet) {
		raw, err := p.Marshal()
		must(err)
		var back rtp.Packet
		must(back.Unmarshal(raw))
		exts := map[string]string{}
		for _, id := range back.Header.GetExtensionIDs() {
			exts[fmt.Sprint(id)] = h(back.Header.GetExtension(id))
		}
		csrc := []uint32{}
		csrc = append(csrc, back.CSRC...)
		v = append(v, Vector{"rtp", name, h(raw), map[string]any{
			"marker": back.Marker, "pt": back.PayloadType, "seq": back.SequenceNumber, "ts": back.Timestamp,
			"ssrc": back.SSRC, "csrc": csrc, "extension": back.Extension, "profile": back.ExtensionProfile,
			"extensions": exts, "payload": h(back.Payload), "padding": back.PaddingSize,
		}})
	}

	add("plain", &rtp.Packet{Header: rtp.Header{Version: 2, PayloadType: 111, SequenceNumber: 65535, Timestamp: 0xFFFFFFF0, SSRC: 0x11223344}, Payload: []byte{1, 2, 3}})
	add("marker-csrc", &rtp.Packet{Header: rtp.Header{Version: 2, Marker: true, PayloadType: 96, SequenceNumber: 7, Timestamp: 90000, SSRC: 1, CSRC: []uint32{10, 20, 30}}, Payload: bytes.Repeat([]byte{0xAB}, 50)})

	one := rtp.Header{Version: 2, PayloadType: 111, SequenceNumber: 100, Timestamp: 960, SSRC: 0xCAFEBABE}
	abs, _ := rtp.NewAbsSendTimeExtension(time.Unix(1700000000, 123456789)).Marshal()
	lvl, _ := rtp.AudioLevelExtension{Level: 42, Voice: true}.Marshal()
	tcc, _ := rtp.TransportCCExtension{TransportSequence: 0xBEEF}.Marshal()
	must(one.SetExtension(1, []byte("0")))
	must(one.SetExtension(2, abs))
	must(one.SetExtension(3, lvl))
	must(one.SetExtension(5, tcc))
	add("one-byte-ext", &rtp.Packet{Header: one, Payload: []byte{0xF8, 0xFF, 0xFE}})

	two := rtp.Header{Version: 2, PayloadType: 96, SequenceNumber: 1, Timestamp: 2, SSRC: 3, Extension: true, ExtensionProfile: 0x1000}
	must(two.SetExtension(1, []byte("video")))
	must(two.SetExtension(200, bytes.Repeat([]byte{7}, 20)))
	must(two.SetExtension(9, []byte{}))
	add("two-byte-ext", &rtp.Packet{Header: two, Payload: []byte{9}})

	add("padding", &rtp.Packet{Header: rtp.Header{Version: 2, Padding: true, PayloadType: 96, SequenceNumber: 9, Timestamp: 9, SSRC: 9}, Payload: []byte{1, 2, 3, 4, 5}, PaddingSize: 7})
	return v
}

// ---------------- VP8 ----------------

func vp8Fields(name string, payload []byte) Vector {
	var p codecs.VP8Packet
	_, err := p.Unmarshal(payload)
	must(err)
	pid := -1
	bits := 0
	if p.I == 1 {
		pid = int(p.PictureID)
		if p.PictureID > 127 || (len(payload) > 2 && payload[2]&0x80 != 0) {
			bits = 15
		} else {
			bits = 7
		}
	}
	tl0 := -1
	if p.L == 1 {
		tl0 = int(p.TL0PICIDX)
	}
	tid := -1
	if p.T == 1 {
		tid = int(p.TID)
	}
	key := -1
	if p.K == 1 {
		key = int(p.KEYIDX)
	}
	descLen := len(payload) - len(p.Payload)
	isKey := p.S == 1 && p.PID == 0 && len(p.Payload) > 0 && p.Payload[0]&1 == 0
	return Vector{"vp8", name, h(payload), map[string]any{
		"length": descLen, "n": p.N == 1, "s": p.S == 1, "pid": p.PID, "pictureId": pid, "pictureIdBits": bits,
		"tl0": tl0, "tid": tid, "y": p.Y == 1, "keyIdx": key, "isKey": isKey,
	}}
}

func vp8Vectors() []Vector {
	var v []Vector
	key := append([]byte{0x10, 0x02, 0x00, 0x9d, 0x01, 0x2a}, bytes.Repeat([]byte{0x55}, 2500)...) // P bit 0: key frame
	inter := append([]byte{0x31, 0x02, 0x00}, bytes.Repeat([]byte{0x66}, 300)...)                    // P bit 1
	pay := &codecs.VP8Payloader{EnablePictureID: true}
	for i, p := range pay.Payload(1200, key) {
		v = append(v, vp8Fields(fmt.Sprintf("payloader-key-%d", i), p))
	}
	for i, p := range pay.Payload(1200, inter) {
		v = append(v, vp8Fields(fmt.Sprintf("payloader-inter-%d", i), p))
	}
	plain := &codecs.VP8Payloader{}
	for i, p := range plain.Payload(1200, inter) {
		v = append(v, vp8Fields(fmt.Sprintf("payloader-nopid-%d", i), p))
	}
	// Hand-made descriptors covering every optional field (RFC 7741 §4.2), parsed by Pion.
	hand := [][]byte{
		{0x90, 0xF0, 0x81, 0x23, 0x45, 0xA7, 0x00},       // X, I(M=1,15-bit) L T K: TID=2 Y=1 KEYIDX=7, key
		{0x90, 0x80, 0x12, 0x01},                         // X, I 7-bit, inter
		{0x80, 0x60, 0x99, 0x40, 0x01},                   // X, L T: TL0=0x99, TID=1, no S
		{0xB3, 0x10, 0x1F, 0xAA},                         // X N S PID=3, K only KEYIDX=31
		{0x10, 0x00},                                     // S, no X, key
	}
	for i, b := range hand {
		v = append(v, vp8Fields(fmt.Sprintf("hand-%d", i), b))
	}
	return v
}

// ---------------- VP9 ----------------

func vp9Fields(name string, payload []byte) Vector {
	var p codecs.VP9Packet
	_, err := p.Unmarshal(payload)
	must(err)
	pid, bits := -1, 0
	if p.I {
		pid = int(p.PictureID)
		if payload[1]&0x80 != 0 {
			bits = 15
		} else {
			bits = 7
		}
	}
	tid, sid, tl0 := -1, -1, -1
	if p.L {
		tid, sid = int(p.TID), int(p.SID)
		if !p.F {
			tl0 = int(p.TL0PICIDX)
		}
	}
	layers, w0, h0, ng := 0, 0, 0, -1
	if p.V {
		layers = int(p.NS) + 1
		if p.Y && len(p.Width) > 0 {
			w0, h0 = int(p.Width[0]), int(p.Height[0])
		}
		if p.G {
			ng = int(p.NG)
		}
	}
	return Vector{"vp9", name, h(payload), map[string]any{
		"length": len(payload) - len(p.Payload), "p": p.P, "f": p.F, "b": p.B, "e": p.E, "v": p.V, "z": p.Z,
		"pictureId": pid, "pictureIdBits": bits, "tid": tid, "u": p.U, "sid": sid, "d": p.D, "tl0": tl0,
		"refs": len(p.PDiff), "layers": layers, "width0": w0, "height0": h0, "ng": ng,
	}}
}

func vp9Vectors() []Vector {
	var v []Vector
	pay := &codecs.VP9Payloader{FlexibleMode: true, InitialPictureIDFn: func() uint16 { return 0x1234 }}
	for i, p := range pay.Payload(1200, bytes.Repeat([]byte{0x77}, 2600)) {
		v = append(v, vp9Fields(fmt.Sprintf("payloader-flex-%d", i), append([]byte{}, p...)))
	}
	hand := [][]byte{
		// I(15) L non-flexible B, TID=1 U SID=0 D=0, TL0=5, V: N_S=1 Y G: 640x360,1280x720, N_G=2 (TID0 U R=1 pdiff), (TID1 R=0)
		{0xAA, 0x81, 0x02, 0x30, 0x05, 0x38, 0x02, 0x80, 0x01, 0x68, 0x05, 0x00, 0x02, 0xD0, 0x02, 0x14, 0x04, 0x20, 0xFF},
		// I(7) P L F B E: TID=2 SID=1 D, refs: 2 P_DIFF (N=1 then N=0)
		{0xFC, 0x11, 0x43, 0x03, 0x04, 0xFF},
		// P only, no I/L, middle packet
		{0x40, 0xEE},
		// I(7) B Z, key in base layer
		{0x89, 0x7F, 0xDD},
	}
	for i, b := range hand {
		v = append(v, vp9Fields(fmt.Sprintf("hand-%d", i), b))
	}
	return v
}

// ---------------- H.264 ----------------

func h264Vectors() []Vector {
	var v []Vector
	sps := []byte{0x67, 0x42, 0xC0, 0x1F, 0xDA, 0x01, 0x40, 0x16, 0xEC, 0x04, 0x40}
	pps := []byte{0x68, 0xCE, 0x3C, 0x80}
	idr := append([]byte{0x65}, bytes.Repeat([]byte{0x88}, 3000)...)
	slice := append([]byte{0x41}, bytes.Repeat([]byte{0x9A}, 2500)...)
	small := append([]byte{0x41}, bytes.Repeat([]byte{0x9B}, 200)...)
	annexB := func(nals ...[]byte) []byte {
		var b []byte
		for _, n := range nals {
			b = append(b, 0, 0, 0, 1)
			b = append(b, n...)
		}
		return b
	}
	pay := &codecs.H264Payloader{}
	frames := map[string][]byte{"key": annexB(sps, pps, idr), "inter": annexB(slice), "small": annexB(small)}
	for _, name := range []string{"key", "inter", "small"} {
		pk := pay.Payload(1200, frames[name])
		for i, p := range pk {
			v = append(v, Vector{"h264", fmt.Sprintf("payloader-%s-%d", name, i), h(p), map[string]any{
				"frame": name, "index": i, "count": len(pk)}})
		}
	}
	return v
}

// ---------------- AV1 ----------------

func av1Vectors() []Vector {
	var v []Vector
	td := []byte{0x12, 0x00}                              // temporal delimiter, has_size, size 0
	seqPayload := bytes.Repeat([]byte{0x0A}, 12)
	seq := append([]byte{0x0A, byte(len(seqPayload))}, seqPayload...) // sequence header
	frame := []byte{0x32, 0xB8, 0x17}                     // frame OBU, has_size, leb128(3000)
	frame = append(frame, bytes.Repeat([]byte{0x44}, 3000)...)
	inter := []byte{0x32, 0xC8, 0x01}                     // frame OBU, size 200
	inter = append(inter, bytes.Repeat([]byte{0x45}, 200)...)
	pay := &codecs.AV1Payloader{}
	for name, tu := range map[string][]byte{"key": append(append(append([]byte{}, td...), seq...), frame...), "inter": append(append([]byte{}, td...), inter...)} {
		for i, p := range pay.Payload(1200, tu) {
			var pkt codecs.AV1Packet
			_, err := pkt.Unmarshal(p)
			must(err)
			v = append(v, Vector{"av1", fmt.Sprintf("payloader-%s-%d", name, i), h(p), map[string]any{
				"z": pkt.Z, "y": pkt.Y, "w": pkt.W, "n": pkt.N, "elements": len(pkt.OBUElements)}})
		}
	}
	return v
}

// ---------------- RTCP ----------------

func rtcpVectors() []Vector {
	var v []Vector
	rr := []rtcp.ReceptionReport{{SSRC: 0x11, FractionLost: 25, TotalLost: 0xFFFFFF, LastSequenceNumber: 0x10005, Jitter: 33, LastSenderReport: 0xABCD, Delay: 0x10}}
	sr := &rtcp.SenderReport{SSRC: 0xAABBCCDD, NTPTime: 0xE5F0000012345678, RTPTime: 48000, PacketCount: 100, OctetCount: 16000, Reports: rr}
	rrp := &rtcp.ReceiverReport{SSRC: 0x22, Reports: rr}
	sdes := rtcp.NewCNAMESourceDescription(0x33, "bluechat-cname")
	bye := &rtcp.Goodbye{Sources: []uint32{0x44, 0x45}, Reason: "left"}
	nack := &rtcp.TransportLayerNack{SenderSSRC: 1, MediaSSRC: 2, Nacks: rtcp.NackPairsFromSequenceNumbers([]uint16{100, 101, 105, 116, 117, 65535, 0, 3})}
	pli := &rtcp.PictureLossIndication{SenderSSRC: 1, MediaSSRC: 2}
	fir := &rtcp.FullIntraRequest{SenderSSRC: 1, MediaSSRC: 0, FIR: []rtcp.FIREntry{{SSRC: 7, SequenceNumber: 3}, {SSRC: 8, SequenceNumber: 200}}}
	remb := &rtcp.ReceiverEstimatedMaximumBitrate{SenderSSRC: 1, Bitrate: 1_500_000, SSRCs: []uint32{5, 6}}

	one := func(name string, p rtcp.Packet, expect map[string]any) {
		raw, err := p.Marshal()
		must(err)
		v = append(v, Vector{"rtcp", name, h(raw), expect})
	}
	rrExpect := map[string]any{"ssrc": rr[0].SSRC, "fraction": rr[0].FractionLost, "lost": -1, "highest": rr[0].LastSequenceNumber, "jitter": rr[0].Jitter, "lsr": rr[0].LastSenderReport, "dlsr": rr[0].Delay}
	one("sr", sr, map[string]any{"ssrc": sr.SSRC, "ntp": fmt.Sprint(sr.NTPTime), "rtp": sr.RTPTime, "packets": sr.PacketCount, "octets": sr.OctetCount, "block": rrExpect})
	one("rr", rrp, map[string]any{"ssrc": rrp.SSRC, "block": rrExpect})
	one("sdes", sdes, map[string]any{"ssrc": 0x33, "cname": "bluechat-cname"})
	one("bye", bye, map[string]any{"sources": bye.Sources, "reason": bye.Reason})
	var lost []uint16
	for _, p := range nack.Nacks {
		lost = append(lost, p.PacketList()...)
	}
	one("nack", nack, map[string]any{"sender": 1, "media": 2, "lost": lost})
	one("pli", pli, map[string]any{"sender": 1, "media": 2})
	one("fir", fir, map[string]any{"sender": 1, "entries": [][]uint32{{7, 3}, {8, 200}}})
	rembRaw, _ := remb.Marshal()
	var rembBack rtcp.ReceiverEstimatedMaximumBitrate
	must(rembBack.Unmarshal(rembRaw))
	one("remb", remb, map[string]any{"sender": 1, "bitrate": uint64(rembBack.Bitrate), "ssrcs": remb.SSRCs})

	compound, err := rtcp.Marshal([]rtcp.Packet{rrp, sdes, nack, pli})
	must(err)
	v = append(v, Vector{"rtcp-compound", "rr-sdes-nack-pli", h(compound), map[string]any{"types": []int{201, 202, 205, 206}}})
	return v
}

// ---------------- transport-cc ----------------

func twccExpect(raw []byte) map[string]any {
	var fb rtcp.TransportLayerCC
	must(fb.Unmarshal(raw))
	var symbols []int
	for _, c := range fb.PacketChunks {
		switch ch := c.(type) {
		case *rtcp.RunLengthChunk:
			for i := 0; i < int(ch.RunLength); i++ {
				symbols = append(symbols, int(ch.PacketStatusSymbol))
			}
		case *rtcp.StatusVectorChunk:
			for _, s := range ch.SymbolList {
				symbols = append(symbols, int(s))
			}
		}
	}
	symbols = symbols[:fb.PacketStatusCount]
	deltas := make([]int64, len(symbols))
	d := 0
	for i, s := range symbols {
		if s == 1 || s == 2 {
			deltas[i] = fb.RecvDeltas[d].Delta / 250
			d++
		}
	}
	return map[string]any{"sender": fb.SenderSSRC, "media": fb.MediaSSRC, "base": fb.BaseSequenceNumber, "count": fb.PacketStatusCount,
		"refTime": fb.ReferenceTime, "fbCount": fb.FbPktCount, "symbols": symbols, "deltas": deltas}
}

func twccVectors() []Vector {
	var v []Vector
	rng := rand.New(rand.NewSource(7))
	scenarios := []struct {
		name string
		lossPct, jitterUs, gapUs int
		n                       int
	}{{"clean", 0, 300, 5000, 60}, {"loss10", 10, 2000, 5000, 80}, {"burst", 40, 50000, 20000, 40}, {"reorder", 5, 90000, 1000, 100}}
	for _, sc := range scenarios {
		r := twcc.NewRecorder(0x5555)
		t := int64(1_000_000)
		for i := 0; i < sc.n; i++ {
			if rng.Intn(100) < sc.lossPct {
				continue
			}
			t += int64(sc.gapUs + rng.Intn(sc.jitterUs+1))
			arrival := t
			if sc.name == "reorder" && i%7 == 3 {
				arrival -= 70000 // arrives "before" the previous one: negative delta
			}
			r.Record(0x6666, uint16(65500+i), arrival)
		}
		for k, p := range r.BuildFeedbackPacket() {
			raw, err := p.Marshal()
			must(err)
			if _, ok := p.(*rtcp.TransportLayerCC); !ok {
				continue
			}
			v = append(v, Vector{"twcc", fmt.Sprintf("%s-%d", sc.name, k), h(raw), twccExpect(raw)})
		}
	}
	return v
}

// ---------------- verify: BlueChat output parsed by Pion ----------------

func verify() {
	var in []Vector
	must(json.NewDecoder(os.Stdin).Decode(&in))
	fails := 0
	check := func(v Vector, ok bool, detail string) {
		if ok {
			fmt.Printf("ok   %-14s %s\n", v.Kind, v.Name)
		} else {
			fails++
			fmt.Printf("FAIL %-14s %s: %s\n", v.Kind, v.Name, detail)
		}
	}
	for _, v := range in {
		raw, err := hex.DecodeString(v.Hex)
		must(err)
		switch v.Kind {
		case "rtp":
			var p rtp.Packet
			if err := p.Unmarshal(raw); err != nil {
				check(v, false, err.Error())
				continue
			}
			exts := map[string]any{}
			for _, id := range p.Header.GetExtensionIDs() {
				exts[fmt.Sprint(id)] = h(p.Header.GetExtension(id))
			}
			got := map[string]any{"marker": p.Marker, "pt": float64(p.PayloadType), "seq": float64(p.SequenceNumber),
				"ts": float64(p.Timestamp), "ssrc": float64(p.SSRC), "payload": h(p.Payload), "padding": float64(p.PaddingSize), "extensions": exts}
			check(v, same(got, v.Expect), fmt.Sprintf("got %v want %v", got, v.Expect))
		case "twcc":
			got := twccExpect(raw)
			check(v, same(normalize(got), v.Expect), fmt.Sprintf("got %v want %v", got, v.Expect))
		case "rtcp":
			pk, err := rtcp.Unmarshal(raw)
			if err != nil {
				check(v, false, err.Error())
				continue
			}
			check(v, same(normalize(describe(pk)), v.Expect), fmt.Sprintf("got %v want %v", describe(pk), v.Expect))
		default:
			check(v, false, "unknown kind")
		}
	}
	fmt.Printf("%d vectors, %d failed\n", len(in), fails)
	if fails > 0 {
		os.Exit(1)
	}
}

func describe(pk []rtcp.Packet) map[string]any {
	types := []int{}
	m := map[string]any{}
	for _, p := range pk {
		switch x := p.(type) {
		case *rtcp.SenderReport:
			types = append(types, 200)
			m["ssrc"] = x.SSRC
			m["ntp"] = fmt.Sprint(x.NTPTime)
			m["packets"] = x.PacketCount
			m["blocks"] = len(x.Reports)
		case *rtcp.ReceiverReport:
			types = append(types, 201)
			m["ssrc"] = x.SSRC
			m["blocks"] = len(x.Reports)
			if len(x.Reports) > 0 {
				m["fraction"] = x.Reports[0].FractionLost
				m["jitter"] = x.Reports[0].Jitter
			}
		case *rtcp.SourceDescription:
			types = append(types, 202)
			m["cname"] = string(x.Chunks[0].Items[0].Text)
		case *rtcp.Goodbye:
			types = append(types, 203)
			m["sources"] = x.Sources
			m["reason"] = x.Reason
		case *rtcp.TransportLayerNack:
			types = append(types, 205)
			var lost []uint16
			for _, p := range x.Nacks {
				lost = append(lost, p.PacketList()...)
			}
			m["lost"] = lost
		case *rtcp.PictureLossIndication:
			types = append(types, 206)
			m["media"] = x.MediaSSRC
		case *rtcp.FullIntraRequest:
			types = append(types, 206)
			var e [][]uint32
			for _, f := range x.FIR {
				e = append(e, []uint32{f.SSRC, uint32(f.SequenceNumber)})
			}
			m["entries"] = e
		case *rtcp.ReceiverEstimatedMaximumBitrate:
			types = append(types, 206)
			m["bitrate"] = uint64(x.Bitrate)
			m["ssrcs"] = x.SSRCs
		default:
			types = append(types, -1)
		}
	}
	m["types"] = types
	return m
}

// normalize round-trips through JSON so numbers compare as float64 like the decoded expectations.
func normalize(m map[string]any) map[string]any {
	b, err := json.Marshal(m)
	must(err)
	var out map[string]any
	must(json.Unmarshal(b, &out))
	return out
}

func same(got, want map[string]any) bool {
	g, _ := json.Marshal(normalize(got))
	w, _ := json.Marshal(normalize(want))
	return bytes.Equal(g, w)
}
