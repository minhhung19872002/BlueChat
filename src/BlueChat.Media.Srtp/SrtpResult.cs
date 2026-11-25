namespace BlueChat.Media.Srtp;

/// <summary>Outcome of a protect/unprotect call. Bad packets are reported, never thrown (hot path).</summary>
public enum SrtpResult
{
    Ok,

    /// <summary>Shorter than the fixed header plus the authentication tag (and ESRTCP word for SRTCP).</summary>
    TooShort,

    /// <summary>RTP version is not 2 or the header (CSRC list, extension) runs past the end.</summary>
    Malformed,

    /// <summary>The destination span cannot hold the added tag (and ESRTCP word).</summary>
    BufferTooSmall,

    /// <summary>Longer than <see cref="SrtpSession.MaxPacketLength"/>.</summary>
    TooLarge,

    /// <summary>Authentication tag did not verify (RFC 3711 §3.3 step 5, RFC 7714 §8.2).</summary>
    AuthenticationFailed,

    /// <summary>Index already seen or older than the replay window (RFC 3711 §3.3.2).</summary>
    Replayed,

    /// <summary>A new SSRC would exceed the per-context stream limit.</summary>
    TooManyStreams,

    /// <summary>Sender ran out of indices (2^48 for SRTP, 2^31 for SRTCP): the keys must be renegotiated (RFC 3711 §9.2).</summary>
    IndexExhausted,
}
