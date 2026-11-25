namespace BlueChat.Media.Srtp;

/// <summary>
/// Sliding replay window (RFC 3711 §3.3.2), 128 indices wide (the RFC asks for at least 64). <see cref="Check"/> before
/// authentication, <see cref="Accept"/> only after the tag verified, so forged packets never move the window.
/// </summary>
internal struct ReplayWindow
{
    public const int Size = 128;

    private UInt128 _mask; // bit d set ⇔ index (Highest - d) was received
    private bool _started;

    public ulong Highest { get; private set; }

    public readonly bool Check(ulong index)
    {
        if (!_started || index > Highest)
        {
            return true;
        }

        var delta = Highest - index;
        return delta < Size && ((_mask >> (int)delta) & UInt128.One) == UInt128.Zero;
    }

    public void Accept(ulong index)
    {
        if (!_started)
        {
            _started = true;
            Highest = index;
            _mask = UInt128.One;
            return;
        }

        if (index > Highest)
        {
            var shift = index - Highest;
            _mask = shift >= Size ? UInt128.One : (_mask << (int)shift) | UInt128.One;
            Highest = index;
        }
        else
        {
            _mask |= UInt128.One << (int)(Highest - index);
        }
    }
}
