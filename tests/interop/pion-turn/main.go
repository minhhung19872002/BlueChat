// Pion TURN client for interop with BlueChat.Turn: allocates over UDP, TCP or TLS with REST-API time-limited
// credentials, sends numbered packets to an echo peer (turnutils_peer) through the relay and counts the echoes.
// Pion binds a channel (0x4000…, RFC 8656 range) after the first packet, so traffic moves to ChannelData.
// Prints one JSON line; exit code 0 only with zero loss.
package main

import (
	"crypto/hmac"
	"crypto/sha1"
	"crypto/tls"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"flag"
	"fmt"
	"net"
	"os"
	"sync/atomic"
	"time"

	"github.com/pion/logging"
	"github.com/pion/turn/v4"
)

func main() {
	server := flag.String("server", "10.79.0.10:3478", "TURN server host:port")
	transport := flag.String("transport", "udp", "udp, tcp or tls")
	secret := flag.String("secret", "interop-secret", "REST API shared secret")
	realm := flag.String("realm", "bluechat.test", "realm")
	peer := flag.String("peer", "10.79.0.20:3480", "echo peer")
	count := flag.Int("n", 200, "packets")
	size := flag.Int("size", 160, "payload bytes")
	flag.Parse()

	user := fmt.Sprintf("%d:pion", time.Now().Add(time.Hour).Unix())
	mac := hmac.New(sha1.New, []byte(*secret))
	mac.Write([]byte(user))
	pass := base64.StdEncoding.EncodeToString(mac.Sum(nil))

	var conn net.PacketConn
	switch *transport {
	case "udp":
		c, err := net.ListenPacket("udp4", "0.0.0.0:0")
		must(err)
		conn = c
	case "tcp":
		c, err := net.Dial("tcp", *server)
		must(err)
		conn = turn.NewSTUNConn(c)
	case "tls":
		c, err := tls.Dial("tcp", *server, &tls.Config{InsecureSkipVerify: true}) //nolint:gosec // self-signed test cert
		must(err)
		conn = turn.NewSTUNConn(c)
	default:
		must(fmt.Errorf("unknown transport %s", *transport))
	}

	client, err := turn.NewClient(&turn.ClientConfig{
		STUNServerAddr: *server,
		TURNServerAddr: *server,
		Conn:           conn,
		Username:       user,
		Password:       pass,
		Realm:          *realm,
		LoggerFactory:  logging.NewDefaultLoggerFactory(),
	})
	must(err)
	defer client.Close()
	must(client.Listen())
	relay, err := client.Allocate()
	must(err)
	defer relay.Close()

	peerAddr, err := net.ResolveUDPAddr("udp4", *peer)
	must(err)

	var received atomic.Int64
	go func() {
		buf := make([]byte, 2048)
		for {
			n, _, err := relay.ReadFrom(buf)
			if err != nil {
				return
			}
			if n == *size {
				received.Add(1)
			}
		}
	}()

	payload := make([]byte, *size)
	for i := 0; i < *count; i++ {
		binary.BigEndian.PutUint32(payload, uint32(i))
		_, err := relay.WriteTo(payload, peerAddr)
		must(err)
		time.Sleep(20 * time.Millisecond)
	}
	time.Sleep(2 * time.Second)

	got := received.Load()
	lost := int64(*count) - got
	out, _ := json.Marshal(map[string]any{
		"client": "pion-turn", "transport": *transport, "relayed": relay.LocalAddr().String(),
		"sent": *count, "echoed": got, "lost": lost,
	})
	fmt.Println(string(out))
	if lost != 0 {
		os.Exit(1)
	}
}

func must(err error) {
	if err != nil {
		fmt.Printf("{\"client\":\"pion-turn\",\"error\":%q}\n", err.Error())
		os.Exit(1)
	}
}
