namespace CodexTokenOverlay;

internal static class ClipboardUiTests
{
    internal const int MaxAttempts = 3;

    internal static DiagnosticsRunner.SelfTestResult Run()
        => RunWithRetry(RunAttempt, MaxAttempts);

    // The retry loop is separated from the scenario so a synthetic test can drive
    // it deterministically: a single-flake attempt must still report clean, and a
    // persistently failing attempt must be reported as failed rather than retried
    // forever or swallowed.
    internal static DiagnosticsRunner.SelfTestResult RunWithRetry(
        Func<int, DiagnosticsRunner.SelfTestResult> attempt, int maxAttempts)
    {
        DiagnosticsRunner.SelfTestResult result = new(0, 0, []);
        for (var number = 1; number <= maxAttempts; number++)
        {
            result = attempt(number);
            // The clipboard is a global resource: a clipboard manager or another
            // foreground process can legitimately race this synthetic probe. A
            // deterministic regression fails every attempt, so only retry when
            // the previous attempt was not clean.
            if (result.Failed == 0) return result;
            if (number < maxAttempts) Thread.Sleep(150);
        }
        return result;
    }

    private static DiagnosticsRunner.SelfTestResult RunAttempt(int attempt)
    {
        var checks = new List<DiagnosticsRunner.TestResult>();
        void Check(string name, bool passed) => checks.Add(new(name, passed, passed ? null : "assertion failed"));
        ClipboardSnapshot? original = null;
        var ours = CurrentConversationLink.GetClipboardSequenceNumber();
        try
        {
            // Capture itself can hit a transient OLE lock (CLIPBRD_E_CANT_OPEN);
            // keeping it inside the try turns a hard probe crash (exit 0xE0434352,
            // no result file) into a failed attempt the retry loop can absorb.
            original = ClipboardSnapshot.Capture();
            var fixture = new DataObject();
            fixture.SetData(DataFormats.UnicodeText, false, "original fixture text");
            fixture.SetData(DataFormats.Html, false, "<b>original fixture</b>");
            fixture.SetData(DataFormats.Rtf, false, @"{\rtf1 original fixture}");
            fixture.SetData(DataFormats.FileDrop, false, new[] { @"C:\synthetic\file.txt" });
            using var binary = new MemoryStream(new byte[] { 0, 1, 2, 127, 255 });
            fixture.SetData("CodexUsage-TestBinary", false, binary);
            Clipboard.SetDataObject(fixture, true); ours = CurrentConversationLink.GetClipboardSequenceNumber();
            using var snapshot = ClipboardSnapshot.Capture();
            var fingerprint = snapshot.Fingerprint();
            Clipboard.SetText("codex://threads/11111111-1111-7111-8111-111111111111"); ours = CurrentConversationLink.GetClipboardSequenceNumber();
            Check("restore materialized clipboard after copying a thread link", snapshot.RestoreIfUnchanged(ours));
            ours = CurrentConversationLink.GetClipboardSequenceNumber();
            using var restored = ClipboardSnapshot.Capture();
            Check("restore preserves all original fixture formats and data", fingerprint == restored.Fingerprint());
            Check("restore preserves original text", Clipboard.GetText() == "original fixture text");
            var current = Clipboard.GetDataObject()!;
            Check("restore preserves HTML and file list", current.GetData(DataFormats.Html) as string == "<b>original fixture</b>"
                && ((string[])current.GetData(DataFormats.FileDrop)!).Single() == @"C:\synthetic\file.txt");
            Clipboard.SetText("tool-owned copied link"); ours = CurrentConversationLink.GetClipboardSequenceNumber();
            var expected = ours;
            Clipboard.SetText("new user copy"); ours = CurrentConversationLink.GetClipboardSequenceNumber();
            Check("clipboard restoration never overwrites an intervening copy", !snapshot.RestoreIfUnchanged(expected) && Clipboard.GetText() == "new user copy");
            Clipboard.Clear(); ours = CurrentConversationLink.GetClipboardSequenceNumber();
            using var empty = ClipboardSnapshot.Capture();
            Clipboard.SetText("temporary link"); ours = CurrentConversationLink.GetClipboardSequenceNumber();
            Check("empty clipboard returns to empty", empty.RestoreIfUnchanged(ours) && !Clipboard.ContainsText());
            ours = CurrentConversationLink.GetClipboardSequenceNumber();
            using var bitmap = new Bitmap(4, 4); bitmap.SetPixel(1, 1, Color.FromArgb(255, 110, 70, 230));
            Clipboard.SetImage(bitmap); ours = CurrentConversationLink.GetClipboardSequenceNumber();
            using var image = ClipboardSnapshot.Capture();
            Clipboard.SetText("temporary link"); ours = CurrentConversationLink.GetClipboardSequenceNumber();
            Check("image clipboard restoration succeeds", image.RestoreIfUnchanged(ours));
            ours = CurrentConversationLink.GetClipboardSequenceNumber();
            using var restoredImage = Clipboard.GetImage() as Bitmap;
            Check("image clipboard pixel data is retained", restoredImage?.GetPixel(1, 1) == bitmap.GetPixel(1, 1));
        }
        catch (Exception error) { checks.Add(new("clipboard UI probe exception " + error.GetType().Name, false, "attempt " + attempt + ": " + error.Message)); }
        finally { original?.RestoreIfUnchanged(ours); original?.Dispose(); }
        return new(checks.Count(c => c.Passed), checks.Count(c => !c.Passed), checks);
    }
}
