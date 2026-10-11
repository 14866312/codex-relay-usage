namespace CodexTokenOverlay;

// Codex surfaces its Chromium document before the lazily built accessibility tree
// fills in the URL, name and children; a fresh window can therefore expose one empty
// document node for a while. These checks pin that pending state and the transition
// to a ready document on a synthetic offscreen window, so the regression never
// depends on a personal Codex session.
internal static class PageReadinessTests
{
    internal static void Run(Action<string, bool> check, string directory)
    {
        check("a page whose route is not exposed yet is pending, not ambiguous",
            CodexViewIdentityReader.DocumentReadError(0, 1) == "当前页面尚未就绪" &&
            CodexViewIdentityReader.DocumentReadError(0, 0) == "无法唯一识别当前页面" &&
            CodexViewIdentityReader.DocumentReadError(2, 1) == "无法唯一识别当前页面");

        using var host = new SyntheticPageForm { Bounds = new(-20_000, -20_000, 240, 120) };
        var document = new SyntheticDocumentControl { Dock = DockStyle.Fill, DocumentName = "合成会话" };
        host.Controls.Add(document);
        host.Show();
        Application.DoEvents();
        try
        {
            var pending = CodexViewIdentityReader.ReadWindow(host.Handle);
            check("unpopulated accessibility document reports the pending page",
                pending is { WindowCount: 1, Error: "当前页面尚未就绪", Title: null, ThreadId: null });

            document.DocumentValue = "app://-/threads/11111111-2222-4333-8444-555555555555";
            Application.DoEvents();
            var ready = CodexViewIdentityReader.ReadWindow(host.Handle);
            check("populated accessibility document exposes title and route",
                ready is { WindowCount: 1, Error: null, Title: "合成会话" } &&
                ready.ThreadId == "11111111-2222-4333-8444-555555555555");
        }
        finally { host.Hide(); }

        var root = Path.Combine(directory, "page-readiness", "sessions");
        Directory.CreateDirectory(root);
        using var monitor = new CodexVisibleThreadMonitor(root, () => new(1, null, Error: "当前页面尚未就绪"));
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (monitor.GetStatus().LastError != "当前页面尚未就绪" && DateTime.UtcNow < deadline) Thread.Sleep(5);
        var error = monitor.IdentifyVisibleAsync().GetAwaiter().GetResult();
        check("identify on a pending page names the page state instead of the sidebar",
            error == "当前页面尚未就绪，请稍后重新点击识别" && monitor.GetStatus().ThreadId is null);
    }

    private sealed class SyntheticPageForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    private sealed class SyntheticDocumentControl : Control
    {
        internal string DocumentName = "";
        internal string DocumentValue = "";
        protected override AccessibleObject CreateAccessibilityInstance() => new SyntheticDocumentAccessible(this);
        private sealed class SyntheticDocumentAccessible(SyntheticDocumentControl owner) : ControlAccessibleObject(owner)
        {
            public override string? Name { get => owner.DocumentName; set { } }
            public override string? Value { get => owner.DocumentValue; set => owner.DocumentValue = value ?? ""; }
            public override AccessibleRole Role => AccessibleRole.Document;
        }
    }
}
