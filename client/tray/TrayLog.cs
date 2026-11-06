using System.IO;

/// <summary>
/// Small diagnostic log in %LOCALAPPDATA%\Vigil\tray.log (the child's profile; nothing secret in it).
/// Kept under ~256 KB: when bigger it is moved to tray.log.old. Never throws.
/// </summary>
public static class TrayLog
{
    private static readonly object Gate = new();
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vigil");
    private static readonly string File_ = Path.Combine(Dir, "tray.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var info = new FileInfo(File_);
                if (info.Exists && info.Length > 256 * 1024)
                    File.Move(File_, File_ + ".old", overwrite: true);
                File.AppendAllText(File_, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* diagnostics only */ }
    }
}
