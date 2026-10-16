using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace AgentX.App;

/// <summary>
/// Approved flat-green bottom-filling context circle: a thin-outlined
/// disc filled solid from the bottom up to the real Pi usage percentage,
/// with a crisp horizontal fill boundary. Unknown context renders as a
/// hollow muted outline — never a measured 0%. Pure fill math is tested;
/// rendering is evidenced by screenshots. Sizes via Width/Height
/// (16 footer, 22 Usage card).
/// </summary>
public sealed class ContextGauge : Control
{
    public static readonly StyledProperty<double?> PercentProperty =
        AvaloniaProperty.Register<ContextGauge, double?>(nameof(Percent));

    public static readonly StyledProperty<bool> HasNoticeProperty =
        AvaloniaProperty.Register<ContextGauge, bool>(nameof(HasNotice));

    static ContextGauge()
    {
        PercentProperty.Changed.AddClassHandler<ContextGauge>((c, _) => c.InvalidateVisual());
        HasNoticeProperty.Changed.AddClassHandler<ContextGauge>((c, _) => c.InvalidateVisual());
        FocusableProperty.OverrideDefaultValue<ContextGauge>(false);
    }

    /// <summary>Pi-reported context percent, or null when unknown.</summary>
    public double? Percent
    {
        get => GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    /// <summary>True while a footer notice waits: the outline turns amber.</summary>
    public bool HasNotice
    {
        get => GetValue(HasNoticeProperty);
        set => SetValue(HasNoticeProperty, value);
    }

    /// Fill fraction for a known percent, clamped to the disc. Pure so the
    /// clamping is tested, not eyeballed.
    public static double FillFraction(double percent)
    {
        if (double.IsNaN(percent))
        {
            return 0;
        }

        return Math.Clamp(percent / 100.0, 0.0, 1.0);
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        var diameter = Math.Min(size.Width, size.Height);
        if (diameter <= 0)
        {
            return;
        }

        var rect = new Rect((size.Width - diameter) / 2, (size.Height - diameter) / 2, diameter, diameter);
        var outline = HasNotice
            ? AxTheme.YellowBrush
            : Percent is null ? AxTheme.MutedBrush : AxTheme.GreenBrush;

        if (Percent is { } known)
        {
            var fraction = FillFraction(known);
            if (fraction > 0)
            {
                var geometry = new EllipseGeometry(rect);
                using (context.PushGeometryClip(geometry))
                {
                    var fillHeight = rect.Height * fraction;
                    context.FillRectangle(
                        AxTheme.GreenBrush,
                        new Rect(rect.X, rect.Bottom - fillHeight, rect.Width, fillHeight));
                }
            }
        }

        context.DrawEllipse(null, new Pen(outline, 1), rect.Center, rect.Width / 2, rect.Height / 2);
    }
}
