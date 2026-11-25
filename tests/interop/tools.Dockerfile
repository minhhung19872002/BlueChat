# Reference tools for media interop (spec §12, Phase 0 / Z4). Test-only: nothing from here goes into a runtime image.
#   Pion (Go) and aiortc (Python): independent WebRTC stacks · coturn + turnutils: TURN reference
#   tshark: packet format checks · iproute2 (tc netem): loss / delay / rate limits
FROM golang:1.25-bookworm AS pion
WORKDIR /src
COPY pion-client/ ./
RUN go mod init bluechat/pionclient \
 && go get github.com/pion/webrtc/v4@latest \
 && go mod tidy \
 && CGO_ENABLED=0 go build -o /out/pion-client . \
 && go list -m github.com/pion/webrtc/v4 > /out/pion-version.txt

FROM debian:bookworm-slim
ENV DEBIAN_FRONTEND=noninteractive
RUN echo "wireshark-common wireshark-common/install-setuid boolean false" | debconf-set-selections \
 && apt-get update \
 && apt-get install -y --no-install-recommends tshark coturn iproute2 python3 python3-venv curl ca-certificates jq procps \
 && rm -rf /var/lib/apt/lists/*
RUN python3 -m venv /opt/aiortc && /opt/aiortc/bin/pip install --no-cache-dir aiortc aiohttp
COPY --from=pion /out/ /usr/local/bin/
COPY aiortc_client.py /usr/local/bin/aiortc_client.py
ENV PATH="/opt/aiortc/bin:${PATH}"
CMD ["sleep", "infinity"]
