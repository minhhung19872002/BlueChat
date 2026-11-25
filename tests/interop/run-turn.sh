#!/bin/sh
# BlueChat.Turn against independent TURN clients (spec §6.6, lesson 5). Start the rig first:
#   docker compose -f tests/interop/turn-compose.yml up -d
# 1. coturn turnutils_uclient, REST-API credentials (-W secret), Send/Data indications (-s), over UDP, TCP, TLS:
#    0 % loss expected.
# 2. turnutils_uclient without -s picks channel numbers from the RFC 5766 range (0x4000–0x7FFE); RFC 8656 §12
#    narrowed it to 0x4000–0x4FFF and requires 400 outside it — the 400 is checked as intended behaviour.
# 3. Pion TURN client (channel numbers from 0x4000, so ChannelData is used) over UDP, TCP, TLS: 0 lost echoes;
#    tshark on the server side confirms ChannelBind success, ChannelData frames and no malformed packet.
# 4. Negative: peers outside the allow-list (loopback, the TURN host, an unlisted 10.x) → 403, wrong secret → refused.
set -eu
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")"
OUT=out
mkdir -p "$OUT"
DC="docker compose -f turn-compose.yml"
T="$DC exec -T tools"
NET=bluechat-turn-interop_turn
SERVER=10.79.0.10
PEER=10.79.0.20
SECRET=interop-secret
PION_TURN=v4.1.4

echo "== wait for TURN"
for i in $(seq 1 120); do
  if $DC logs turn 2>/dev/null | grep -q "TURN ready"; then break; fi
  sleep 2
done
$DC logs turn | grep "TURN ready"
$T sh -c "pkill turnutils_peer || true; (turnutils_peer -L $PEER -p 3480 >/tmp/peer.log 2>&1 &); sleep 1"

echo "== 1. turnutils_uclient, Send/Data indications"
for mode in "udp:" "tcp:-t" "tls:-t -S -p 5349"; do
  name=${mode%%:*}; flags=${mode#*:}
  $T sh -c "turnutils_uclient -W $SECRET -u alice -s -c -n 200 -m 2 -l 160 -e $PEER -r 3480 $flags $SERVER 2>&1" > "$OUT/turn-uclient-$name.txt" || true
  line=$(grep -E "Total lost packets" "$OUT/turn-uclient-$name.txt" | tail -1 || true)
  echo "uclient $name: ${line:-NO RESULT}"
  echo "$line" | grep -Eq "Total lost packets 0 \(0\.0+%\)" || { echo "FAIL"; tail -20 "$OUT/turn-uclient-$name.txt"; exit 1; }
done

echo "== 2. turnutils_uclient ChannelBind: random channel number in 0x4000–0x7FFE (RFC 5766 range)"
# RFC 8656 §12 narrowed the range to 0x4000–0x4FFF. uclient picks at random, so either outcome is correct and both
# are checked: outside the range → 400; inside → ChannelData relays with 0 % loss. Six runs usually hit both paths.
for i in 1 2 3 4 5 6; do
  $T sh -c "turnutils_uclient -W $SECRET -u alice -c -n 5 -m 1 -e $PEER -r 3480 $SERVER 2>&1" > "$OUT/turn-uclient-channel-$i.txt" || true
  if grep -q "channel bind: error 400" "$OUT/turn-uclient-channel-$i.txt"; then
    echo "uclient channel run $i: 400 (number outside RFC 8656 §12 range)"
  elif grep -q "Total lost packets 0 (0.000000%)" "$OUT/turn-uclient-channel-$i.txt"; then
    echo "uclient channel run $i: bound inside 0x4000–0x4FFF, 0 % loss over ChannelData"
  else
    echo "FAIL: uclient channel run $i neither 400 nor clean relay"; cat "$OUT/turn-uclient-channel-$i.txt"; exit 1
  fi
done

echo "== 3. Pion TURN client $PION_TURN (ChannelData), with a server-side capture"
docker rm -f bc-turn-capture >/dev/null 2>&1 || true
docker run --rm -d --name bc-turn-capture --net container:bluechat-turn-interop-turn-1 --cap-add NET_RAW --cap-add NET_ADMIN \
  bluechat/interop-tools:dev sleep infinity >/dev/null
docker exec -d bc-turn-capture sh -c 'tshark -i eth0 -f "port 3478" -w /tmp/c.pcap >/dev/null 2>&1'
sleep 3
docker run --rm --network $NET -v "$(pwd)/pion-turn:/src:ro" -v bluechat-gomod:/go/pkg/mod golang:1.25-bookworm sh -c "
  mkdir -p /b && cp /src/main.go /b/ && cd /b && go mod init bluechat/pionturn >/dev/null 2>&1 &&
  go get github.com/pion/turn/v4@$PION_TURN >/dev/null 2>&1 && go mod tidy >/dev/null 2>&1 && CGO_ENABLED=0 go build -o p . &&
  ./p -transport udp -server $SERVER:3478 && ./p -transport tcp -server $SERVER:3478 && ./p -transport tls -server $SERVER:5349" \
  | tee "$OUT/turn-pion.txt"
test "$(grep -c '"lost":0' "$OUT/turn-pion.txt")" -eq 3 || { echo "FAIL: pion"; exit 1; }
sleep 1
docker exec bc-turn-capture sh -c 'pkill tshark; sleep 1;
  echo "channelbind success: $(tshark -r /tmp/c.pcap -Y "stun.type == 0x0109" 2>/dev/null | wc -l)";
  echo "channelbind error:   $(tshark -r /tmp/c.pcap -Y "stun.type == 0x0119" 2>/dev/null | wc -l)";
  echo "channeldata frames:  $(tshark -r /tmp/c.pcap -Y "stun.channel" 2>/dev/null | wc -l)";
  echo "malformed/error:     $(tshark -r /tmp/c.pcap -Y "_ws.malformed || _ws.expert.severity == error" 2>/dev/null | wc -l)"' \
  | tee "$OUT/turn-pion-capture.txt"
docker rm -f bc-turn-capture >/dev/null
grep -Eq "malformed/error: +0$" "$OUT/turn-pion-capture.txt" || { echo "FAIL: malformed packets"; exit 1; }
grep -Eq "channelbind error: +0$" "$OUT/turn-pion-capture.txt" || { echo "FAIL: channel bind errors"; exit 1; }

echo "== 4. negative"
for bad in 127.0.0.1 10.79.0.10 10.79.0.30; do
  $T sh -c "turnutils_uclient -W $SECRET -u alice -s -c -n 5 -m 1 -l 100 -e $bad -r 3480 $SERVER 2>&1" > "$OUT/turn-deny-$bad.txt" || true
  if grep -Eq "error 403" "$OUT/turn-deny-$bad.txt"; then
    echo "peer $bad: 403 Forbidden"
  else
    echo "FAIL: $bad was not refused"; tail -20 "$OUT/turn-deny-$bad.txt"; exit 1
  fi
done
$T sh -c "turnutils_uclient -W wrong-secret -u alice -s -c -n 5 -m 1 -e $PEER -r 3480 $SERVER 2>&1" > "$OUT/turn-wrong-secret.txt" || true
if grep -Eq "Total lost packets 0 \(0\.0+%\)" "$OUT/turn-wrong-secret.txt"; then
  echo "FAIL: wrong secret accepted"; exit 1
fi
grep -q "Cannot complete Allocation" "$OUT/turn-wrong-secret.txt" || { echo "FAIL: wrong secret not refused"; exit 1; }
echo "wrong secret: allocation refused (401 after the authenticated retry)"

echo "TURN interop OK"
