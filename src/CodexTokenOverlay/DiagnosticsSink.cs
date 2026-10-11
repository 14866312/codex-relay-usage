using System.Collections.Concurrent;
using System.Text.Json;

namespace CodexTokenOverlay;

/// <summary>
/// In-memory observability for deliberately swallowed exceptions. The overlay must
/// keep working when a log directory is transiently unreadable, so those sites stay
/// silent in normal operation. This sink records what was ignored so a diagnostics
/// run can distinguish "nothing happened" from "the watcher failed to start". It
/// never writes to disk on its own and contains no conversation content.
/// </summary>
internal static class DiagnosticsSink
{
    private static readonly ConcurrentDictionary<string, int> Counts = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Record(string site, Exception? error = null)
    {
        var key = error is null ? site : site + ": " + error.GetType().Name;
        Counts.AddOrUpdate(key, 1, static (_, value) => value + 1);
    }

    public static IReadOnlyDictionary<string, int> Snapshot() =>
        new Dictionary<string, int>(Counts, StringComparer.Ordinal);

    public static void Clear() => Counts.Clear();

    public static string ToJson() => JsonSerializer.Serialize(Snapshot(), JsonOptions);
}
