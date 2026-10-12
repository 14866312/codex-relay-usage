using System.Diagnostics;

namespace CodexTokenOverlay;

// The strip clears numbers the moment the page identity is lost, but the wording
// waits: a one-to-two second accessibility blip must not flash a status the user
// cannot act on. The stage is presentation-only state, so it never bumps the
// session selection generation.
internal enum PageWaitStage { None, Reading, NotReady, LongWait }

internal static class PageWaitRules
{
    // Three seconds covers the observed one-to-two second blips; a minute of waiting
    // is long enough that trying a restart is worth suggesting.
    public static readonly TimeSpan NotReadyAfter = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan RestartHintAfter = TimeSpan.FromSeconds(60);

    public static PageWaitStage Stage(ActiveThreadRouteStatus route, long now)
    {
        if (route.PagePendingSince == 0) return PageWaitStage.None;
        var elapsed = Stopwatch.GetElapsedTime(route.PagePendingSince, now);
        return elapsed >= RestartHintAfter ? PageWaitStage.LongWait
            : elapsed >= NotReadyAfter ? PageWaitStage.NotReady : PageWaitStage.Reading;
    }

    public static string? Text(PageWaitStage stage) => stage switch
    {
        PageWaitStage.Reading => "正在读取当前对话…",
        PageWaitStage.NotReady => "当前页面尚未就绪",
        PageWaitStage.LongWait => "等待较久，可尝试重启 Codex",
        _ => null
    };
}
