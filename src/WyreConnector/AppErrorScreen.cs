using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace WyreConnector;

public static class AppErrorScreen
{
    public static void Show(IntegrityResult result)
    {
        var message = result.Reason switch
        {
            IntegrityFailureReason.IntegrityFileNotFound =>
                "integrity.toml is missing. The installation may be corrupted.\n" +
                "Please reinstall the application.",
            
            IntegrityFailureReason.IntegrityFileCorrupt =>
                "integrity.toml is corrupted or has been tampered with.\n" +
                "Please reinstall the application.",
            
            IntegrityFailureReason.SignatureMismatch =>
                "The integrity manifest signature is invalid.\n" +
                "The installation may have been tampered with.\n" +
                "Please reinstall the application.",
            
            IntegrityFailureReason.ModuleHostNotFound =>
                "ModuleHost.dll is missing. The installation is corrupted.\n" +
                "Please reinstall the application.",
            
            IntegrityFailureReason.ModuleHostHashMismatch =>
                $"ModuleHost.dll has been modified or corrupted.\n\n" +
                $"Expected: {result.Expected?[..Math.Min(16, result.Expected?.Length ?? 0)]}...\n" +
                $"Found:    {result.Actual?[..Math.Min(16, result.Actual?.Length ?? 0)]}...\n\n" +
                "Please reinstall the application.",
            
            _ => "An unknown integrity error occurred. Please reinstall."
        };
        
        // Write to crash log regardless
        var logPath = Path.Combine(AppContext.BaseDirectory,
            $"integrity-failure-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(logPath, $"[{DateTime.UtcNow:O}] {result.Reason}\n{message}\n" +
            $"Expected: {result.Expected}\nActual: {result.Actual}");
        
        // Platform error dialog — no framework deps
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            ShowWindows(message);
        else
            ShowLinux(message);
    }
    
    private static void ShowWindows(string message)
    {
        // P/Invoke MessageBoxW — zero managed UI deps
        MessageBox(IntPtr.Zero,
            message,
            "Integrity Check Failed — Cannot Start",
            MB_OK | MB_ICONERROR | MB_SYSTEMMODAL);
    }
    
    private static void ShowLinux(string message)
    {
        // Try zenity first (GNOME), then kdialog (KDE), then stderr
        var shown = TryShowDialog("zenity",
                $"--error --title=\"Integrity Check Failed\" --text=\"{message}\"")
            || TryShowDialog("kdialog",
                $"--error \"{message}\" --title \"Integrity Check Failed\"");
        
        if (!shown)
        {
            // Absolute last resort — stderr
            Console.Error.WriteLine($"FATAL INTEGRITY FAILURE: {message}");
        }
    }
    
    private static bool TryShowDialog(string tool, string args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(tool, args)
                { UseShellExecute = true })?.WaitForExit();
            return true;
        }
        catch { return false; }
    }
    
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(
        IntPtr hWnd, string text, string caption, uint type);
    
    private const uint MB_OK = 0x0;
    private const uint MB_ICONERROR = 0x10;
    private const uint MB_SYSTEMMODAL = 0x1000;
}
