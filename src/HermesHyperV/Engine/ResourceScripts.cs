using System.Reflection;

namespace HermesHyperV.Engine;

/// <summary>Embedded .ps1 bodies, loaded once from the assembly manifest.
/// Files live in the project's ps1/ folder, embedded as
/// "HermesHyperV.ps1.&lt;name&gt;.ps1".</summary>
public static class ResourceScripts
{
    static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string Get(string name)
    {
        if (Cache.TryGetValue(name, out var hit)) return hit;
        var loaded = Load(name);
        Cache[name] = loaded;
        return loaded;
    }

    static string Load(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var key = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($".{name}.ps1", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"embedded script '{name}.ps1' not found. Have: {string.Join(",", asm.GetManifestResourceNames())}");
        using var stream = asm.GetManifestResourceStream(key)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<string> Names =>
        Assembly.GetExecutingAssembly().GetManifestResourceNames()
            .Where(n => n.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            .ToList();
}
