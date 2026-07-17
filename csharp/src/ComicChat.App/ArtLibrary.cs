using ComicChat.Core.Art;

namespace ComicChat.App;

/// <summary>
/// Finds and loads the original Comic Chat art shipped in the repository.
/// </summary>
/// <remarks>
/// The .avb/.bgb files are read straight from the archived <c>comicart/</c> and
/// <c>artpack1/</c> folders — this clone renders the real Bolo, Anna and Kevin, not lookalikes.
/// </remarks>
public sealed class ArtLibrary
{
    public List<AvatarFile> Avatars { get; } = [];
    public List<(string name, ChatBackdrop backdrop)> Backdrops { get; } = [];

    /// <summary>Walk up from the running binary to the repo root, then find the art folders.</summary>
    public static string? FindArtDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var candidate in new[] { "v2.5-beta-1/comicart", "v2.5-beta-1/artpack1" })
            {
                var path = Path.Combine(dir.FullName, candidate);
                if (Directory.Exists(path)) return path;
            }
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Load every avatar and backdrop we can. Individual files that fail to parse are skipped
    /// rather than taking the app down — a 30-year-old art pack is allowed the odd surprise.
    /// </summary>
    public static ArtLibrary Load(string? artDir = null)
    {
        var lib = new ArtLibrary();
        artDir ??= FindArtDirectory();
        if (artDir is null || !Directory.Exists(artDir)) return lib;

        foreach (var file in Directory.EnumerateFiles(artDir, "*.avb").OrderBy(f => f))
        {
            try { lib.Avatars.Add(AvbReader.LoadAvatar(file)); }
            catch (Exception ex) { Console.Error.WriteLine($"skipped {Path.GetFileName(file)}: {ex.Message}"); }
        }

        foreach (var file in Directory.EnumerateFiles(artDir, "*.bgb").OrderBy(f => f))
        {
            try { lib.Backdrops.Add((Path.GetFileNameWithoutExtension(file), AvbReader.LoadBackdrop(file))); }
            catch (Exception ex) { Console.Error.WriteLine($"skipped {Path.GetFileName(file)}: {ex.Message}"); }
        }

        return lib;
    }
}
