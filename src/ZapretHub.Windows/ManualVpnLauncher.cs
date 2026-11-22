using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ZapretHub.Core;

namespace ZapretHub.Windows;

public sealed class ManualVpnLauncher : IManualVpnLauncher
{
    public Task OpenAsync(string application, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Path.GetFullPath(application);
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Выбранный EXE-файл VPN-клиента не найден.", path);

        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path));
        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    if (process.MainWindowHandle is var window && window != IntPtr.Zero
                        && string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsIconic(window))
                            ShowWindowAsync(window, 9);
                        if (!SetForegroundWindow(window))
                            FlashWindow(window, true);
                        return Task.CompletedTask;
                    }
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            }
        }
        finally { foreach (Process process in processes) process.Dispose(); }

        OpenThroughDesktop(path);
        return Task.CompletedTask;
    }

    private static void OpenThroughDesktop(string path)
    {
        // Invoke ShellExecute on the desktop Explorer instance to use the interactive user's token.
        var shellWindowsId = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        object windows = Activator.CreateInstance(Type.GetTypeFromCLSID(shellWindowsId, throwOnError: true)!)!;
        object? desktop = null;
        object? view = null;
        object? shell = null;
        try
        {
            object location = 0; // CSIDL_DESKTOP
            object root = System.Reflection.Missing.Value;
            desktop = ((dynamic)windows).FindWindowSW(ref location, ref root, 8, out int _, 1);
            if (desktop is null)
                throw new InvalidOperationException("Рабочий стол Windows недоступен.");
            view = ((dynamic)desktop).Document;
            shell = ((dynamic)view).Application;
            ((dynamic)shell).ShellExecute(path, "", Path.GetDirectoryName(path), "open", 1);
        }
        finally
        {
            foreach (object? item in new[] { shell, view, desktop, windows })
                if (item is not null && Marshal.IsComObject(item))
                    Marshal.ReleaseComObject(item);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr window, bool invert);
}
