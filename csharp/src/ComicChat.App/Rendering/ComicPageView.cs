using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ComicChat.Core.Comic;
using ComicChat.Core.Geometry;
using ComicPanel = ComicChat.Core.Comic.Panel;

namespace ComicChat.App.Rendering;

/// <summary>Draws a <see cref="Body"/>'s art. Implemented over the AVB art loader.</summary>
public interface IBodyRenderer
{
    /// <summary>Draw <paramref name="body"/> into <paramref name="destRect"/> (DIPs), honouring its facing.</summary>
    void DrawBody(DrawingContext ctx, Body body, Rect destRect);
}

/// <summary>Draws a panel's backdrop, given its source-rect crop.</summary>
public interface IBackDropRenderer
{
    /// <summary>Image dimensions in pixels, or null if the backdrop is unavailable.</summary>
    (int width, int height)? GetSize(ushort backId);

    void DrawBackDrop(DrawingContext ctx, ushort backId, Rect destRect,
                      int srcLeft, int srcTop, int srcWidth, int srcHeight);
}

/// <summary>
/// The comic surface. Port of CPageView (pageview.h:7).
/// </summary>
/// <remarks>
/// Renders straight from the engine's model: each panel draws its backdrop, then its bodies,
/// then its balloons' trajectories. The view holds no layout state of its own — exactly as in
/// the original, where resizing simply re-ran the layout and replayed the history.
/// </remarks>
public sealed class ComicPageView : Control
{
    private UnitPanelPage? _page;
    private IBodyRenderer? _bodyRenderer;
    private IBackDropRenderer? _backDropRenderer;

    /// <summary>TWIPS per DIP. Divides engine coordinates down to screen units.</summary>
    public double Scale { get; set; } = AvaloniaTextMeasurer.TwipsPerDip;

    public void SetPage(UnitPanelPage page, IBodyRenderer? bodies = null, IBackDropRenderer? backdrops = null)
    {
        _page = page;
        _bodyRenderer = bodies;
        _backDropRenderer = backdrops;
        InvalidateVisual();
    }

    public void Refresh() => InvalidateVisual();

    /// <summary>Total height the laid-out comic needs, in DIPs — drives the scroll extent.</summary>
    public double ComicHeight
    {
        get
        {
            if (_page is null) return 0;
            var m = _page.Ctx.Metrics;
            int perRow = Math.Max(1, m.PanelsPerRow);
            int rows = (_page.Panels.Count + perRow - 1) / perRow;
            return (rows * (m.UnitHeight + m.HInterstice) + m.HInterstice) / Scale;
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(availableSize.Width, Math.Max(ComicHeight, 1));

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));

        if (_page is null) return;

        var m = _page.Ctx.Metrics;
        double panelW = m.UnitWidth / Scale;
        double panelH = m.UnitHeight / Scale;

        for (int i = 0; i < _page.Panels.Count; i++)
        {
            var panel = _page.Panels[i];
            var (px, py) = _page.GetPanelOrigin(i);

            // Panel origin is its top-left; the engine's y is negative-down.
            double ox = px / Scale + m.VInterstice / Scale;
            double oy = -py / Scale + m.HInterstice / Scale;
            var panelRect = new Rect(ox, oy, panelW, panelH);

            if (!panelRect.Intersects(Bounds.Inflate(panelH))) continue;

            DrawPanel(context, panel, panelRect, ox, oy);
        }
    }

    private void DrawPanel(DrawingContext context, ComicPanel panel, Rect panelRect, double ox, double oy)
    {
        using (context.PushClip(panelRect))
        {
            DrawBackDrop(context, panel, panelRect);

            foreach (var body in panel.Bodies)
            {
                var r = ToRect(body.BBox, ox, oy);
                _bodyRenderer?.DrawBody(context, body, r);
            }

            foreach (var balloon in panel.Elements)
                DrawBalloon(context, balloon, ox, oy);
        }

        if (panel.HasBorder)
        {
            var m = _page!.Ctx.Metrics;
            var pen = new Pen(Brushes.Black, 2 * m.BorderWidth / Scale);
            context.DrawRectangle(null, pen, panelRect);
        }
    }

    private void DrawBackDrop(DrawingContext context, ComicPanel panel, Rect panelRect)
    {
        var size = _backDropRenderer?.GetSize(panel.BackDrop.BackId);
        if (size is null)
        {
            context.FillRectangle(Brushes.White, panelRect);
            return;
        }

        var m = _page!.Ctx.Metrics;
        var (sl, st, sw, sh) = panel.BackDrop.GetSourceRect(
            m.UnitWidth, m.UnitHeight, size.Value.width, size.Value.height);

        _backDropRenderer!.DrawBackDrop(context, panel.BackDrop.BackId, panelRect, sl, st, sw, sh);
    }

    /// <summary>
    /// Draw one balloon: the white nimbus, the outline, then the text.
    /// </summary>
    /// <remarks>
    /// The nimbus is a fat white stroke drawn under the black outline (balloon.cpp:97), which is
    /// what lets a cloud sit legibly on top of busy backdrop art.
    /// </remarks>
    private void DrawBalloon(DrawingContext context, Balloon balloon, double ox, double oy)
    {
        balloon.SetBalloonTraj();
        if (balloon.BalloonTraj is null) return;

        // The trajectory is in balloon-local coords; offset by the balloon's origin.
        double bx = ox + balloon.BBox.Left / Scale;
        double by = oy - balloon.BBox.Top / Scale;

        var sink = new GeometrySink(Scale, bx, by);
        balloon.BalloonTraj.BuildPath(sink);
        var geometry = sink.Build();

        bool dashed = balloon is WoodringNormal { Dashed: not 0 };

        var nimbusPen = new Pen(Brushes.White, WoodringNormal.NimbusPenWidth / Scale)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = PenLineCap.Round,
        };
        context.DrawGeometry(Brushes.White, nimbusPen, geometry);

        var outlinePen = new Pen(Brushes.Black, WoodringNormal.PenWidth / Scale)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = PenLineCap.Round,
            DashStyle = dashed ? DashStyle.Dash : null,
        };
        context.DrawGeometry(null, outlinePen, geometry);

        DrawBalloonText(context, balloon, bx, by);
    }

    private void DrawBalloonText(DrawingContext context, Balloon balloon, double bx, double by)
    {
        var fInfo = balloon.FInfo;
        if (fInfo is null) return;

        var typeface = balloon.FontI.Font as Typeface? ?? new Typeface(FontFamily.Default);

        // Draw at exactly the size the balloon was measured at, converted to screen DIPs.
        // Anything else and the text will not fit the cloud that was built around it.
        double fontSizeDip = balloon.FontI.FontSize / Scale;

        for (int i = 0; i < fInfo.NLines; i++)
        {
            var text = fInfo.Starts[i];
            if (string.IsNullOrEmpty(text)) continue;

            var ft = new FormattedText(
                text, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, fontSizeDip, Brushes.Black);

            double lx = bx + fInfo.LeftX[i] / Scale;
            double ly = by + i * (balloon.FontI.LineHeight / Scale);
            context.DrawText(ft, new Avalonia.Point(lx, ly));
        }
    }

    /// <summary>Engine SRect (TWIPS, y negative-down, panel-local) to a screen Rect in DIPs.</summary>
    private Rect ToRect(SRect r, double ox, double oy) =>
        new(ox + r.Left / Scale,
            oy - r.Top / Scale,
            Math.Max(0, r.Width / Scale),
            Math.Max(0, r.Height / Scale));
}
