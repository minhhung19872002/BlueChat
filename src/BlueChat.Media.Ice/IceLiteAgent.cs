using System.Text;
using BlueChat.Media.Stun;

namespace BlueChat.Media.Ice;

public enum IceState
{
    /// <summary>No valid check received yet.</summary>
    New,

    /// <summary>Valid checks arrive, nothing nominated yet.</summary>
    Checking,

    /// <summary>A pair is nominated; media may flow on <see cref="IceLiteAgent.Selected"/>.</summary>
    Connected,

    /// <summary>No valid check on the selected pair for <see cref="IceLiteAgent.DisconnectedAfter"/>.</summary>
    Disconnected,

    /// <summary>Consent expired (RFC 7675 §5.1, 30 s): stop sending.</summary>
    Failed,

    Closed,
}

public enum CheckOutcome
{
    /// <summary>Valid check; send <c>response</c>.</summary>
    Answered,

    /// <summary>Not for this agent (USERNAME prefix differs) — silently ignore (RFC 8445 §7.3).</summary>
    NotForUs,

    /// <summary>Integrity failed: discard without answer (RFC 8489 §9.1.3: 401 only when MI is missing).</summary>
    Unauthenticated,

    /// <summary>Error response written into <c>response</c> (400/401/420/487).</summary>
    Rejected,

    /// <summary>Not a request we handle (responses, indications, other methods) — ignore.</summary>
    Ignored,
}

/// <summary>
/// ICE-lite answerer for one media transport (RFC 8445 §2.5 lite implementation). The browser is always controlling
/// (§6.1.1), sends connectivity checks; we answer them, take the pair it nominates with USE-CANDIDATE (§8.2.2 for lite),
/// and keep consent from its periodic checks (RFC 7675). No I/O: the caller feeds STUN requests and sends responses.
/// Not thread-safe: one transport is handled by one receive loop.
/// </summary>
public sealed class IceLiteAgent
{
    public static readonly TimeSpan DisconnectedAfter = TimeSpan.FromSeconds(10);

    /// <summary>RFC 7675 §5.1: consent expires 30 s after the last authenticated check.</summary>
    public static readonly TimeSpan ConsentTimeout = TimeSpan.FromSeconds(30);

    private const int MaxPairs = 32;
    private static readonly ushort[] Known =
    [
        StunAttr.Username, StunAttr.MessageIntegrity, StunAttr.MessageIntegritySha256, StunAttr.Priority,
        StunAttr.UseCandidate, StunAttr.Fingerprint,
    ];

    private readonly TimeProvider _clock;
    private readonly Dictionary<IceEndpoint, DateTimeOffset> _validPairs = [];
    private IceCredentials _local;
    private IceCredentials _remote;
    private byte[] _key;
    private (IceCredentials Local, IceCredentials Remote, byte[] Key)? _previous;
    private DateTimeOffset _lastSelectedCheck;

    public IceLiteAgent(IceCredentials local, IceCredentials remote, TimeProvider clock)
    {
        _local = local;
        _remote = remote;
        _key = StunCredentials.ShortTermKey(local.Pwd);
        _clock = clock;
    }

    public IceCredentials Local => _local;

    public IceState State { get; private set; } = IceState.New;

    public IceEndpoint? Selected { get; private set; }

    /// <summary>Raised when the nominated pair changes (first nomination, renomination, after an ICE restart).</summary>
    public event Action<IceEndpoint>? SelectedChanged;

    /// <summary>
    /// RFC 8445 §9: ICE restart with new credentials on both sides. Checks with the previous credentials keep working
    /// until the first valid check with the new ones, so media does not stop during the restart.
    /// </summary>
    public void Restart(IceCredentials newLocal, IceCredentials newRemote)
    {
        _previous = (_local, _remote, _key);
        _local = newLocal;
        _remote = newRemote;
        _key = StunCredentials.ShortTermKey(newLocal.Pwd);
    }

    /// <summary>
    /// Handles one parsed STUN message from <paramref name="from"/>. On <see cref="CheckOutcome.Answered"/> or
    /// <see cref="CheckOutcome.Rejected"/>, <paramref name="response"/> holds <paramref name="responseLength"/> bytes.
    /// </summary>
    public CheckOutcome HandleRequest(in StunMessage request, IceEndpoint from, Span<byte> response, out int responseLength)
    {
        responseLength = 0;
        if (State == IceState.Closed || request.Method != StunMethod.Binding || request.Class != StunClass.Request)
        {
            return CheckOutcome.Ignored;
        }

        // RFC 8445 §7.3: USERNAME = "<our ufrag>:<their ufrag>".
        var username = request.GetString(StunAttr.Username);
        if (username is null || !request.Has(StunAttr.MessageIntegrity))
        {
            // RFC 8489 §9.1.3: request without credentials → 400 Bad Request.
            responseLength = Error(request, response, 400, "Bad Request", null);
            return CheckOutcome.Rejected;
        }

        byte[] key;
        var usingPrevious = false;
        if (Matches(username, _local, _remote))
        {
            key = _key;
        }
        else if (_previous is { } p && Matches(username, p.Local, p.Remote))
        {
            key = p.Key;
            usingPrevious = true;
        }
        else
        {
            return CheckOutcome.NotForUs;
        }

        if (StunIntegrity.VerifySha1(request, key) != IntegrityResult.Ok)
        {
            return CheckOutcome.Unauthenticated;
        }

        // RFC 8445 §7.3: FINGERPRINT is mandatory on connectivity checks; a present but wrong one means not STUN-for-ICE.
        if (StunIntegrity.VerifyFingerprint(request) != IntegrityResult.Ok)
        {
            return CheckOutcome.Ignored;
        }

        Span<ushort> unknown = stackalloc ushort[8];
        var unknownCount = request.UnknownComprehensionRequired(Known, unknown);
        if (unknownCount > 0)
        {
            responseLength = Error(request, response, 420, "Unknown Attribute", key, unknown[..unknownCount]);
            return CheckOutcome.Rejected;
        }

        // RFC 8445 §6.1.1: with a lite peer the full agent must be controlling. A check claiming the controlled role
        // is a role conflict we cannot resolve by switching (a lite agent is always controlled): 487 (§7.3.1.1).
        if (!request.Has(StunAttr.IceControlling))
        {
            responseLength = Error(request, response, 487, "Role Conflict", key);
            return CheckOutcome.Rejected;
        }

        if (!usingPrevious && _previous is not null)
        {
            _previous = null; // first check with the new credentials ends the restart
        }

        var now = _clock.GetUtcNow();
        if (_validPairs.Count >= MaxPairs && !_validPairs.ContainsKey(from))
        {
            // Bounded state: drop the stalest non-selected pair.
            var stale = _validPairs.Where(kv => kv.Key != Selected).MinBy(kv => kv.Value).Key;
            _validPairs.Remove(stale);
        }

        _validPairs[from] = now;
        if (State == IceState.New)
        {
            State = IceState.Checking;
        }

        // RFC 8445 §8.2.2 (lite): a valid check with USE-CANDIDATE nominates the pair. Later nominations replace it.
        if (request.Has(StunAttr.UseCandidate) && Selected != from)
        {
            Selected = from;
            State = IceState.Connected;
            SelectedChanged?.Invoke(from);
        }

        if (from == Selected)
        {
            _lastSelectedCheck = now;
            if (State == IceState.Disconnected)
            {
                State = IceState.Connected;
            }
        }

        responseLength = Success(request, from, response, key);
        return CheckOutcome.Answered;
    }

    /// <summary>Consent freshness (RFC 7675 §5.1). Call periodically (e.g. every second).</summary>
    public void Tick()
    {
        if (Selected is null || State is IceState.Failed or IceState.Closed)
        {
            return;
        }

        var silent = _clock.GetUtcNow() - _lastSelectedCheck;
        if (silent >= ConsentTimeout)
        {
            State = IceState.Failed;
        }
        else if (silent >= DisconnectedAfter)
        {
            State = IceState.Disconnected;
        }
    }

    /// <summary>True when media may be sent to <paramref name="to"/> (consent held, RFC 7675 §5.1).</summary>
    public bool MaySendTo(IceEndpoint to) => State is IceState.Connected or IceState.Disconnected && Selected == to;

    public void Close() => State = IceState.Closed;

    private static bool Matches(string username, IceCredentials local, IceCredentials remote)
    {
        var colon = username.IndexOf(':', StringComparison.Ordinal);
        return colon > 0
               && username.AsSpan(0, colon).SequenceEqual(local.Ufrag)
               && username.AsSpan(colon + 1).SequenceEqual(remote.Ufrag);
    }

    /// <summary>RFC 8445 §7.3.1.4: success with XOR-MAPPED-ADDRESS = source of the request, MI with our pwd, FINGERPRINT.</summary>
    private static int Success(in StunMessage request, IceEndpoint from, Span<byte> response, byte[] key)
    {
        var w = new StunWriter(response, StunMethod.Binding, StunClass.SuccessResponse, request.TransactionId);
        w.AddXorAddress(StunAttr.XorMappedAddress, from.Remote.Address, from.Remote.Port);
        w.AddMessageIntegrity(key);
        w.AddFingerprint();
        return w.Length;
    }

    private static int Error(in StunMessage request, Span<byte> response, int code, string reason, byte[]? key,
        ReadOnlySpan<ushort> unknown = default)
    {
        var w = new StunWriter(response, StunMethod.Binding, StunClass.ErrorResponse, request.TransactionId);
        w.AddErrorCode(code, reason);
        if (!unknown.IsEmpty)
        {
            w.AddUnknownAttributes(unknown);
        }

        if (key is not null)
        {
            w.AddMessageIntegrity(key);
        }

        w.AddFingerprint();
        return w.Length;
    }

    /// <summary>Local ufrag of a check: the part of USERNAME before ':' (used by the shared-port mux).</summary>
    public static string? LocalUfragOf(in StunMessage request)
    {
        if (!request.TryGetAttribute(StunAttr.Username, out var v))
        {
            return null;
        }

        var colon = v.IndexOf((byte)':');
        return colon > 0 ? Encoding.UTF8.GetString(v[..colon]) : null;
    }
}
