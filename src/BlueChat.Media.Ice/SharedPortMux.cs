using System.Collections.Concurrent;
using BlueChat.Media.Stun;

namespace BlueChat.Media.Ice;

/// <summary>
/// One UDP port (and one passive TCP port) for every session of a media node (spec §2.3, §6.2): a packet from a known
/// remote endpoint goes to the session bound to it; an unknown endpoint is accepted only through an authenticated
/// STUN check whose USERNAME names a registered local ufrag (RFC 8445 §7.3). Everything else is dropped.
/// Thread-safe: registrations come from signalling, lookups from the receive loop.
/// </summary>
public sealed class SharedPortMux<TSession>
    where TSession : class
{
    private const int MaxEndpointsPerSession = 16;

    private readonly ConcurrentDictionary<string, (TSession Session, IceLiteAgent Agent)> _byUfrag = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<IceEndpoint, TSession> _byEndpoint = new();
    private readonly ConcurrentDictionary<TSession, ConcurrentDictionary<IceEndpoint, byte>> _endpointsOf = new();

    public int SessionCount => _endpointsOf.Count;

    /// <summary>Registers the session under the agent's current local ufrag (call again after an ICE restart).</summary>
    public void Register(TSession session, IceLiteAgent agent)
    {
        _byUfrag[agent.Local.Ufrag] = (session, agent);
        _endpointsOf.TryAdd(session, new ConcurrentDictionary<IceEndpoint, byte>());
    }

    public void Unregister(TSession session)
    {
        foreach (var kv in _byUfrag.Where(kv => ReferenceEquals(kv.Value.Session, session)).ToList())
        {
            _byUfrag.TryRemove(kv.Key, out _);
        }

        if (_endpointsOf.TryRemove(session, out var endpoints))
        {
            foreach (var ep in endpoints.Keys)
            {
                _byEndpoint.TryRemove(ep, out _);
            }
        }
    }

    /// <summary>Session already bound to this remote endpoint (fast path for DTLS/RTP/RTCP).</summary>
    public TSession? Lookup(IceEndpoint from) => _byEndpoint.TryGetValue(from, out var s) ? s : null;

    /// <summary>
    /// Routes a STUN check. When the check is valid for a registered agent, binds <paramref name="from"/> to its
    /// session and returns the session plus the response to send.
    /// </summary>
    public TSession? RouteCheck(in StunMessage request, IceEndpoint from, Span<byte> response, out int responseLength,
        out CheckOutcome outcome)
    {
        responseLength = 0;
        outcome = CheckOutcome.NotForUs;
        var ufrag = IceLiteAgent.LocalUfragOf(request);
        if (ufrag is null || !_byUfrag.TryGetValue(ufrag, out var entry))
        {
            return null;
        }

        outcome = entry.Agent.HandleRequest(request, from, response, out responseLength);
        if (outcome != CheckOutcome.Answered)
        {
            return outcome == CheckOutcome.Rejected ? entry.Session : null;
        }

        var endpoints = _endpointsOf.GetOrAdd(entry.Session, _ => new ConcurrentDictionary<IceEndpoint, byte>());
        if (!endpoints.ContainsKey(from) && endpoints.Count >= MaxEndpointsPerSession)
        {
            return entry.Session; // answered, but not bound: bounded state per session
        }

        // An endpoint moving between sessions (NAT rebinding to a port another session used) follows the latest
        // authenticated check.
        if (_byEndpoint.TryGetValue(from, out var previous) && !ReferenceEquals(previous, entry.Session)
            && _endpointsOf.TryGetValue(previous, out var previousEndpoints))
        {
            previousEndpoints.TryRemove(from, out _);
        }

        _byEndpoint[from] = entry.Session;
        endpoints[from] = 0;
        return entry.Session;
    }

    /// <summary>Forget an endpoint (TCP connection closed).</summary>
    public void Forget(IceEndpoint from)
    {
        if (_byEndpoint.TryRemove(from, out var s) && _endpointsOf.TryGetValue(s, out var endpoints))
        {
            endpoints.TryRemove(from, out _);
        }
    }
}
