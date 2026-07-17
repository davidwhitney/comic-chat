namespace ComicChat.Tests;

/// <summary>
/// Locates the original Comic Chat art shipped alongside the C++ reference.
/// </summary>
/// <remarks>
/// Walks up from the test assembly rather than trusting the working directory, so the suite behaves
/// the same from the repo root, from the test project, and from an IDE runner.
/// </remarks>
public static class ArtPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string ComicArt => Path.Combine(RepoRoot, "v2.5-beta-1", "comicart");

    public static string ArtPack1 => Path.Combine(RepoRoot, "v2.5-beta-1", "artpack1");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "v2.5-beta-1", "comicart")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the repo root (a directory containing v2.5-beta-1/comicart) above {AppContext.BaseDirectory}.");
    }

    /// <summary>Every shipped avatar file, as xunit member data.</summary>
    public static IEnumerable<object[]> AllAvatars() => AllWithExtension("*.avb");

    /// <summary>Every shipped backdrop file, as xunit member data.</summary>
    public static IEnumerable<object[]> AllBackdrops() => AllWithExtension("*.bgb");

    private static IEnumerable<object[]> AllWithExtension(string pattern) =>
        new[] { ComicArt, ArtPack1 }
            .SelectMany(d => Directory.EnumerateFiles(d, pattern))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => new object[] { f });
}
