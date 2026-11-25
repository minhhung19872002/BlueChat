using System.Runtime.CompilerServices;

namespace BlueChat.Media.Sdp.Tests;

/// <summary>Real browser offers from Corpus/generate.mjs (Chromium, Firefox).</summary>
public static class Corpus
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Corpus");

    public static IEnumerable<object[]> Files() =>
        Directory.GetFiles(Dir, "*.sdp").Select(f => new object[] { Path.GetFileName(f) }).OrderBy(x => (string)x[0]);

    public static string Read(string name) => File.ReadAllText(Path.Combine(Dir, name));

    /// <summary>tests/BlueChat.Media.Sdp.Tests/out (git-ignored), where answers for the browser check are written.</summary>
    public static string OutDir([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "out");
}
