using System.Xml.Linq;
using ZapretHub.Windows;

namespace ZapretHub.Tests;

public sealed class WindowsStartupTests
{
    [Fact]
    public void TaskRunsOnlyForTheChosenInteractiveUserAndUsesTheTrayArgument()
    {
        const string sid = "S-1-5-21-11-22-33-1001";
        const string executable = @"C:\Program Files\Zapret & Hub\ZapretHub.exe";
        var task = XElement.Parse(WindowsStartup.CreateDefinition(executable, sid));
        XNamespace ns = task.Name.Namespace;

        var trigger = Assert.Single(task.Element(ns + "Triggers")!.Elements());
        Assert.Equal(ns + "LogonTrigger", trigger.Name);
        Assert.Equal(sid, (string?)trigger.Element(ns + "UserId"));
        var principal = Assert.Single(task.Element(ns + "Principals")!.Elements());
        Assert.Equal(sid, (string?)principal.Element(ns + "UserId"));
        Assert.Equal("InteractiveToken", (string?)principal.Element(ns + "LogonType"));
        Assert.Equal("HighestAvailable", (string?)principal.Element(ns + "RunLevel"));
        var actions = task.Element(ns + "Actions")!;
        Assert.Equal((string?)principal.Attribute("id"), (string?)actions.Attribute("Context"));
        var run = Assert.Single(actions.Elements());
        Assert.Equal(executable, (string?)run.Element(ns + "Command"));
        Assert.Equal("--autostart", (string?)run.Element(ns + "Arguments"));
        Assert.Equal(Path.GetDirectoryName(executable), (string?)run.Element(ns + "WorkingDirectory"));
        var settings = task.Element(ns + "Settings")!;
        Assert.Equal("IgnoreNew", (string?)settings.Element(ns + "MultipleInstancesPolicy"));
        Assert.Equal("PT0S", (string?)settings.Element(ns + "ExecutionTimeLimit"));
        Assert.Equal("false", (string?)settings.Element(ns + "DisallowStartIfOnBatteries"));
        Assert.Equal("false", (string?)settings.Element(ns + "StopIfGoingOnBatteries"));
    }

    [Fact]
    public void RequiresAnExistingInstalledCopyBeforeRegisteringAnElevatedTask()
    {
        using var directory = new TestDirectory();
        string installed = Path.Combine(directory.Path, "ZapretHub", "App", "ZapretHub.exe");
        string development = Path.Combine(directory.Path, "ZapretHub.exe");
        File.WriteAllText(development, "development build");

        Assert.Throws<InvalidOperationException>(() => WindowsStartup.RequireInstalledExecutable(development, installed));
        Assert.Throws<InvalidOperationException>(() => WindowsStartup.RequireInstalledExecutable(installed, installed));
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.Copy(development, installed);

        WindowsStartup.RequireInstalledExecutable(installed, installed);
    }
}
