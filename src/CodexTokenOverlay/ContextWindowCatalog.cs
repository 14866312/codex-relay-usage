using System.Text;
using System.Text.Json;

namespace CodexTokenOverlay;

// One row of the Codex model directory: the configured windows plus the ratio the
// client reserves before reporting the effective window.
internal sealed record ContextWindowEntry(long? ContextWindow, long? MaxContextWindow, double? EffectivePercent, string Source);

// The resolved context ceiling for one snapshot: the window that divides the latest
// call estimate, next to the configured window that explains it.
internal sealed record ContextWindowResolution(long? EffectiveWindow, string? EffectiveSource,
    long? ConfiguredWindow, string? ConfiguredSource, bool ConfiguredReference);

internal static class ContextWindowRules
{
    internal const string LogSource = "日志";
    internal const string CatalogSource = "目录";
    internal const string EmbeddedSource = "内置映射";
    // Codex reserves part of the configured window for its own bookkeeping; a cache
    // without the field falls back to the documented default.
    internal const double DefaultEffectivePercent = 95;

    // The log window is already the effective one, so it is never scaled again. The
    // directory only supplies a ceiling the log omits, and is marked a reference when
    // its own derivation disagrees with what the log reported.
    public static ContextWindowResolution Resolve(string? model, long? logWindow,
        IReadOnlyDictionary<string, ContextWindowEntry>? catalog,
        IReadOnlyDictionary<string, ContextWindowEntry>? embedded = null)
    {
        var entry = Find(model, catalog);
        var configured = Window(entry);
        if (configured is null)
        {
            // A directory row without a usable window steps aside for the embedded table.
            entry = Find(model, embedded); configured = Window(entry);
        }
        var logged = logWindow is > 0 ? logWindow : null;
        if (logged is { } log)
        {
            // The log value already includes Codex's reserve; only a derivation that lands
            // elsewhere marks the directory value a reference.
            var reference = entry is not null && configured is { } catalogWindow
                && Derive(catalogWindow, entry.EffectivePercent) != log;
            return new(log, LogSource, configured, entry?.Source, reference);
        }
        if (entry is not null && configured is { } window)
            return new(Derive(window, entry.EffectivePercent), entry.Source, window, entry.Source, false);
        return new(null, null, null, null, false);
    }

    internal static long Derive(long window, double? percent)
    {
        var effective = percent is > 0 and <= 100 ? percent.Value : DefaultEffectivePercent;
        return (long)Math.Floor(window * effective / 100d);
    }

    private static ContextWindowEntry? Find(string? model, IReadOnlyDictionary<string, ContextWindowEntry>? lookup) =>
        !string.IsNullOrWhiteSpace(model) && lookup is not null && lookup.TryGetValue(model, out var entry) ? entry : null;

    // Prefer the reported context window; an unusable column falls to the other one.
    private static long? Window(ContextWindowEntry? entry) => entry switch
    {
        null => null,
        { ContextWindow: > 0 } => entry.ContextWindow,
        { MaxContextWindow: > 0 } => entry.MaxContextWindow,
        _ => null
    };
}

// Last-resort directory values, verified 2026-10-12 against the Codex model cache of
// the development machine (%USERPROFILE%\.codex\models_cache.json). They answer only
// when that file is missing; relay prefixes match as exact slugs, never as aliases, and
// an unverified model keeps “unknown ceiling” instead of a guessed window.
internal static class EmbeddedModelWindows
{
    internal static readonly IReadOnlyDictionary<string, ContextWindowEntry> Entries =
        new Dictionary<string, ContextWindowEntry>(StringComparer.Ordinal)
        {
            ["gpt-6-astra"] = new(272_000, 272_000, 95, ContextWindowRules.EmbeddedSource),
            ["gpt-6-sol"] = new(272_000, 272_000, 95, ContextWindowRules.EmbeddedSource),
            ["gpt-6-luna"] = new(272_000, 272_000, 95, ContextWindowRules.EmbeddedSource),
            ["gpt-6.1-sol"] = new(272_000, 272_000, 95, ContextWindowRules.EmbeddedSource),
            ["gpt-5.6-sol"] = new(272_000, 272_000, 95, ContextWindowRules.EmbeddedSource),
            ["gpt-5.6-terra"] = new(272_000, 272_000, 95, ContextWindowRules.EmbeddedSource),
            ["gpt-5.6-luna"] = new(272_000, 272_000, 95, ContextWindowRules.EmbeddedSource),
            ["gpt-5.5"] = new(272_000, 272_000, 95, ContextWindowRules.EmbeddedSource),
            ["61sol/gpt-6.1-sol"] = new(128_000, 128_000, 95, ContextWindowRules.EmbeddedSource),
            ["cn/deepseek-v4.1-flash"] = new(1_000_000, 1_000_000, 95, ContextWindowRules.EmbeddedSource),
            ["global/deepseek-v4.1-flash"] = new(1_000_000, 1_000_000, 95, ContextWindowRules.EmbeddedSource),
            ["gem/gemini-3.8-flash-high"] = new(128_000, 128_000, 95, ContextWindowRules.EmbeddedSource),
            ["codex-auto-review"] = new(272_000, 872_000, 95, ContextWindowRules.EmbeddedSource)
        };
}

// Reads models_cache.json next to the sessions directory. Parsed entries are reused
// while the file keeps its length and write time, so the one-second poll never
// re-parses the whole directory for an unchanged file.
internal sealed class ContextWindowCatalog
{
    private readonly object _sync = new();
    private bool _hasSnapshot;
    private long _length = -1;
    private DateTime _writeUtc;
    private Dictionary<string, ContextWindowEntry> _entries = new(StringComparer.Ordinal);
    public string? Path { get; }
    public ContextWindowCatalog(string sessionRoot) => Path = PathFor(sessionRoot);

    internal static string? PathFor(string sessionRoot)
    {
        if (string.IsNullOrWhiteSpace(sessionRoot)) return null;
        var parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(sessionRoot));
        return parent is null ? null : System.IO.Path.Combine(parent, "models_cache.json");
    }

    public ContextWindowResolution Resolve(string? model, long? logWindow) =>
        ContextWindowRules.Resolve(model, logWindow, Load(), EmbeddedModelWindows.Entries);

    public IReadOnlyDictionary<string, ContextWindowEntry> Load()
    {
        lock (_sync)
        {
            if (Path is null) return _entries;
            try
            {
                var info = new FileInfo(Path);
                if (!info.Exists) { Reset(); return _entries; }
                if (_hasSnapshot && info.Length == _length && info.LastWriteTimeUtc == _writeUtc) return _entries;
                _entries = Parse(File.ReadAllText(Path, Encoding.UTF8));
                _hasSnapshot = true; _length = info.Length; _writeUtc = info.LastWriteTimeUtc;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Keep the last accepted directory; the next poll retries the file.
            }
            return _entries;
        }
    }

    private void Reset()
    {
        _hasSnapshot = false; _length = -1; _writeUtc = default;
        _entries = new(StringComparer.Ordinal);
    }

    internal static Dictionary<string, ContextWindowEntry> Parse(string json)
    {
        var entries = new Dictionary<string, ContextWindowEntry>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array) return entries;
        foreach (var model in models.EnumerateArray())
        {
            if (model.ValueKind != JsonValueKind.Object) continue;
            var slug = model.TryGetProperty("slug", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (string.IsNullOrWhiteSpace(slug)) continue;
            entries[slug] = new(Number(model, "context_window"), Number(model, "max_context_window"),
                Fraction(model, "effective_context_window_percent"), ContextWindowRules.CatalogSource);
        }
        return entries;
    }

    private static long? Number(JsonElement model, string name) =>
        model.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number : null;

    private static double? Fraction(JsonElement model, string name) =>
        model.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number : null;
}
