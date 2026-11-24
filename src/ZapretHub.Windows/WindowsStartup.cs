using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;

namespace ZapretHub.Windows;

public sealed class WindowsStartup(string executable)
{
    public const string TrayArgument = "--autostart";
    private const int TaskNotFound = unchecked((int)0x80070002);
    private const int CreateOrUpdate = 6;
    private const int InteractiveToken = 3;

    public bool IsEnabled() => WithFolder((folder, sid) =>
    {
        object? task = null;
        try
        {
            task = ((dynamic)folder).GetTask(TaskName(sid));
            return (bool)((dynamic)task).Enabled;
        }
        catch (Exception error) when (error.HResult == TaskNotFound) { return false; }
        finally { Release(task); }
    });

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            WithFolder((folder, sid) =>
            {
                try { ((dynamic)folder).DeleteTask(TaskName(sid), 0); }
                catch (Exception error) when (error.HResult == TaskNotFound) { }
                return true;
            });
            return;
        }

        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ZapretHub", "App", "ZapretHub.exe");
        RequireInstalledExecutable(executable, expected);
        WithFolder((folder, sid) =>
        {
            object task = ((dynamic)folder).RegisterTask(TaskName(sid), CreateDefinition(expected, sid), CreateOrUpdate,
                sid, null, InteractiveToken, null);
            Release(task);
            return true;
        });
    }

    internal static void RequireInstalledExecutable(string executable, string expected)
    {
        if (!Path.GetFullPath(executable).Equals(expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(expected))
            throw new InvalidOperationException($"Для автозапуска установите ZapretHub и откройте {expected}.");

        // The scheduled action runs elevated. It must point at the installed copy, including its DLLs.
        string app = Path.GetDirectoryName(expected)!;
        string root = Path.GetDirectoryName(app)!;
        if (new[] { expected, app, root }.Any(path => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            || Directory.EnumerateFileSystemEntries(app, "*", new EnumerationOptions { AttributesToSkip = 0 })
                .Any(path => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)))
            throw new InvalidOperationException("Для автозапуска установите ZapretHub в обычную папку без ссылок и перенаправлений.");
    }

    internal static string CreateDefinition(string executable, string sid)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        return new XElement(ns + "Task", new XAttribute("version", "1.3"),
            new XElement(ns + "RegistrationInfo", new XElement(ns + "Description", "Запускает ZapretHub в трее при входе в Windows.")),
            new XElement(ns + "Triggers", new XElement(ns + "LogonTrigger", new XElement(ns + "UserId", sid))),
            new XElement(ns + "Principals", new XElement(ns + "Principal", new XAttribute("id", "CurrentUser"),
                new XElement(ns + "UserId", sid), new XElement(ns + "LogonType", "InteractiveToken"),
                new XElement(ns + "RunLevel", "HighestAvailable"))),
            new XElement(ns + "Settings", new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", "false"), new XElement(ns + "StopIfGoingOnBatteries", "false"),
                new XElement(ns + "ExecutionTimeLimit", "PT0S")),
            new XElement(ns + "Actions", new XAttribute("Context", "CurrentUser"), new XElement(ns + "Exec",
                new XElement(ns + "Command", executable), new XElement(ns + "Arguments", TrayArgument),
                new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(executable))))).ToString();
    }

    private static string TaskName(string sid) => $"ZapretHub-{sid}";

    private static T WithFolder<T>(Func<object, string, T> action)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new InvalidOperationException("Не удалось определить пользователя Windows.");
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        object service = Activator.CreateInstance(type)!;
        object? folder = null;
        try
        {
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder(@"\");
            return action(folder!, sid);
        }
        finally
        {
            Release(folder);
            Release(service);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }
}
