using System.Diagnostics;
using ZapretHub.Core;
using ZapretHub.Windows;

namespace ZapretHub.Tests;

public sealed class WindowsAndStateTests
{
    [Fact]
    public void SettingsAreRecoveredFromDiskAndInvalidEditsPreserveThem()
    {
        using var directory = new TestDirectory();
        var store = new LocalState(directory.Path);
        var settings = new HubSettings { KeepVersions = 3, MonitorMinutes = 5, Vpn = new(VpnMode.Automatic, Profile: "office") };

        store.SaveSettings(settings);
        Assert.Throws<ArgumentException>(() => store.SaveSettings(settings with { KeepVersions = 0 }));
        Assert.Throws<ArgumentException>(() => store.SaveSettings(settings with { Vpn = new(VpnMode.Automatic) }));
        HubSettings restored = new LocalState(directory.Path).LoadSettings();

        Assert.Equal(3, restored.KeepVersions);
        Assert.Equal(5, restored.MonitorMinutes);
        Assert.Equal(settings.Vpn, restored.Vpn);
        Assert.Equal(settings.Targets, restored.Targets);
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void WorkingHistoryPrioritizesCurrentNetworkAndMostRecentStrategy()
    {
        using var directory = new TestDirectory();
        var store = new LocalState(directory.Path);
        var history = new StrategyHistory();
        history.Remember("home", new("1.0", "first"));
        history.Remember("home", new("1.1", "second"));
        history.Remember("office", new("1.2", "third"));
        store.SaveHistory(history);

        Assert.Equal([new("1.1", "second"), new("1.0", "first"), new("1.2", "third")], store.LoadHistory().Preferred("home"));
    }

    [Fact]
    public async Task WindowsJobStopsRealChildAfterTheLauncherHasExited()
    {
        using var directory = new TestDirectory();
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string sleeper = Path.Combine(directory.Path, "zphub-child.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), sleeper);
        string batch = Path.Combine(directory.Path, "general (ALT).bat");
        await File.WriteAllTextAsync(batch,
            "@echo off\r\nif not defined NO_UPDATE_CHECK exit /b 1\r\nstart \"\" /b zphub-child.exe -n 60 127.0.0.1\r\n",
            TestContext.Current.CancellationToken);
        using var job = WindowsJob.Start(cmd, $"/d /v:off /c set \"NO_UPDATE_CHECK=1\"&& \"{Path.GetFileName(batch)}\"", directory.Path);
        using Process child = await WaitForChild();
        Assert.True(job.IsRunning);
        Assert.False(child.HasExited);

        await job.StopAsync(TestContext.Current.CancellationToken);
        await child.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.False(job.IsRunning);
        Assert.True(child.HasExited);

        async Task<Process> WaitForChild()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (true)
            {
                foreach (var process in Process.GetProcessesByName("zphub-child"))
                {
                    if (job.Contains(process.SafeHandle))
                        return process;
                    process.Dispose();
                }
                await Task.Delay(25, timeout.Token);
            }
        }
    }

    [Fact]
    public async Task FlowsealRunnerRejectsUnsafeOrMissingScriptNamesBeforeLaunchingAnything()
    {
        using var directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "general & unexpected.bat");
        await File.WriteAllTextAsync(path, "exit", TestContext.Current.CancellationToken);
        using var runner = new FlowsealRunner();

        await Assert.ThrowsAsync<FileNotFoundException>(() => runner.StartAsync(new(new("1", "unsafe"), path), TestContext.Current.CancellationToken));

        Assert.False(runner.IsRunning);
    }
}
