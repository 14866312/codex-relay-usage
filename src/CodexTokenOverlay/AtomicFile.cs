namespace CodexTokenOverlay;

// Same-volume write: a fully written and flushed temporary file replaces the target,
// so a mid-write failure or a process exit never leaves a half-written configuration.
internal static class AtomicFile
{
    internal static void Write(string path, Action<Stream> write)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            temporary = null;
        }
        finally
        {
            if (temporary is not null)
            {
                // A failed save must never leave its scratch file behind.
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
