using System.Runtime.CompilerServices;
using System.Text.Json;

namespace BlueChat.Media.Rtp.Tests;

/// <summary>One reference vector produced by Pion (Vectors/gen/main.go).</summary>
public sealed record PionVector(string Kind, string Name, byte[] Bytes, JsonElement Expect)
{
    public override string ToString() => $"{Kind}/{Name}";
}

internal static class Vectors
{
    private static readonly Lazy<IReadOnlyList<PionVector>> All = new(Load);

    public static IReadOnlyList<PionVector> Pion => All.Value;

    public static IEnumerable<object[]> OfKind(string kind) =>
        Pion.Where(v => v.Kind == kind).Select(v => new object[] { v.Name });

    public static PionVector Get(string kind, string name) => Pion.Single(v => v.Kind == kind && v.Name == name);

    /// <summary>Folder of this source file (to write ours.json next to the generator).</summary>
    public static string SourceFolder([CallerFilePath] string path = "") => Path.Combine(Path.GetDirectoryName(path)!, "Vectors");

    private static List<PionVector> Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "pion-vectors.json")));
        return doc.RootElement.EnumerateArray().Select(e => new PionVector(
            e.GetProperty("kind").GetString()!, e.GetProperty("name").GetString()!,
            Convert.FromHexString(e.GetProperty("hex").GetString()!), e.GetProperty("expect").Clone())).ToList();
    }

    public static int Int(this JsonElement e, string name) => e.GetProperty(name).GetInt32();

    public static long Long(this JsonElement e, string name) => e.GetProperty(name).GetInt64();

    public static bool Bool(this JsonElement e, string name) => e.GetProperty(name).GetBoolean();

    public static string Str(this JsonElement e, string name) => e.GetProperty(name).GetString()!;

    public static long[] Longs(this JsonElement e, string name) =>
        e.GetProperty(name).ValueKind == JsonValueKind.Null ? [] : e.GetProperty(name).EnumerateArray().Select(x => x.GetInt64()).ToArray();
}
