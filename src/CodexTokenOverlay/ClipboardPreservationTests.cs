using System.Runtime.InteropServices;

namespace CodexTokenOverlay;

// Deterministic coverage for the production copy-poll-restore sequence behind the
// identify button. The real path touches the machine-wide clipboard and injects
// Ctrl+Alt+L, so these checks drive PreserveCopy with scripted doubles and assert
// each outcome branch, including the OLE-locked capture that used to escape as a
// raw exception instead of a user-facing message.
internal static class ClipboardPreservationTests
{
    private const string Link = "codex://threads/8f8b0c1e-2a3d-4e5f-8a9b-0c1d2e3f4a5b";

    private sealed class ScriptedSnapshot : IClipboardPreservation, IDisposable
    {
        private readonly Func<bool> _restore;
        public int DisposeCount { get; private set; }
        public int RestoreCalls { get; private set; }
        public uint Sequence { get; init; }
        public bool Restored { get; private set; }
        public ScriptedSnapshot(Func<bool> restore) => _restore = restore;
        public bool RestoreIfUnchanged(uint expectedSequence)
        {
            RestoreCalls++;
            Restored = _restore();
            return Restored;
        }
        public void Dispose() => DisposeCount++;
    }

    // The script's snapshot is owned by the production code under test (PreserveCopy
    // disposes it); the test only inspects the disposal count afterwards.
#pragma warning disable CA1001 // The snapshot is owned and disposed by PreserveCopy, not by this test double.
    private sealed class Script
    {
        public uint Sequence = 10;
        public bool Foreground = true;
        public bool OwnedByHost = true;
        public string? ClipboardText = Link;
        public bool CopySucceeds = true;
        public int CopyCalls;
        // Flipped after the shortcut so the poll observes the user leaving the page.
        public bool ForegroundAfterCopy = true;
        public Exception? CaptureFailure;
        public bool RestoreSucceeds = true;
        public ScriptedSnapshot? LastSnapshot;

        public IClipboardPreservation Capture()
        {
            if (CaptureFailure is not null) throw CaptureFailure;
            return LastSnapshot = new ScriptedSnapshot(() => RestoreSucceeds) { Sequence = Sequence };
        }
        public bool CopyShortcut()
        {
            CopyCalls++;
            Foreground = ForegroundAfterCopy;
            // A successful copy replaces the clipboard, which advances the sequence.
            if (CopySucceeds && ClipboardText is not null) Sequence++;
            return CopySucceeds;
        }
        public CopiedConversationLink Run() => CurrentConversationLink.PreserveCopy(
            Capture, CopyShortcut, () => Sequence, () => OwnedByHost,
            () => ClipboardText, () => Foreground, CancellationToken.None);
    }
#pragma warning restore CA1001

    internal static void Run(Action<string, bool> check)
    {
        var happy = new Script().Run();
        check("identification returns the copied link", happy.Link == Link && happy.Error is null);
        check("identification restores the original clipboard", happy.ClipboardRestored);

        // The real clipboard raises ExternalException (CLIPBRD_E_CANT_OPEN) here; the
        // test raises a plain exception of a type the production catch filter accepts.
        var locked = new Script { CaptureFailure = new InvalidOperationException("CLIPBRD_E_CANT_OPEN") }.Run();
        check("OLE-locked capture reports a retry message instead of crashing",
            locked.Link is null && locked.Error == "剪贴板暂不可用，请稍后点击识别");

        var copyFailed = new Script { CopySucceeds = false }.Run();
        check("unavailable copy command is reported without polling",
            copyFailed.Error == "无法调用 Codex 的复制链接命令" && copyFailed.ClipboardRestored == false);

        var userCopy = new Script { OwnedByHost = false }.Run();
        check("a clipboard owned by another app is left untouched",
            userCopy.Link is null && userCopy.ClipboardRestored == false);

        var restoreFailed = new Script { RestoreSucceeds = false }.Run();
        check("a failed restore keeps the link and warns about the clipboard",
            restoreFailed.Link == Link && restoreFailed.Error == "已读取链接，但剪贴板恢复失败，请稍后重试");

        // The page is foreground when identification starts; the poll then observes the
        // user leaving before a valid link ever appears, so it aborts with that message.
        var leftPage = new Script { ForegroundAfterCopy = false, ClipboardText = "unrelated text" }.Run();
        check("leaving the conversation aborts identification",
            leftPage.Link is null && leftPage.Error == "识别期间离开了当前对话，请重新点击识别");

        var blank = new Script { ClipboardText = null }.Run();
        check("missing link is reported once the poll window closes",
            blank.Link is null && blank.Error == "Codex 未提供对话链接，请确认 Ctrl+Alt+L 可以复制深度链接");

        var disposed = new Script();
        var disposedResult = disposed.Run();
        check("captured clipboard snapshot is always released",
            disposedResult.Link == Link && disposed.LastSnapshot!.DisposeCount == 1);
    }
}

