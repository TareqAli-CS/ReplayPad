namespace ReplayPad.Core;

/// <summary>
/// Durable file writes that survive a mid-write kill AND the real-world
/// failure modes seen in %AppData%: File.Replace throws "Unable to remove
/// the file to be replaced" on EFS-encrypted files and when Defender / the
/// Search indexer briefly holds a handle. Strategy: write a temp file, back
/// up the current good copy, then atomically move-overwrite (a rename, which
/// works on encrypted files) with retries, and a plain overwrite as a last
/// resort so the data always lands.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);

        try { if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true); }
        catch { /* backup is best-effort */ }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception) when (attempt < 4)
            {
                Thread.Sleep(75); // let whoever holds the handle release it
            }
        }

        // Last resort: overwrite in place (not atomic, but the data lands).
        File.WriteAllText(path, content);
        try { File.Delete(temp); } catch { }
    }
}
