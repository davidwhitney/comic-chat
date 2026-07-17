using ComicChat.Core.Art;
using ComicChat.Core.Avatars;

namespace ComicChat.Tests;

/// <summary>
/// Loader tests against the original shipped art. Everything asserted here was read off the bytes on
/// disk, not from the spec — the files are the specification of record.
/// </summary>
public class AvbReaderTests
{
    private static string ComicArt(string file) => Path.Combine(ArtPaths.ComicArt, file);

    private static string ArtPack1(string file) => Path.Combine(ArtPaths.ArtPack1, file);

    // ========================================================================
    // Header and metadata

    [Fact]
    public void BoloHeaderAndMetadata()
    {
        var avatar = AvbReader.LoadAvatar(ComicArt("bolo.avb"));

        Assert.Equal(0x8181, avatar.MagicNumber);
        Assert.Equal(AvatarFileType.Complex, avatar.Type);
        Assert.Equal(2, avatar.Version);
        Assert.Equal("Bolo", avatar.Name);
        Assert.Equal(1, avatar.Style);
        Assert.Equal(AvatarFlags.HeadMask | AvatarFlags.TorsoFirst, avatar.Flags);
        Assert.Equal(5, (byte)avatar.Flags);

        var complex = Assert.IsType<AvatarComplex>(avatar);
        Assert.Equal(12, complex.Faces.Count);

        Assert.NotNull(avatar.Copyright);
        Assert.Contains("Jim Woodring", avatar.Copyright);
    }

    /// <summary>
    /// The © in the copyright notice is a bare 0xA9 — proof the ANSI decode is not ASCII or UTF-8.
    /// </summary>
    [Fact]
    public void CopyrightDecodesWindows1252()
    {
        var avatar = AvbReader.LoadAvatar(ComicArt("bolo.avb"));

        Assert.StartsWith("Copyright © 1996, 1997, 1998 Microsoft Corporation", avatar.Copyright);
    }

    [Fact]
    public void GlendaIsSimple()
    {
        var avatar = AvbReader.LoadAvatar(ComicArt("glenda.avb"));

        Assert.Equal(AvatarFileType.Simple, avatar.Type);
        var simple = Assert.IsType<AvatarSimple>(avatar);
        Assert.NotEmpty(simple.Bodies);
    }

    // ========================================================================
    // Offset adjustment

    [Fact]
    public void ComicArtBoloCarriesOffsetAdjustment()
    {
        var avatar = AvbReader.LoadAvatar(ComicArt("bolo.avb"));

        Assert.Equal(0x4D, avatar.OffsetAdjustment);
    }

    [Fact]
    public void ArtPack1BoloHasNoOffsetAdjustment()
    {
        var avatar = AvbReader.LoadAvatar(ArtPack1("bolo.avb"));

        Assert.Equal(0, avatar.OffsetAdjustment);
        Assert.NotEmpty(avatar.Poses);
    }

    /// <summary>
    /// The two Bolos are the same drawing published twice; only the resource offsets shift. If the
    /// adjustment were being dropped or double-applied, the pose bitmaps would not agree.
    /// </summary>
    [Fact]
    public void BothBolosDecodeToTheSamePixels()
    {
        var withAdjustment = AvbReader.LoadAvatar(ComicArt("bolo.avb"));
        var without = AvbReader.LoadAvatar(ArtPack1("bolo.avb"));

        Assert.Equal(withAdjustment.Poses.Count, without.Poses.Count);
        Assert.Equal(withAdjustment.Poses[0].Bgra, without.Poses[0].Bgra);
    }

    // ========================================================================
    // Pose records and the ditto rule

    [Fact]
    public void BoloFaceRecordZero()
    {
        var avatar = (AvatarComplex)AvbReader.LoadAvatar(ComicArt("bolo.avb"));
        var face = avatar.Faces[0];

        Assert.Equal(9, face.EmotionIndex);
        Assert.Equal(Em.EmotionToFloat(9), face.Emotion);
        Assert.Equal(0.0f, face.Intensity);
        Assert.Equal(97, face.Cx);
        Assert.Equal(128, face.Cy);
        Assert.Equal(2, face.CxDelta);
        Assert.Equal(-5, face.CyDelta);
        Assert.Equal(105, face.FaceX);
        Assert.Equal(97, face.FaceY);
    }

    /// <summary>
    /// Face 1 (happy, intensity 0x33) points at the same image offset as face 0, so it must reuse
    /// face 0's pose rather than mint a second one off identical bytes.
    /// </summary>
    [Fact]
    public void BoloFaceRecordOneDittosRecordZero()
    {
        var avatar = (AvatarComplex)AvbReader.LoadAvatar(ComicArt("bolo.avb"));

        var face0 = avatar.Faces[0];
        var face1 = avatar.Faces[1];

        Assert.Equal(1, face1.EmotionIndex);
        Assert.Equal(0x33 / 255.0f, face1.Intensity);

        Assert.Equal(face0.PoseId, face1.PoseId);
        Assert.NotEqual(AvbConstants.InvalidPoseId, face1.PoseId);

        // A ditto shares the pose object outright — not a copy of it.
        Assert.Same(avatar.GetPose(face0.PoseId), avatar.GetPose(face1.PoseId));
    }

    [Fact]
    public void PoseIdsAreOneBasedAndResolve()
    {
        var avatar = (AvatarComplex)AvbReader.LoadAvatar(ComicArt("bolo.avb"));

        Assert.Null(avatar.GetPose(AvbConstants.InvalidPoseId));

        foreach (var face in avatar.Faces)
        {
            Assert.InRange(face.PoseId, 1, avatar.Poses.Count);
            Assert.Same(avatar.Poses[face.PoseId - 1], avatar.GetPose(face.PoseId));
        }
    }

    // ========================================================================
    // DIBStorageWidth

    [Theory]
    [InlineData(315, 4, 160)]
    [InlineData(1, 1, 4)]
    [InlineData(8, 1, 4)]
    [InlineData(9, 1, 4)]
    [InlineData(32, 8, 32)]
    public void DibStorageWidthMatchesOriginal(int width, int bitCount, int expected)
    {
        Assert.Equal(expected, ArtDib.StorageWidth(width, bitCount));
    }

    // ========================================================================
    // Backdrops

    [Fact]
    public void RoomBackdrop()
    {
        var backdrop = AvbReader.LoadBackdrop(ComicArt("room.bgb"));

        Assert.Equal(315, backdrop.Width);
        Assert.Equal(315, backdrop.Height);
        Assert.Equal(4, backdrop.Dib.BitCount);

        // The row padding rule and the file's own biSizeImage have to agree, or the loader would
        // have rejected the decompressed payload.
        Assert.Equal(160, ArtDib.StorageWidth(315, 4));
        Assert.Equal(50400, 160 * 315);
        Assert.Equal(50400, backdrop.Dib.Bits.Length);

        // Backdrops are opaque: every pixel comes back with full alpha.
        var bgra = backdrop.Bgra;
        Assert.Equal(315 * 315 * 4, bgra.Length);
        for (int i = 3; i < bgra.Length; i += 4)
            Assert.Equal(255, bgra[i]);
    }

    // ========================================================================
    // The real proof: every shipped file

    [Theory]
    [MemberData(nameof(ArtPaths.AllAvatars), MemberType = typeof(ArtPaths))]
    public void EveryShippedAvatarLoadsAndDecodes(string path)
    {
        var avatar = AvbReader.LoadAvatar(path);

        Assert.Contains(avatar.Type, new[] { AvatarFileType.Simple, AvatarFileType.Complex });
        Assert.NotEmpty(avatar.Poses);

        foreach (var pose in avatar.Poses)
        {
            Assert.NotNull(pose.Image);
            Assert.True(pose.Width > 0, $"{path}: pose has zero width");
            Assert.True(pose.Height > 0, $"{path}: pose has zero height");

            var bgra = pose.Bgra;
            Assert.Equal(pose.Width * pose.Height * 4, bgra.Length);

            // A pose that decoded to nothing would still be the right length, so check it actually
            // carries something visible.
            Assert.Contains(bgra, b => b != 0);
        }

        switch (avatar)
        {
            case AvatarComplex complex:
                Assert.NotEmpty(complex.Faces);
                Assert.NotEmpty(complex.Torsos);
                break;
            case AvatarSimple simple:
                Assert.NotEmpty(simple.Bodies);
                break;
        }
    }

    [Theory]
    [MemberData(nameof(ArtPaths.AllBackdrops), MemberType = typeof(ArtPaths))]
    public void EveryShippedBackdropLoadsAndDecodes(string path)
    {
        var backdrop = AvbReader.LoadBackdrop(path);

        Assert.True(backdrop.Width > 0, $"{path}: zero width");
        Assert.True(backdrop.Height > 0, $"{path}: zero height");

        var bgra = backdrop.Bgra;
        Assert.Equal(backdrop.Width * backdrop.Height * 4, bgra.Length);
        Assert.Contains(bgra, b => b != 0);
    }

    /// <summary>
    /// Every mask that survives the 2bpp expansion must be a 1bpp plane the same size as its image,
    /// otherwise <see cref="ArtDib.ToBgra"/> would be sampling the wrong pixels for alpha.
    /// </summary>
    [Theory]
    [MemberData(nameof(ArtPaths.AllAvatars), MemberType = typeof(ArtPaths))]
    public void MasksExpandToMatchingMonochromePlanes(string path)
    {
        var avatar = AvbReader.LoadAvatar(path);

        foreach (var pose in avatar.Poses)
        {
            if (pose.Mask is not { } mask)
                continue;

            Assert.Equal(1, mask.BitCount);
            Assert.Equal(pose.Width, mask.Width);
            Assert.Equal(pose.Height, mask.Height);

            if (pose.Aura is { } aura)
            {
                Assert.Equal(1, aura.BitCount);
                Assert.Equal(pose.Width, aura.Width);
                Assert.Equal(pose.Height, aura.Height);
            }
        }
    }

    /// <summary>
    /// The masked poses have to actually key something out, or the mask plumbing could be silently
    /// returning "all opaque" and every other assertion here would still pass.
    /// </summary>
    [Fact]
    public void MaskedPosesProduceTransparentPixels()
    {
        var avatar = AvbReader.LoadAvatar(ComicArt("bolo.avb"));
        var pose = avatar.Poses.First(p => p.Mask is not null);

        var bgra = pose.Bgra;
        var alphas = Enumerable.Range(0, pose.Width * pose.Height).Select(i => bgra[i * 4 + 3]).ToList();

        Assert.Contains((byte)0, alphas);
        Assert.Contains((byte)255, alphas);
    }
}
