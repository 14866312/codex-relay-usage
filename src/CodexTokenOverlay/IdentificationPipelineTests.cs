using System.Text.Json;

namespace CodexTokenOverlay;

internal static class IdentificationPipelineTests
{
    internal static void Run(Action<string, bool> check, string directory)
    {
        const string a = "11111111-1111-7111-8111-111111111111", b = "22222222-2222-7222-8222-222222222222", title = "同名对话";
        var root = Path.Combine(directory, "identity-home", "sessions");
        var otherRoot = Path.Combine(directory, "identity-other", "sessions");
        foreach (var path in new[] { root, otherRoot })
        {
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "session_index.jsonl"),
                JsonSerializer.Serialize(new { id = a, thread_name = title }) + "\n" + JsonSerializer.Serialize(new { id = b, thread_name = title }) + "\n");
        }
        CodexViewIdentity View(int row) => new(1, title, DocumentKey: "fixture", Sidebar: new("fixture", new[] { new SidebarRowIdentity(row, title, null) }));
        var first = View(10); var second = View(20); var current = first;
        bool Wait(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (!condition() && DateTime.UtcNow < deadline) Thread.Sleep(5);
            return condition();
        }
        using (var monitor = new CodexVisibleThreadMonitor(root, () => Volatile.Read(ref current),
            (view, _) => new("codex://threads/" + (view.Sidebar!.Rows[0].NodeId == 10 ? a : b), null)))
        {
            check("identity pipeline starts with same-title ambiguity", Wait(() => monitor.CaptureBindingTarget() is not null) && monitor.GetStatus().ThreadId is null);
            var error = monitor.IdentifyVisibleAsync().GetAwaiter().GetResult();
            check("identity click publishes exact ID before completing", error is null && monitor.GetStatus().ThreadId == a);
            Volatile.Write(ref current, second); monitor.Wake();
            check("switch to unbound same-title row clears old identity", Wait(() => monitor.GetStatus().ThreadId is null && monitor.CaptureBindingTarget()?.ViewKey == SidebarSessionResolver.ViewKey(second)));
            error = monitor.IdentifyVisibleAsync().GetAwaiter().GetResult();
            check("second same-title click selects its own ID", error is null && monitor.GetStatus().ThreadId == b);
            Volatile.Write(ref current, first); monitor.Wake();
            check("return to identified row automatically restores its own ID", Wait(() => monitor.GetStatus().ThreadId == a));
        }
        foreach (var operation in new[] { "switch", "root", "timeout" })
        {
            current = first;
            using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var ended = new ManualResetEventSlim();
            using var monitor = new CodexVisibleThreadMonitor(root, () => Volatile.Read(ref current), (view, token) =>
            {
                started.Set(); release.Wait(TimeSpan.FromSeconds(2));
                ended.Set(); // Deliberately return a late result despite cancellation.
                return new("codex://threads/" + a, null);
            }, identifyTimeout: TimeSpan.FromMilliseconds(150));
            try
            {
                Wait(() => monitor.CaptureBindingTarget() is not null);
                var pending = monitor.IdentifyVisibleAsync();
                check(operation + " test reaches production copy phase", started.Wait(TimeSpan.FromSeconds(1)));
                if (operation == "switch") { Volatile.Write(ref current, second); release.Set(); }
                else if (operation == "root") monitor.SetRoot(otherRoot);
                var error = pending.GetAwaiter().GetResult();
                check(operation + " cancels or rejects old identify operation", error is not null);
                release.Set(); ended.Wait(TimeSpan.FromSeconds(1)); monitor.Wake();
                check(operation + " late result cannot establish a row binding", Wait(() => monitor.CaptureBindingTarget() is not null) && monitor.GetStatus().ThreadId is null);
            }
            finally { release.Set(); }
        }
    }
}
