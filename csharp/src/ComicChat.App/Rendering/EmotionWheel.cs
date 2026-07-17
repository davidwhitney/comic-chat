using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ComicChat.Core.Avatars;

namespace ComicChat.App.Rendering;

/// <summary>
/// The emotion wheel — drag the bullseye to pose your character.
/// Port of CBodyCam (bodycam.h:8).
/// </summary>
/// <remarks>
/// This is the emotion model made literal: the drag <i>angle</i> is the emotion and the drag
/// <i>radius</i> is the intensity, which is exactly how <see cref="Emotion"/> stores them.
/// Posing by hand freezes the avatar (AF_TEMPFROZEN), and a frozen avatar bypasses the text
/// expert system entirely (textpose.cpp:119) — the user's choice always wins over the rules.
/// </remarks>
public sealed class EmotionWheel : Control
{
    /// <summary>Raised as the user drags. The pose is applied live.</summary>
    public event Action<Emotion>? EmotionChanged;

    private Emotion _current = new(0.0, Em.Neutral);
    private bool _dragging;

    public Emotion Current
    {
        get => _current;
        set { _current = value; InvalidateVisual(); }
    }

    /// <summary>The 8 spokes, in wheel order, with the labels the UI shows.</summary>
    private static readonly (float Emotion, string Label)[] Spokes =
    [
        (Em.Happy, "Happy"),
        (Em.Coy, "Coy"),
        (Em.Bored, "Bored"),
        (Em.Scared, "Scared"),
        (Em.Sad, "Sad"),
        (Em.Angry, "Angry"),
        (Em.Shout, "Shout"),
        (Em.Laugh, "Laugh"),
    ];

    private Avalonia.Point BullsEye => new(Bounds.Width / 2, Bounds.Height / 2);
    private double BullRadius => Math.Min(Bounds.Width, Bounds.Height) / 2 - 18;

    /// <summary>
    /// Port of CBodyCam::GetEmotionFromPoint (bodycam.cpp:403).
    /// </summary>
    /// <remarks>
    /// The &lt;0.2 snap-to-zero is the original's "detente in the center" — without it, resting
    /// near the middle would emit a jittery low-intensity emotion instead of clean Neutral.
    /// </remarks>
    private Emotion GetEmotionFromPoint(Avalonia.Point point)
    {
        var centre = BullsEye;
        double vx = point.X - centre.X;
        double vy = point.Y - centre.Y;

        double intensity = Math.Min(Math.Sqrt(vx * vx + vy * vy) / BullRadius, 1.0);
        if (intensity < 0.2) intensity = 0.0;

        // Screen y is down, engine angles are y-up: negate to match the wheel.
        double emotion = intensity == 0 ? 0 : Math.Atan2(-vy, vx);
        if (emotion < 0) emotion += Math.PI * 2;

        return new Emotion(intensity, emotion);
    }

    private Avalonia.Point PointFromEmotion(Emotion e)
    {
        double r = e.Intensity * BullRadius;
        return BullsEye + new Vector(Math.Cos(e.EmotionValue) * r, -Math.Sin(e.EmotionValue) * r);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _dragging = true;
        e.Pointer.Capture(this);
        Apply(e.GetPosition(this));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragging) Apply(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void Apply(Avalonia.Point p)
    {
        _current = GetEmotionFromPoint(p);
        EmotionChanged?.Invoke(_current);
        InvalidateVisual();
    }

    /// <summary>Port of CBodyCam::DrawBullsEye (bodycam.cpp).</summary>
    public override void Render(DrawingContext context)
    {
        var centre = BullsEye;
        double radius = BullRadius;
        if (radius <= 0) return;

        var ring = new Pen(Brushes.Gray, 1);
        context.DrawEllipse(Brushes.White, new Pen(Brushes.Black, 1.5), centre, radius, radius);
        context.DrawEllipse(null, ring, centre, radius * 0.66, radius * 0.66);

        // The detente: inside this ring the pose reads as Neutral.
        context.DrawEllipse(null, new Pen(Brushes.LightGray, 1) { DashStyle = DashStyle.Dash },
                            centre, radius * 0.2, radius * 0.2);

        var typeface = new Typeface(FontFamily.Default);
        foreach (var (emotion, label) in Spokes)
        {
            var edge = centre + new Vector(Math.Cos(emotion) * radius, -Math.Sin(emotion) * radius);
            context.DrawLine(ring, centre, edge);

            var ft = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture,
                                       FlowDirection.LeftToRight, typeface, 10, Brushes.Black);
            var labelPt = centre + new Vector(Math.Cos(emotion) * (radius + 10),
                                              -Math.Sin(emotion) * (radius + 10));
            context.DrawText(ft, new Avalonia.Point(labelPt.X - ft.Width / 2, labelPt.Y - ft.Height / 2));
        }

        var cursor = PointFromEmotion(_current);
        context.DrawEllipse(Brushes.Red, new Pen(Brushes.DarkRed, 1), cursor, 5, 5);
    }
}
