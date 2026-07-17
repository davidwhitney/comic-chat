using Avalonia;
using Avalonia.Media;
using ComicChat.Core.Geometry;
using CorePoint = ComicChat.Core.Geometry.Point;

namespace ComicChat.App.Rendering;

/// <summary>
/// Bridges the engine's <see cref="Traj"/> geometry onto an Avalonia
/// <see cref="StreamGeometry"/>.
/// </summary>
/// <remarks>
/// This is the direct replacement for the MFC path calls the original drew into
/// (BeginPath / MoveTo / PolyBezierTo / CloseFigure / EndPath, traj.cpp:45). The engine emits
/// exactly the same sequence; only the sink changed.
///
/// Coordinates arrive in the engine's TWIPS with y negative-down, and are converted to
/// Avalonia's y-down DIPs here — the single place that conversion happens.
/// </remarks>
public sealed class GeometrySink : IPathSink, IDisposable
{
    private readonly StreamGeometry _geometry = new();
    private readonly StreamGeometryContext _ctx;
    private readonly double _scale;
    private readonly double _originX;
    private readonly double _originY;
    private bool _figureOpen;
    private bool _disposed;

    /// <param name="scale">TWIPS to DIP divisor (see <see cref="AvaloniaTextMeasurer.TwipsPerDip"/>).</param>
    /// <param name="originX">Panel origin x in DIPs.</param>
    /// <param name="originY">Panel origin y in DIPs (the panel's top edge).</param>
    public GeometrySink(double scale, double originX, double originY)
    {
        _scale = scale;
        _originX = originX;
        _originY = originY;
        _ctx = _geometry.Open();
    }

    /// <summary>TWIPS (y negative-down, panel-local) to DIPs (y down, page-absolute).</summary>
    private Avalonia.Point Map(CorePoint p) =>
        new(_originX + p.X / _scale, _originY - p.Y / _scale);

    public void MoveTo(CorePoint p)
    {
        if (_figureOpen) _ctx.EndFigure(false);
        _ctx.BeginFigure(Map(p), isFilled: true);
        _figureOpen = true;
    }

    public void LineTo(CorePoint p)
    {
        if (!_figureOpen) MoveTo(p);
        else _ctx.LineTo(Map(p));
    }

    public void CubicTo(CorePoint c1, CorePoint c2, CorePoint end)
    {
        if (!_figureOpen) MoveTo(c1);
        _ctx.CubicBezierTo(Map(c1), Map(c2), Map(end));
    }

    public void Close()
    {
        if (!_figureOpen) return;
        _ctx.EndFigure(true);
        _figureOpen = false;
    }

    /// <summary>Finish the geometry. The sink must not be used afterwards.</summary>
    public StreamGeometry Build()
    {
        if (_figureOpen)
        {
            _ctx.EndFigure(false);
            _figureOpen = false;
        }
        Dispose();
        return _geometry;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _ctx.Dispose();
        _disposed = true;
    }
}
