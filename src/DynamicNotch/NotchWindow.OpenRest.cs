using DynamicNotch.Controls;
using static DynamicNotch.Interop.Native;

namespace DynamicNotch;

/// <summary>
/// The open notch rests when nobody is moving in it: the pointer still for 2.5 s, or gone from the panel for a moment.
/// Then its continuous motion (the theme's particles, Neon's rain, the song slider's flowing wave) holds still exactly
/// where it is, and asks for no frames at all; the first move (or key) and it all flows on from there, with no jump.
/// The panel looks the same at a glance; the CPU and the graphics chip get a break while you read or look away.
/// </summary>
public partial class NotchWindow
{
    bool _openResting, _restArmed;
    POINT _restPointer;
    DateTime _restMoved = DateTime.UtcNow, _restInside = DateTime.UtcNow;

    const double RestStillSeconds = 2.5, RestAwaySeconds = 0.6;

    /// <summary>From the pointer poll (every 60 ms while the notch is open).</summary>
    void OpenRestPoll(POINT p)
    {
        if (_mode != Mode.Expanded) { _restArmed = false; if (_openResting) OpenRest(false); return; }
        var now = DateTime.UtcNow;
        if (!_restArmed) { _restArmed = true; _restMoved = _restInside = now; _restPointer = p; } // each opening starts awake
        bool moved = p.X != _restPointer.X || p.Y != _restPointer.Y;
        _restPointer = p;
        if (moved || IdleTime().TotalMilliseconds < 300) _restMoved = now; // a move, a key, a wheel turn
        // inside the open panel (its width, its height with the chat's extra room)
        double cx = _winLeft + _winW / 2.0, half = (OpenSize.Width / 2 + 10 + 16) * _scale; // as the hover test
        bool inside = p.X >= cx - half && p.X <= cx + half && p.Y >= _winTop && p.Y <= _winTop + ((ExpandedSize.Height + _chatExtra) * _k + 16) * _scale;
        if (inside) _restInside = now;
        bool rest = (now - _restInside).TotalSeconds >= RestAwaySeconds || (now - _restMoved).TotalSeconds >= RestStillSeconds;
        OpenRest(rest);
    }

    void OpenRest(bool on)
    {
        if (on == _openResting) return;
        _openResting = on;
        PillSlider.RestAll(on);
        UpdateRain();    // both read _openResting: paused where they are, or on from there
        UpdateAmbient();
    }
}
