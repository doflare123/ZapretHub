using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ZapretHub.Windows;

public sealed class WireGuardSystem : IWireGuardSystem
{
    private const string Prefix = "WireGuardTunnel$";
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";
    private readonly string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WireGuard", "wg.exe");

    public IReadOnlyList<string> Profiles()
    {
        if (!File.Exists(executable))
            return [];
        using RegistryKey? services = Registry.LocalMachine.OpenSubKey(ServicesPath);
        return services?.GetSubKeyNames().Where(name => name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            .Select(name => name[Prefix.Length..]).Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
    }

    public bool IsStopped(string profile) => WithService(profile, 0x4, service =>
    {
        if (!QueryServiceStatus(service, out var status))
            throw new Win32Exception();
        return status.CurrentState == 1;
    });

    public void Start(string profile) => WithService(profile, 0x10, service =>
    {
        if (!StartServiceW(service, 0, IntPtr.Zero))
            throw new Win32Exception();
        return true;
    });

    // A start or stop already in progress can temporarily reject the stop control.
    public void Stop(string profile) => WithService(profile, 0x20, service =>
    {
        if (!ControlService(service, 1, out _))
        {
            int error = Marshal.GetLastWin32Error();
            if (error is not (1061 or 1062))
                throw new Win32Exception(error);
        }
        return true;
    });

    public async Task<DateTimeOffset?> LatestHandshakeAsync(string profile, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            ArgumentList = { "show", profile, "latest-handshakes" }
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить wg.exe.");
        try
        {
            string output = await process.StandardOutput.ReadToEndAsync(token).ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            if (process.ExitCode != 0)
                return null;
            long latest = 0;
            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                int split = line.LastIndexOf('\t');
                if (split >= 0 && long.TryParse(line.AsSpan(split + 1).Trim(), CultureInfo.InvariantCulture, out long seconds))
                    latest = Math.Max(latest, seconds);
            }
            return latest is > 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(latest) : null;
        }
        finally
        {
            if (!process.HasExited)
                process.Kill();
        }
    }

    private static T WithService<T>(string profile, uint access, Func<IntPtr, T> action)
    {
        IntPtr manager = OpenSCManagerW(null, null, 1);
        if (manager == IntPtr.Zero)
            throw new Win32Exception();
        try
        {
            IntPtr service = OpenServiceW(manager, Prefix + profile, access);
            if (service == IntPtr.Zero)
                throw new Win32Exception();
            try { return action(service); }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceExitCode, Checkpoint, WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool StartServiceW(IntPtr service, int argumentCount, IntPtr arguments);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll")]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
