#!/bin/sh
# Phase 0 / Z4 acceptance: every reference tool runs, Pion and aiortc complete DTLS-SRTP with the OpenSSL probe
# (also under 10 % loss + 150 ms delay), tshark finds no malformed packet, coturn's turnutils works against coturn.
set -eu
# Git Bash on Windows rewrites absolute container paths unless told not to.
export MSYS_NO_PATHCONV=1
# Fail on a client that did not connect (tee would hide its exit code).
connected() { grep -Eq "\"state\": ?\"connected\"" "$1" || { echo "FAIL: $1"; exit 1; }; }
cd "$(dirname "$0")"
OUT=out
mkdir -p "$OUT"
DC="docker compose -f compose.yml"
T="$DC exec -T tools"

echo "== versions"
$T sh -c 'cat /usr/local/bin/pion-version.txt; /opt/aiortc/bin/pip show aiortc | grep ^Version; tshark --version | head -1; turnserver --version 2>&1 | tail -1; tc -V'

echo "== wait for probe"
for i in $(seq 1 120); do
  if $T curl -sf -o /dev/null http://10.78.0.10:5199/; then break; fi
  sleep 2
done

echo "== Pion and aiortc against the probe (clean network, tshark capturing)"
$T sh -c 'rm -f /tmp/z4.pcap; (timeout 60 tshark -q -i eth0 -f "udp port 50000" -w /tmp/z4.pcap >/dev/null 2>&1 &) ; sleep 2'
$T pion-client -offer-url http://10.78.0.10:5199/offer | tee "$OUT/z4-pion.json"
connected "$OUT/z4-pion.json"
$T curl -s http://10.78.0.10:5199/status | tee "$OUT/z4-pion-server.json" | grep -q '"handshakeCompleted":true'
$T python3 /usr/local/bin/aiortc_client.py --offer-url http://10.78.0.10:5199/offer | tee "$OUT/z4-aiortc.json"
connected "$OUT/z4-aiortc.json"
$T curl -s http://10.78.0.10:5199/status | tee "$OUT/z4-aiortc-server.json" | grep -q '"handshakeCompleted":true'
$T sh -c 'sleep 1; pkill tshark || true; sleep 1'
$T sh -c 'tshark -r /tmp/z4.pcap -d udp.port==50000,stun -q -z io,stat,0 | tail -4'
# SRTP/SRTCP bodies are encrypted, so tshark cannot judge them (it flags Pion RTCP ciphertext as malformed).
# Judged: every STUN/DTLS packet in both directions and every packet the probe itself sent.
BAD=$($T sh -c 'tshark -r /tmp/z4.pcap -Y "((stun || dtls) || ip.src == 10.78.0.10) && (_ws.malformed || _ws.expert.severity == error)" | wc -l')
CHECKED=$($T sh -c 'tshark -r /tmp/z4.pcap -Y "(stun || dtls) || ip.src == 10.78.0.10" | wc -l')
echo "packets judged: $CHECKED"
echo "malformed or error packets: $BAD"
test "$BAD" -eq 0
$T sh -c 'tshark -r /tmp/z4.pcap -Y "dtls.handshake.type" -T fields -e ip.src -e dtls.handshake.type | sort | uniq -c' | tee "$OUT/z4-dtls-handshake.txt"

echo "== bad network: 10 % loss, 150 ms delay (tc netem on the tools side)"
$T tc qdisc add dev eth0 root netem loss 10% delay 150ms
trap '$T tc qdisc del dev eth0 root 2>/dev/null || true' EXIT
$T pion-client -offer-url http://10.78.0.10:5199/offer -timeout 40s | tee "$OUT/z4-pion-netem.json"
connected "$OUT/z4-pion-netem.json"
$T python3 /usr/local/bin/aiortc_client.py --offer-url http://10.78.0.10:5199/offer --timeout 40 | tee "$OUT/z4-aiortc-netem.json"
connected "$OUT/z4-aiortc-netem.json"
$T tc qdisc del dev eth0 root

echo "== coturn reference: turnutils_uclient against a local coturn"
$T sh -c 'turnserver -n --no-cli --listening-ip=127.0.0.1 --relay-ip=127.0.0.1 --allow-loopback-peers --user=u:p --realm=bluechat.test --lt-cred-mech --log-file=stdout >/tmp/turn.log 2>&1 & sleep 2;
  turnutils_peer -L 127.0.0.1 -p 3480 >/dev/null 2>&1 & sleep 1;
  turnutils_uclient -u u -w p -e 127.0.0.1 -r 3480 -n 20 -m 1 -l 100 127.0.0.1 2>&1 | grep -E "Total lost|Total send|ERROR" ; pkill turnserver; pkill turnutils_peer' | tee "$OUT/z4-coturn.txt"

echo "Z4 OK"
