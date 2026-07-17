using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ComicChat.Core.Art;
using ComicChat.Core.Comic;

namespace ComicChat.App.Rendering;

/// <summary>
/// Turns the art loader's decoded BGRA buffers into Avalonia bitmaps, cached per pose.
/// </summary>
/// <remarks>
/// Core deliberately hands back plain <c>byte[]</c> rather than any UI bitmap type, so this
/// is the only place that knows about Avalonia imaging. Decoding is expensive and poses repeat
/// constantly across panels, hence the cache — the original had the same idea in cache.cpp,
/// though that file never compiled and was dead code.
/// </remarks>
public sealed class ArtBitmapCache
{
    private readonly Dictionary<object, WriteableBitmap?> _cache = [];

    public WriteableBitmap? GetBitmap(Pose? pose)
    {
        if (pose is null || pose.Width == 0 || pose.Height == 0) return null;
        if (_cache.TryGetValue(pose, out var cached)) return cached;

        var bmp = FromBgra(pose.Bgra, pose.Width, pose.Height);
        _cache[pose] = bmp;
        return bmp;
    }

    public WriteableBitmap? GetBitmap(ChatBackdrop backdrop)
    {
        if (_cache.TryGetValue(backdrop, out var cached)) return cached;

        var bmp = FromBgra(backdrop.Bgra, backdrop.Width, backdrop.Height);
        _cache[backdrop] = bmp;
        return bmp;
    }

    /// <summary>Wrap a top-down 32-bit BGRA buffer as a bitmap.</summary>
    private static WriteableBitmap? FromBgra(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return null;

        var bmp = new WriteableBitmap(
            new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);

        using var fb = bmp.Lock();
        int stride = width * 4;
        unsafe
        {
            var dst = (byte*)fb.Address;
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    bgra, y * stride, (IntPtr)(dst + y * fb.RowBytes), stride);
        }

        return bmp;
    }
}

/// <summary>Draws avatar bodies from loaded .avb art.</summary>
public sealed class AvatarBodyRenderer(ArtBitmapCache cache) : IBodyRenderer
{
    /// <summary>
    /// Draw a body, anchoring the head onto the torso for complex avatars.
    /// Port of CBodyDouble::GetBodyBox + DrawBody (bodycam.cpp:606, avatar.cpp).
    /// </summary>
    /// <remarks>
    /// Head and torso are positioned within the composite bounding box by the same offsets
    /// GetDimInfo used, so what is drawn matches exactly what the layout engine measured.
    /// The torso is drawn first and the head over it, because the head overlaps the neck.
    ///
    /// Facing is a horizontal mirror about the body's centre — the original flipped the DIB
    /// blit. Mirroring the whole composite keeps head and torso registered to each other.
    /// </remarks>
    public void DrawBody(DrawingContext ctx, Body body, Rect destRect)
    {
        if (body is not AvatarBody ab) return;
        if (destRect.Width <= 0 || destRect.Height <= 0) return;

        var torso = cache.GetBitmap(ab.TorsoPose);
        var face = cache.GetBitmap(ab.FacePose);
        if (torso is null && face is null) return;

        var (left, top, right, bottom) = ab.CompositeRect;
        double bitW = right - left, bitH = bottom - top;
        if (bitW <= 0 || bitH <= 0) return;

        // The engine already chose the destination size; scale the composite into it.
        double scaleX = destRect.Width / bitW;
        double scaleY = destRect.Height / bitH;

        using var _ = ab.Flip
            ? ctx.PushTransform(
                Matrix.CreateTranslation(-destRect.Center.X, 0) *
                Matrix.CreateScale(-1, 1) *
                Matrix.CreateTranslation(destRect.Center.X, 0))
            : default;

        if (torso is not null)
        {
            var r = new Rect(
                destRect.X + (0 - left) * scaleX,
                destRect.Y + (0 - top) * scaleY,
                torso.PixelSize.Width * scaleX,
                torso.PixelSize.Height * scaleY);
            ctx.DrawImage(torso, r);
        }

        if (face is not null)
        {
            var (ox, oy) = ab.HeadOffset;
            var r = new Rect(
                destRect.X + (ox - left) * scaleX,
                destRect.Y + (oy - top) * scaleY,
                face.PixelSize.Width * scaleX,
                face.PixelSize.Height * scaleY);
            ctx.DrawImage(face, r);
        }
    }
}

/// <summary>
/// Draws panel backdrops, honouring the engine's source-rect crop.
/// </summary>
/// <remarks>
/// The crop is the camera: <see cref="BackDrop.GetSourceRect"/> returns a smaller source
/// rect when the panel is zoomed in, and stretching it over the panel is what makes the
/// background move with the characters (backdrop.cpp:341).
/// </remarks>
public sealed class BackDropRenderer(ArtBitmapCache cache) : IBackDropRenderer
{
    private readonly Dictionary<ushort, ChatBackdrop> _backdrops = [];

    public void Register(ushort id, ChatBackdrop backdrop) => _backdrops[id] = backdrop;

    public (int width, int height)? GetSize(ushort backId) =>
        _backdrops.TryGetValue(backId, out var b) ? (b.Width, b.Height) : null;

    public void DrawBackDrop(DrawingContext ctx, ushort backId, Rect destRect,
                             int srcLeft, int srcTop, int srcWidth, int srcHeight)
    {
        if (!_backdrops.TryGetValue(backId, out var backdrop)) return;

        var bmp = cache.GetBitmap(backdrop);
        if (bmp is null) return;

        // Clamp the crop to the image; a zoomed panel can ask for a rect past the edge.
        srcLeft = Math.Clamp(srcLeft, 0, Math.Max(0, backdrop.Width - 1));
        srcTop = Math.Clamp(srcTop, 0, Math.Max(0, backdrop.Height - 1));
        srcWidth = Math.Clamp(srcWidth, 1, backdrop.Width - srcLeft);
        srcHeight = Math.Clamp(srcHeight, 1, backdrop.Height - srcTop);

        ctx.DrawImage(bmp, new Rect(srcLeft, srcTop, srcWidth, srcHeight), destRect);
    }
}
