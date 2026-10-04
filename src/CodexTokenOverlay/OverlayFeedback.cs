namespace CodexTokenOverlay;

internal readonly record struct OverlayFeedbackFrame(double Hover, double Press, double Open, double PanelReveal);

// The clock is supplied by the window. No idle polling or asynchronous callbacks are needed.
internal sealed class OverlayFeedback
{
    private readonly Tween _hover = new();
    private readonly Tween _press = new();
    private readonly Tween _open = new();
    private readonly Tween _reveal = new();

    public bool PointerInside { get; private set; }
    public bool PointerDown { get; private set; }
    public bool Expanded { get; private set; }
    public bool IsAnimating => _hover.IsAnimating || _press.IsAnimating || _open.IsAnimating || _reveal.IsAnimating;
    public OverlayFeedbackFrame Frame => new(_hover.Value, _press.Value, _open.Value, _reveal.Value);

    public void Move(bool inside, long now, bool animate)
    {
        PointerInside = inside;
        _hover.To(inside ? 1 : 0, now, 130, animate);
        if (PointerDown) _press.To(inside ? 1 : 0, now, 80, animate: false);
    }

    public bool Down(bool inside, long now, bool animate)
    {
        if (!inside || PointerDown) return false;
        PointerDown = true;
        Move(true, now, animate);
        return true;
    }

    public bool Up(bool inside, long now, bool animate)
    {
        var clicked = PointerDown && inside;
        PointerDown = false;
        PointerInside = inside;
        _hover.To(inside ? 1 : 0, now, 130, animate);
        _press.To(0, now, 100, animate);
        return clicked;
    }

    public void SetExpanded(bool expanded, long now, bool animate)
    {
        if (Expanded == expanded) return;
        Expanded = expanded;
        _open.To(expanded ? 1 : 0, now, 150, animate);
        if (expanded)
        {
            _reveal.Snap(0);
            _reveal.To(1, now, 160, animate);
        }
        else _reveal.Snap(0);
    }

    public void CancelPointer()
    {
        PointerDown = PointerInside = false;
        _hover.Snap(0);
        _press.Snap(0);
    }

    public void Advance(long now)
    {
        _hover.Advance(now); _press.Advance(now); _open.Advance(now); _reveal.Advance(now);
    }

    public void Finish()
    {
        _hover.Finish(); _press.Finish(); _open.Finish(); _reveal.Finish();
    }

    private sealed class Tween
    {
        private double _from, _target;
        private long _started;
        private int _duration;
        public double Value { get; private set; }
        public bool IsAnimating { get; private set; }

        public void To(double target, long now, int duration, bool animate)
        {
            Advance(now);
            if (!animate) { Snap(target); return; }
            if (_target == target) return;
            _from = Value; _target = target; _started = now; _duration = duration;
            IsAnimating = Value != target;
        }

        public void Advance(long now)
        {
            if (!IsAnimating) return;
            var t = Math.Clamp((now - _started) / (double)_duration, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);
            Value = _from + (_target - _from) * eased;
            if (t >= 1) Finish();
        }

        public void Snap(double value)
        {
            Value = _from = _target = value;
            IsAnimating = false;
        }

        public void Finish() => Snap(_target);
    }
}
