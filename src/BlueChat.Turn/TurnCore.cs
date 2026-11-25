using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using BlueChat.Media.Stun;

namespace BlueChat.Turn;

/// <summary>
/// TURN protocol state machine (RFC 8656) without socket I/O: the host feeds client messages
/// (<see cref="OnClientData"/>) and peer datagrams (<see cref="OnPeerData"/>), calls <see cref="Tick"/> once a second,
/// and receives outgoing bytes through <see cref="ITurnSink"/> and <see cref="IRelay.Send"/>. All public methods are
/// serialized by one lock.
/// </summary>
public sealed class TurnCore : IDisposable
{
    private const int ResponseBufferSize = 1024;

    // Attributes this server understands in requests (RFC 8489 §15: others in 0x0000–0x7FFF → 420).
    // DONT-FRAGMENT is deliberately absent: the relay cannot set DF portably, and RFC 8656 §7.2 says a server that
    // does not support it answers 420 listing DONT-FRAGMENT.
    private static readonly ushort[] KnownRequestAttributes =
    [
        StunAttr.Username, StunAttr.MessageIntegrity, StunAttr.MessageIntegritySha256, StunAttr.Realm, StunAttr.Nonce,
        StunAttr.PasswordAlgorithm, StunAttr.UserHash, StunAttr.ChannelNumber, StunAttr.Lifetime, StunAttr.XorPeerAddress,
        StunAttr.Data, StunAttr.RequestedAddressFamily, StunAttr.EvenPort, StunAttr.RequestedTransport,
        StunAttr.ReservationToken, StunAttr.Priority, StunAttr.UseCandidate,
    ];

    private readonly object _sync = new();
    private readonly TurnOptions _options;
    private readonly TimeProvider _time;
    private readonly IRelayFactory _relays;
    private readonly ITurnSink _sink;
    private readonly NonceService _nonces;
    private readonly Dictionary<FiveTuple, Allocation> _byTuple = [];
    private readonly Dictionary<IRelay, Allocation> _byRelay = new(ReferenceEqualityComparer.Instance);

    public TurnCore(TurnOptions options, TimeProvider time, IRelayFactory relays, ITurnSink sink)
    {
        _options = options;
        _time = time;
        _relays = relays;
        _sink = sink;
        var nonceKey = options.NonceKey.Length > 0 ? options.NonceKey : RandomNumberGenerator.GetBytes(32);
        _nonces = new NonceService(nonceKey, time, options.NonceLifetime);
    }

    public int AllocationCount
    {
        get
        {
            lock (_sync)
            {
                return _byTuple.Count;
            }
        }
    }

    /// <summary>One complete message from a client: a STUN message or a ChannelData message (RFC 8656 §12.4).</summary>
    public void OnClientData(in FiveTuple tuple, ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            if (data.Length < 4 || data.Length > _options.MaxMessageSize)
            {
                return;
            }

            // RFC 8656 §12.4 / RFC 7983: first two bits 01 = ChannelData (0x4000–0x7FFF), 00 = STUN.
            if ((data[0] & 0xC0) == 0x40)
            {
                HandleChannelData(tuple, data);
                return;
            }

            if (StunMessage.TryParse(data, out var m, _options.MaxMessageSize) != StunParseError.None)
            {
                return;
            }

            // RFC 8489 §7.3: a FINGERPRINT that does not match means the message is not STUN — discard.
            if (m.Has(StunAttr.Fingerprint) && StunIntegrity.VerifyFingerprint(m) != IntegrityResult.Ok)
            {
                return;
            }

            switch (m.Class)
            {
                case StunClass.Request:
                    HandleRequest(tuple, m);
                    break;
                case StunClass.Indication when m.Method == StunMethod.Send:
                    HandleSend(tuple, m);
                    break;
                default:
                    break; // responses and other indications from clients are ignored (RFC 8489 §6.3.2, §6.3.3)
            }
        }
    }

    /// <summary>A UDP datagram received on a relayed address (RFC 8656 §11.3, §12.5).</summary>
    public void OnPeerData(IRelay relay, IPEndPoint peer, ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            if (!_byRelay.TryGetValue(relay, out var a) || data.Length > _options.MaxPeerPayload)
            {
                return;
            }

            var now = _time.GetUtcNow();
            peer = Normalize(peer);
            if (a.Expires <= now || !HasPermission(a, peer.Address, now) || !a.Bucket.TryConsume(data.Length, now))
            {
                return; // §11.3: no permission → silently discarded
            }

            if (a.ChannelsByPeer.TryGetValue(peer, out var number) && a.ChannelsByNumber[number].Expires > now)
            {
                SendChannelData(a.Tuple, number, data);
                return;
            }

            // §11.3: Data indication with XOR-PEER-ADDRESS and DATA.
            var buffer = ArrayPool<byte>.Shared.Rent(data.Length + 64);
            try
            {
                Span<byte> tid = stackalloc byte[12];
                StunWriter.NewTransactionId(tid);
                var w = new StunWriter(buffer, StunMethod.Data, StunClass.Indication, tid);
                w.AddXorAddress(StunAttr.XorPeerAddress, peer.Address, peer.Port);
                w.Add(StunAttr.Data, data);
                _sink.SendToClient(a.Tuple, w.Written);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>RFC 8656 §3.1: when the TCP/TLS connection of an allocation closes, the allocation is deleted.</summary>
    public void OnClientDisconnected(in FiveTuple tuple)
    {
        lock (_sync)
        {
            if (_byTuple.TryGetValue(tuple, out var a))
            {
                Delete(a);
            }
        }
    }

    /// <summary>Expires allocations (§7), permissions (§9) and channel bindings (§12).</summary>
    public void Tick()
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            foreach (var a in _byTuple.Values.ToList())
            {
                if (a.Expires <= now)
                {
                    Delete(a);
                    continue;
                }

                foreach (var (ip, until) in a.Permissions.ToList())
                {
                    if (until <= now)
                    {
                        a.Permissions.Remove(ip);
                    }
                }

                foreach (var c in a.ChannelsByNumber.Values.ToList())
                {
                    if (c.Expires <= now)
                    {
                        a.ChannelsByNumber.Remove(c.Number);
                        a.ChannelsByPeer.Remove(c.Peer);
                        c.Expires = now + _options.ChannelCooldown;
                        a.Cooldown.Add(c);
                    }
                }

                a.Cooldown.RemoveAll(c => c.Expires <= now);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var a in _byTuple.Values.ToList())
            {
                Delete(a);
            }
        }
    }

    // ---------------------------------------------------------------- requests

    private void HandleRequest(in FiveTuple t, in StunMessage m)
    {
        if (m.Method == StunMethod.Binding)
        {
            // RFC 8656 §2: a TURN server is also a STUN server; Binding needs no authentication (RFC 8489 §9).
            Span<byte> buf = stackalloc byte[ResponseBufferSize];
            var w = new StunWriter(buf, StunMethod.Binding, StunClass.SuccessResponse, m.TransactionId);
            w.AddXorAddress(StunAttr.XorMappedAddress, t.Client.Address, t.Client.Port);
            Finish(ref w, null);
            _sink.SendToClient(t, w.Written);
            return;
        }

        if (m.Method is not (StunMethod.Allocate or StunMethod.Refresh or StunMethod.CreatePermission or StunMethod.ChannelBind))
        {
            SendError(t, m, 400, "Bad Request", null);
            return;
        }

        var key = Authenticate(t, m, out var username);
        if (key is null)
        {
            return;
        }

        Span<ushort> unknown = stackalloc ushort[16];
        var unknownCount = m.UnknownComprehensionRequired(KnownRequestAttributes, unknown);
        if (unknownCount > 0)
        {
            SendError(t, m, 420, "Unknown Attribute", key, unknown: unknown[..unknownCount]);
            return;
        }

        switch (m.Method)
        {
            case StunMethod.Allocate:
                HandleAllocate(t, m, key, username);
                break;
            case StunMethod.Refresh:
                HandleRefresh(t, m, key, username);
                break;
            case StunMethod.CreatePermission:
                HandleCreatePermission(t, m, key, username);
                break;
            default:
                HandleChannelBind(t, m, key, username);
                break;
        }
    }

    /// <summary>
    /// RFC 8489 §9.2 long-term credential mechanism: no MESSAGE-INTEGRITY → 401 with REALM and NONCE; missing
    /// USERNAME/REALM/NONCE → 400; bad or expired nonce → 438; unknown user or wrong MAC → 401. Returns the key that
    /// signs the response, or null after an error was sent.
    /// </summary>
    private byte[]? Authenticate(in FiveTuple t, in StunMessage m, out string username)
    {
        username = "";
        if (!m.Has(StunAttr.MessageIntegrity))
        {
            // MESSAGE-INTEGRITY-SHA256 alone is answered like no integrity: the client falls back to SHA-1 (§9.2.4).
            SendError(t, m, 401, "Unauthorized", null, realmAndNonce: true);
            return null;
        }

        var user = m.GetString(StunAttr.Username);
        var realm = m.GetString(StunAttr.Realm);
        var nonce = m.GetString(StunAttr.Nonce);
        if (user is null || realm is null || nonce is null || user.Length > _options.MaxUsernameLength)
        {
            SendError(t, m, 400, "Bad Request", null);
            return null;
        }

        if (!string.Equals(realm, _options.Realm, StringComparison.Ordinal))
        {
            SendError(t, m, 401, "Unauthorized", null, realmAndNonce: true);
            return null;
        }

        if (!_nonces.IsValid(nonce, t.Client.Address))
        {
            SendError(t, m, 438, "Stale Nonce", null, realmAndNonce: true);
            return null;
        }

        if (!TryPassword(user, out var password))
        {
            SendError(t, m, 401, "Unauthorized", null, realmAndNonce: true);
            return null;
        }

        byte[] key;
        try
        {
            key = StunCredentials.LongTermKeyMd5(user, realm, password);
        }
        catch (ArgumentException)
        {
            SendError(t, m, 401, "Unauthorized", null, realmAndNonce: true);
            return null;
        }

        if (StunIntegrity.VerifySha1(m, key) != IntegrityResult.Ok)
        {
            SendError(t, m, 401, "Unauthorized", null, realmAndNonce: true);
            return null;
        }

        username = user;
        return key;
    }

    /// <summary>Time-limited username: not expired and password = base64(HMAC-SHA1(secret, username)).</summary>
    private bool TryPassword(string username, out string password)
    {
        password = "";
        if (_options.SharedSecret.Length == 0
            || !TurnRestCredentials.TryGetExpiry(username, out var expires) || expires <= _time.GetUtcNow())
        {
            return false;
        }

        password = TurnRestCredentials.PasswordFor(_options.SharedSecret, username);
        return true;
    }

    /// <summary>RFC 8656 §7.2 receiving an Allocate request.</summary>
    private void HandleAllocate(in FiveTuple t, in StunMessage m, byte[] key, string username)
    {
        if (_byTuple.TryGetValue(t, out var existing))
        {
            // §7.2 step 2: same transaction → same success response; otherwise 437 Allocation Mismatch.
            if (m.TransactionId.SequenceEqual(existing.AllocateTransactionId))
            {
                _sink.SendToClient(t, existing.AllocateResponse);
            }
            else
            {
                SendError(t, m, 437, "Allocation Mismatch", key);
            }

            return;
        }

        // §7.2 step 3: REQUESTED-TRANSPORT required; only UDP (17) relaying.
        if (!m.TryGetAttribute(StunAttr.RequestedTransport, out var transport) || transport.Length != 4)
        {
            SendError(t, m, 400, "Bad Request", key);
            return;
        }

        if (transport[0] != 17)
        {
            SendError(t, m, 442, "Unsupported Transport Protocol", key);
            return;
        }

        // §7.2 step 5: no reservations are kept, so RESERVATION-TOKEN and EVEN-PORT with R=1 cannot be honoured.
        var evenPort = false;
        if (m.TryGetAttribute(StunAttr.EvenPort, out var even))
        {
            if (even.Length != 1)
            {
                SendError(t, m, 400, "Bad Request", key);
                return;
            }

            if ((even[0] & 0x80) != 0)
            {
                SendError(t, m, 508, "Insufficient Capacity", key);
                return;
            }

            evenPort = true;
        }

        if (m.Has(StunAttr.ReservationToken))
        {
            SendError(t, m, 508, "Insufficient Capacity", key);
            return;
        }

        // §7.2 step 6 / RFC 8656 §18.11: REQUESTED-ADDRESS-FAMILY 0x01 IPv4, 0x02 IPv6. ADDITIONAL-ADDRESS-FAMILY
        // (comprehension-optional) is ignored: only one relayed address is allocated.
        var family = AddressFamily.InterNetwork;
        if (m.TryGetAttribute(StunAttr.RequestedAddressFamily, out var raf))
        {
            if (raf.Length != 4 || raf[0] is not (0x01 or 0x02))
            {
                SendError(t, m, 440, "Address Family not Supported", key);
                return;
            }

            if (m.Has(StunAttr.AdditionalAddressFamily))
            {
                SendError(t, m, 400, "Bad Request", key);
                return;
            }

            family = raf[0] == 0x01 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        }

        if (!_relays.Supports(family))
        {
            SendError(t, m, 440, "Address Family not Supported", key);
            return;
        }

        // §7.2 step 7 quotas: 486 per user and per client IP, 508 for the server as a whole.
        var clientIp = Normalize(t.Client).Address;
        var perUser = 0;
        var perIp = 0;
        foreach (var a in _byTuple.Values)
        {
            perUser += string.Equals(a.Username, username, StringComparison.Ordinal) ? 1 : 0;
            perIp += Normalize(a.Tuple.Client).Address.Equals(clientIp) ? 1 : 0;
        }

        if (perUser >= _options.MaxAllocationsPerUser || perIp >= _options.MaxAllocationsPerClientIp)
        {
            SendError(t, m, 486, "Allocation Quota Reached", key);
            return;
        }

        if (_byTuple.Count >= _options.MaxAllocations)
        {
            SendError(t, m, 508, "Insufficient Capacity", key);
            return;
        }

        var relay = _relays.Create(family, evenPort);
        if (relay is null)
        {
            SendError(t, m, 508, "Insufficient Capacity", key);
            return;
        }

        var now = _time.GetUtcNow();
        var lifetime = Lifetime(m);
        var relayed = new IPEndPoint(_options.RelayAdvertisedAddress ?? relay.LocalEndPoint.Address, relay.LocalEndPoint.Port);
        var allocation = new Allocation(t, username, key, relay, relayed, now + lifetime,
            new TokenBucket(_options.BandwidthBytesPerSecond, _options.BandwidthBurstBytes, now));

        Span<byte> buf = stackalloc byte[ResponseBufferSize];
        var w = new StunWriter(buf, StunMethod.Allocate, StunClass.SuccessResponse, m.TransactionId);
        w.AddXorAddress(StunAttr.XorRelayedAddress, relayed.Address, relayed.Port);
        w.AddUInt32(StunAttr.Lifetime, (uint)lifetime.TotalSeconds);
        w.AddXorAddress(StunAttr.XorMappedAddress, t.Client.Address, t.Client.Port);
        Finish(ref w, key);
        allocation.AllocateTransactionId = m.TransactionId.ToArray();
        allocation.AllocateResponse = w.Written.ToArray();
        _byTuple[t] = allocation;
        _byRelay[relay] = allocation;
        _sink.SendToClient(t, w.Written);
    }

    /// <summary>RFC 8656 §8 Refresh: LIFETIME 0 deletes the allocation.</summary>
    private void HandleRefresh(in FiveTuple t, in StunMessage m, byte[] key, string username)
    {
        if (!TryGetOwnAllocation(t, m, key, username, out var a))
        {
            return;
        }

        if (m.TryGetAttribute(StunAttr.RequestedAddressFamily, out var raf)
            && (raf.Length != 4 || (raf[0] == 0x01) != (a.Family == AddressFamily.InterNetwork)))
        {
            SendError(t, m, 443, "Peer Address Family Mismatch", key);
            return;
        }

        var lifetime = m.TryGetUInt32(StunAttr.Lifetime, out var requested) && requested == 0 ? TimeSpan.Zero : Lifetime(m);
        if (lifetime == TimeSpan.Zero)
        {
            Delete(a);
        }
        else
        {
            a.Expires = _time.GetUtcNow() + lifetime;
        }

        Span<byte> buf = stackalloc byte[ResponseBufferSize];
        var w = new StunWriter(buf, StunMethod.Refresh, StunClass.SuccessResponse, m.TransactionId);
        w.AddUInt32(StunAttr.Lifetime, (uint)lifetime.TotalSeconds);
        Finish(ref w, key);
        _sink.SendToClient(t, w.Written);
    }

    /// <summary>RFC 8656 §9 CreatePermission: every XOR-PEER-ADDRESS must be acceptable, or none is installed.</summary>
    private void HandleCreatePermission(in FiveTuple t, in StunMessage m, byte[] key, string username)
    {
        if (!TryGetOwnAllocation(t, m, key, username, out var a))
        {
            return;
        }

        var peers = new List<IPAddress>();
        foreach (var attr in m.Attributes)
        {
            if (attr.Type != StunAttr.XorPeerAddress)
            {
                continue;
            }

            if (!StunAddress.TryDecodeXor(attr.Value, m.TransactionId, out var ep) || peers.Count >= _options.MaxPermissionsPerAllocation)
            {
                SendError(t, m, 400, "Bad Request", key);
                return;
            }

            peers.Add(Normalize(ep!).Address);
        }

        if (peers.Count == 0)
        {
            SendError(t, m, 400, "Bad Request", key);
            return;
        }

        foreach (var p in peers)
        {
            if (p.AddressFamily != a.Family)
            {
                SendError(t, m, 443, "Peer Address Family Mismatch", key);
                return;
            }

            if (!_options.PeerPolicy.IsAllowed(p))
            {
                SendError(t, m, 403, "Forbidden", key);
                return;
            }
        }

        var now = _time.GetUtcNow();
        if (a.Permissions.Keys.Union(peers).Count() > _options.MaxPermissionsPerAllocation)
        {
            SendError(t, m, 508, "Insufficient Capacity", key);
            return;
        }

        foreach (var p in peers)
        {
            a.Permissions[p] = now + _options.PermissionLifetime;
        }

        SendEmptySuccess(t, m, key);
    }

    /// <summary>RFC 8656 §12.2 ChannelBind: number 0x4000–0x4FFF, one-to-one with a peer transport address.</summary>
    private void HandleChannelBind(in FiveTuple t, in StunMessage m, byte[] key, string username)
    {
        if (!TryGetOwnAllocation(t, m, key, username, out var a))
        {
            return;
        }

        if (!m.TryGetAttribute(StunAttr.ChannelNumber, out var cn) || cn.Length != 4
            || !m.TryGetXorAddress(StunAttr.XorPeerAddress, out var rawPeer))
        {
            SendError(t, m, 400, "Bad Request", key);
            return;
        }

        var number = BinaryPrimitives.ReadUInt16BigEndian(cn);
        if (number is < 0x4000 or > 0x4FFF)
        {
            SendError(t, m, 400, "Bad Request", key);
            return;
        }

        var peer = Normalize(rawPeer!);
        if (peer.AddressFamily != a.Family)
        {
            SendError(t, m, 443, "Peer Address Family Mismatch", key);
            return;
        }

        if (!_options.PeerPolicy.IsAllowed(peer.Address))
        {
            SendError(t, m, 403, "Forbidden", key);
            return;
        }

        // §12.2: the channel is bound to another peer, or the peer to another channel → 400. The same applies during
        // the 5-minute cooldown after a binding expired.
        var conflict =
            (a.ChannelsByNumber.TryGetValue(number, out var byNumber) && !byNumber.Peer.Equals(peer))
            || (a.ChannelsByPeer.TryGetValue(peer, out var byPeer) && byPeer != number)
            || a.Cooldown.Any(c => (c.Number == number) != c.Peer.Equals(peer));
        if (conflict)
        {
            SendError(t, m, 400, "Bad Request", key);
            return;
        }

        var now = _time.GetUtcNow();
        if (byNumber is null)
        {
            if (a.ChannelsByNumber.Count >= _options.MaxChannelsPerAllocation)
            {
                SendError(t, m, 508, "Insufficient Capacity", key);
                return;
            }

            a.Cooldown.RemoveAll(c => c.Number == number);
            byNumber = new ChannelBinding(number, peer, now);
            a.ChannelsByNumber[number] = byNumber;
            a.ChannelsByPeer[peer] = number;
        }

        byNumber.Expires = now + _options.ChannelLifetime;
        if (a.Permissions.Count >= _options.MaxPermissionsPerAllocation && !a.Permissions.ContainsKey(peer.Address))
        {
            SendError(t, m, 508, "Insufficient Capacity", key);
            return;
        }

        a.Permissions[peer.Address] = now + _options.PermissionLifetime; // §12.2: binding also refreshes the permission
        SendEmptySuccess(t, m, key);
    }

    // ---------------------------------------------------------------- data

    /// <summary>RFC 8656 §11.2 Send indication: discarded silently unless allocation, permission and policy allow.</summary>
    private void HandleSend(in FiveTuple t, in StunMessage m)
    {
        if (!_byTuple.TryGetValue(t, out var a)
            || !m.TryGetXorAddress(StunAttr.XorPeerAddress, out var rawPeer)
            || !m.TryGetAttribute(StunAttr.Data, out var data))
        {
            return;
        }

        var now = _time.GetUtcNow();
        var peer = Normalize(rawPeer!);
        if (a.Expires <= now || peer.AddressFamily != a.Family || !HasPermission(a, peer.Address, now)
            || !_options.PeerPolicy.IsAllowed(peer.Address) || !a.Bucket.TryConsume(data.Length, now))
        {
            return;
        }

        a.Relay.Send(peer, data);
    }

    /// <summary>RFC 8656 §12.4: ChannelData from the client → UDP to the bound peer.</summary>
    private void HandleChannelData(in FiveTuple t, ReadOnlySpan<byte> data)
    {
        if (!_byTuple.TryGetValue(t, out var a))
        {
            return;
        }

        var number = BinaryPrimitives.ReadUInt16BigEndian(data);
        var length = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        // §12.4: over UDP the datagram may carry padding; it may not be shorter than the length field says.
        if (4 + length > data.Length || !a.ChannelsByNumber.TryGetValue(number, out var c))
        {
            return;
        }

        var now = _time.GetUtcNow();
        if (a.Expires <= now || c.Expires <= now || !_options.PeerPolicy.IsAllowed(c.Peer.Address)
            || !a.Bucket.TryConsume(length, now))
        {
            return;
        }

        a.Relay.Send(c.Peer, data.Slice(4, length));
    }

    private void SendChannelData(in FiveTuple t, ushort number, ReadOnlySpan<byte> data)
    {
        var padded = t.IsStream ? (data.Length + 3) & ~3 : data.Length; // §12.5: pad to 4 over TCP/TLS
        var buffer = ArrayPool<byte>.Shared.Rent(4 + padded);
        try
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer, number);
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), (ushort)data.Length);
            data.CopyTo(buffer.AsSpan(4));
            buffer.AsSpan(4 + data.Length, padded - data.Length).Clear();
            _sink.SendToClient(t, buffer.AsSpan(0, 4 + padded));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The allocation of this 5-tuple, created by the same user (RFC 8656 §5: requests on an allocation must use the
    /// credentials that created it, else 441 Wrong Credentials; no allocation → 437).
    /// </summary>
    private bool TryGetOwnAllocation(in FiveTuple t, in StunMessage m, byte[] key, string username, out Allocation a)
    {
        a = null!;
        if (!_byTuple.TryGetValue(t, out var found) || found.Expires <= _time.GetUtcNow())
        {
            SendError(t, m, 437, "Allocation Mismatch", key);
            return false;
        }

        if (!string.Equals(found.Username, username, StringComparison.Ordinal))
        {
            SendError(t, m, 441, "Wrong Credentials", key);
            return false;
        }

        a = found;
        return true;
    }

    /// <summary>
    /// RFC 8656 §7.2 / §8: min(requested, max); if that is not greater than the default, the default.
    /// </summary>
    private TimeSpan Lifetime(in StunMessage m)
    {
        if (!m.TryGetUInt32(StunAttr.Lifetime, out var requested))
        {
            return _options.DefaultLifetime;
        }

        var computed = TimeSpan.FromSeconds(Math.Min(requested, _options.MaxLifetime.TotalSeconds));
        return computed > _options.DefaultLifetime ? computed : _options.DefaultLifetime;
    }

    private static bool HasPermission(Allocation a, IPAddress peer, DateTimeOffset now) =>
        a.Permissions.TryGetValue(peer, out var until) && until > now;

    private void Delete(Allocation a)
    {
        _byTuple.Remove(a.Tuple);
        _byRelay.Remove(a.Relay);
        a.Relay.Dispose();
    }

    private static IPEndPoint Normalize(IPEndPoint ep) =>
        ep.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port) : ep;

    private void SendEmptySuccess(in FiveTuple t, in StunMessage m, byte[] key)
    {
        Span<byte> buf = stackalloc byte[ResponseBufferSize];
        var w = new StunWriter(buf, m.Method, StunClass.SuccessResponse, m.TransactionId);
        Finish(ref w, key);
        _sink.SendToClient(t, w.Written);
    }

    /// <summary>
    /// Error response (RFC 8489 §6.3.4). Signed when the request authenticated (§9.2.4); 401/438 carry REALM and a
    /// fresh NONCE (§9.2.4); 420 carries UNKNOWN-ATTRIBUTES (§14.9).
    /// </summary>
    private void SendError(in FiveTuple t, in StunMessage m, int code, string reason, byte[]? key,
        bool realmAndNonce = false, ReadOnlySpan<ushort> unknown = default)
    {
        Span<byte> buf = stackalloc byte[ResponseBufferSize];
        var w = new StunWriter(buf, m.Method, StunClass.ErrorResponse, m.TransactionId);
        w.AddErrorCode(code, reason);
        if (!unknown.IsEmpty)
        {
            w.AddUnknownAttributes(unknown);
        }

        if (realmAndNonce)
        {
            w.AddString(StunAttr.Realm, _options.Realm);
            w.AddString(StunAttr.Nonce, _nonces.Create(Normalize(t.Client).Address));
        }

        Finish(ref w, key);
        _sink.SendToClient(t, w.Written);
    }

    private void Finish(ref StunWriter w, byte[]? key)
    {
        w.AddString(StunAttr.Software, _options.Software);
        if (key is not null)
        {
            w.AddMessageIntegrity(key);
        }

        w.AddFingerprint();
    }
}
