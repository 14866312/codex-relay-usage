using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexTokenOverlay;

internal sealed record CopiedConversationLink(string? Link, string? Error, bool ClipboardRestored = false);

// Explicit user action only. Reads the app's Copy deep link command; never derives
// the visible conversation from background subscriptions or the newest log.
internal static class CurrentConversationLink
{
    internal static string? ThreadId(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 512 ||
            !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != "codex" || uri.Host != "threads" || uri.UserInfo.Length != 0 ||
            uri.Port != -1 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
        var path = uri.AbsolutePath.Trim('/');
        return Guid.TryParseExact(path, "D", out var id) ? id.ToString() : null;
    }

    internal static CopiedConversationLink Read(CodexViewIdentity view, CancellationToken cancellationToken)
    {
        if (!TryGetForegroundHost(view, out var host, out var processId))
            return new(null, "请回到要识别的 Codex 对话后重试");
        if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0))
            return new(null, "请松开键盘修饰键后再点击识别");
        ClipboardSnapshot? snapshot = null;
        uint copiedSequence = 0;
        var restoreFailed = false;
        CopiedConversationLink result;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Materialize before copying. Keeping only an OLE proxy is insufficient:
            // its old provider may no longer render data after clipboard replacement.
            snapshot = ClipboardSnapshot.Capture();
            if (!IsForegroundHost(host) || GetClipboardSequenceNumber() != snapshot.Sequence)
                return new(null, "页面或剪贴板已变化，请重新点击识别");
            if (!CopyShortcut()) return new(null, "无法调用 Codex 的复制链接命令");
            var clock = Stopwatch.StartNew();
            result = new(null, "Codex 未提供对话链接，请确认 Ctrl+Alt+L 可以复制深度链接");
            while (clock.ElapsedMilliseconds < 1800)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sequence = GetClipboardSequenceNumber();
                if (sequence != snapshot.Sequence && IsClipboardFrom(processId))
                {
                    try
                    {
                        var text = Clipboard.GetText(TextDataFormat.UnicodeText);
                        if (sequence == GetClipboardSequenceNumber() && ThreadId(text) is not null)
                        {
                            copiedSequence = sequence;
                            result = new(text, null);
                            break;
                        }
                    }
                    catch (ExternalException) { }
                }
                if (!IsForegroundHost(host))
                {
                    result = new(null, "识别期间离开了当前对话，请重新点击识别");
                    break;
                }
                Thread.Sleep(20);
            }
        }
        catch (OperationCanceledException)
        {
            result = new(null, "识别已取消");
        }
        catch (Exception error) when (error is ExternalException or InvalidOperationException or NotSupportedException)
        {
            result = new(null, "剪贴板暂不可用，请稍后点击识别");
        }
        finally
        {
            // Restore only our own copied link. An intervening user copy wins.
            if (snapshot is not null && copiedSequence != 0)
            {
                var shouldRestore = GetClipboardSequenceNumber() == copiedSequence;
                restoreFailed = shouldRestore && !snapshot.RestoreIfUnchanged(copiedSequence)
                    && GetClipboardSequenceNumber() == copiedSequence;
            }
            snapshot?.Dispose();
        }
        return result with { ClipboardRestored = snapshot?.Restored == true,
            Error = restoreFailed ? "已读取链接，但剪贴板恢复失败，请稍后重试" : result.Error };
    }

    private static bool TryGetForegroundHost(CodexViewIdentity view, out IntPtr host, out uint processId)
    {
        host = IntPtr.Zero; processId = 0;
        if (view.WindowCount != 1 || view.Error is not null || view.DocumentKey is null ||
            !long.TryParse(view.DocumentKey.Split('/')[0], out var handle)) return false;
        host = new(handle);
        if (!IsForegroundHost(host)) return false;
        var windows = CodexWindowLocator.GetVisibleMainWindows();
        if (windows.Count != 1 || windows[0].Handle != host) return false;
        GetWindowThreadProcessId(host, out processId);
        return processId != 0;
    }
    private static bool IsForegroundHost(IntPtr host) => GetAncestor(GetForegroundWindow(), 2) == host;
    private static bool IsClipboardFrom(uint processId)
    {
        var owner = GetClipboardOwner();
        return owner != IntPtr.Zero && GetWindowThreadProcessId(owner, out var ownerId) != 0 && ownerId == processId;
    }
    private static bool CopyShortcut()
    {
        Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0u } } };
        var inputs = new[] { Key(0x11), Key(0x12), Key(0x4C), Key(0x4C, true), Key(0x12, true), Key(0x11, true) };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) == inputs.Length) return true;
        // A partial injection must not leave Ctrl/Alt pressed.
        var release = new[] { Key(0x4C, true), Key(0x12, true), Key(0x11, true) };
        SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
        return false;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
}

internal sealed class ClipboardSnapshot : IDisposable
{
    private readonly DataObject _data = new();
    private readonly List<IDisposable> _owned = [];
    private bool _empty;
    internal uint Sequence { get; private set; }
    internal bool Restored { get; private set; }
    // Diagnostic equality only; never expose clipboard contents in a report.
    internal string Fingerprint()
    {
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var format in _data.GetFormats(autoConvert: false).Order(StringComparer.Ordinal))
        {
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(format)); hash.AppendData(new byte[] { 0 });
            var value = ((IDataObject)_data).GetData(format, autoConvert: false);
            switch (value)
            {
                case string text: hash.AppendData(System.Text.Encoding.UTF8.GetBytes(text)); break;
                case string[] paths: hash.AppendData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(paths)); break;
                case byte[] buffer: hash.AppendData(buffer); break;
                case MemoryStream stream: hash.AppendData(stream.ToArray()); break;
                case Image image:
                    using (var output = new MemoryStream()) { image.Save(output, System.Drawing.Imaging.ImageFormat.Png); hash.AppendData(output.ToArray()); }
                    break;
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    internal static ClipboardSnapshot Capture()
    {
        var snapshot = new ClipboardSnapshot { Sequence = CurrentConversationLink.GetClipboardSequenceNumber() };
        try
        {
            var source = Clipboard.GetDataObject();
            var formats = source?.GetFormats(autoConvert: false) ?? [];
            if (formats.Length > 64) throw new NotSupportedException("Too many clipboard formats");
            snapshot._empty = formats.Length == 0;
            long bytes = 0;
            foreach (var format in formats)
            {
                var value = source!.GetData(format, autoConvert: false);
                // CF_BITMAP may be exposed by name while GetData(autoConvert:false)
                // returns null. GetImage uses the clipboard's native image conversion.
                using var nativeImage = value is null && format == DataFormats.Bitmap ? Clipboard.GetImage() : null;
                value ??= nativeImage;
                object copy = value switch
                {
                    string text => text,
                    string[] paths => paths.ToArray(),
                    byte[] buffer when buffer.Length <= 32 * 1024 * 1024 => buffer.ToArray(),
                    MemoryStream stream when stream.Length <= 32 * 1024 * 1024 => new MemoryStream(stream.ToArray()),
                    Image image when (long)image.Width * image.Height <= 8_000_000 => new Bitmap(image),
                    _ => throw new NotSupportedException("Clipboard format cannot be preserved: " + format + " (" + (value?.GetType().Name ?? "null") + ")")
                };
                bytes += copy switch { string text => (long)text.Length * 2, string[] paths => paths.Sum(p => (long)p.Length * 2),
                    byte[] buffer => buffer.Length, MemoryStream stream => stream.Length, Image image => (long)image.Width * image.Height * 4, _ => 0 };
                if (copy is IDisposable disposable) snapshot._owned.Add(disposable);
                if (bytes > 64 * 1024 * 1024) throw new NotSupportedException("Clipboard content too large to preserve");
                snapshot._data.SetData(format, autoConvert: false, copy);
            }
            if (snapshot.Sequence != CurrentConversationLink.GetClipboardSequenceNumber())
                throw new InvalidOperationException("Clipboard changed during capture");
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }
    internal bool RestoreIfUnchanged(uint expectedSequence)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (CurrentConversationLink.GetClipboardSequenceNumber() != expectedSequence) return false;
            try
            {
                if (_empty) Clipboard.Clear(); else Clipboard.SetDataObject(_data, copy: true, retryTimes: 0, retryDelay: 0);
                return Restored = true;
            }
            catch (ExternalException) { Thread.Sleep(25); }
        }
        return false;
    }
    public void Dispose() { foreach (var value in _owned) value.Dispose(); _owned.Clear(); }
}
