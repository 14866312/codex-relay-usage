using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexTokenOverlay;

internal enum PriceEntryMode { BaseWithMultiplier, EffectivePrices }
internal sealed record TokenPrices(decimal? Input = null, decimal? CacheRead = null, decimal? CacheWrite = null, decimal? Output = null);
internal sealed record ModelPriceProfile(string Id, string Name, IReadOnlyList<string> Models,
    PriceEntryMode Mode, decimal Multiplier, bool TwoTiers, long Threshold, TokenPrices Low, TokenPrices High);
internal sealed record PricingSettings(int Version, IReadOnlyList<ModelPriceProfile> Profiles)
{
    public const int CurrentVersion = 1;
    public static PricingSettings Empty => new(CurrentVersion, Array.Empty<ModelPriceProfile>());
    public ModelPriceProfile? ForModel(string model) => Profiles.FirstOrDefault(p => p.Models.Contains(model, StringComparer.Ordinal));
    public string? ValidationIssue()
    {
        if (Version != CurrentVersion) return "价格配置版本不支持";
        if (Profiles is null) return "价格方案未提供";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in Profiles)
        {
            if (p is null || string.IsNullOrWhiteSpace(p.Id) || !ids.Add(p.Id) || string.IsNullOrWhiteSpace(p.Name) || p.Name.Any(char.IsControl)) return "方案名称或标识无效";
            if (!Enum.IsDefined(p.Mode) || p.Multiplier < 0 || p.Threshold < 0 || p.Low is null || p.High is null || p.Models is null) return "计费模式、倍率或阈值无效";
            foreach (var model in p.Models)
                if (string.IsNullOrWhiteSpace(model) || model != model.Trim() || model.Any(char.IsControl) || !aliases.Add(model)) return "模型别名为空、无效或重复绑定";
            foreach (var rates in new[] { p.Low, p.High })
                if (new[] { rates.Input, rates.CacheRead, rates.CacheWrite, rates.Output }.Any(v => v < 0)) return "单价不能为负数";
        }
        return null;
    }
}
internal sealed record PricingRevision(long Version, PricingSettings Settings);
internal sealed record PricingLoadResult(PricingSettings Settings, string? Error);
internal static class PricingStore
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexRelayUsage", "prices.json");
    private static readonly JsonSerializerOptions Options = new()
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static PricingLoadResult Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new(PricingSettings.Empty, null);
            var settings = JsonSerializer.Deserialize<PricingSettings>(File.ReadAllText(path, Encoding.UTF8), Options);
            var error = settings?.ValidationIssue() ?? (settings is null ? "价格配置为空" : null);
            return error is null ? new(settings!, null) : new(PricingSettings.Empty, error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { return new(PricingSettings.Empty, "无法读取价格配置：" + e.Message); }
    }
    public static bool TrySave(PricingSettings settings, out string? error, string? path = null)
    {
        error = settings.ValidationIssue(); if (error is not null) return false;
        path ??= DefaultPath;
        string? temporary = null;
        try
        {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, Options); stream.Flush(flushToDisk: true);
            }
            // Same-volume rename replaces the complete file, never a partially written configuration.
            File.Move(temporary, path, overwrite: true); temporary = null; return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { error = "保存失败，原价格未更改：" + e.Message; return false; }
        finally { if (temporary is not null) { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
    }
}
