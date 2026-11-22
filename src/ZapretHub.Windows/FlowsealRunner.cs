using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using ZapretHub.Core;

namespace ZapretHub.Windows;

public sealed partial class FlowsealRunner : IStrategyRunner, IDisposable
{
    private WindowsJob? job;
    public bool IsRunning => job is { IsRunning: true } current && HasWinws(current);

    public async Task StartAsync(Strategy strategy, CancellationToken cancellationToken)
    {
        string file = Path.GetFileName(strategy.ScriptPath);
        if (!StrategyScript().IsMatch(file) || !File.Exists(strategy.ScriptPath))
            throw new FileNotFoundException("Не найден скрипт Flowseal.", strategy.ScriptPath);
        if (job is not null)
            throw new InvalidOperationException("Сначала остановите предыдущую конфигурацию.");
        if (HasWinws(null))
            throw new InvalidOperationException("Zapret уже запущен вне ZapretHub. Остановите его процесс или службу.");

        cancellationToken.ThrowIfCancellationRequested();
        job = WindowsJob.Start(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            $"/d /v:off /c set \"NO_UPDATE_CHECK=1\"&& \"{file}\"", Path.GetDirectoryName(strategy.ScriptPath)!);
        try
        {
            for (int attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                if (IsRunning)
                    return;
                if (!job.IsRunning)
                    break;
            }
            throw new InvalidOperationException("Конфигурация не запустила winws.exe.");
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (job is not { } current)
            return;
        await current.StopAsync(cancellationToken).ConfigureAwait(false);
        current.Dispose();
        job = null;
    }

    public void Dispose() => job?.Dispose();

    private static bool HasWinws(WindowsJob? owner)
    {
        foreach (string name in new[] { "winws", "winws2" })
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        if (!process.HasExited && (owner is null || owner.Contains(process.SafeHandle)))
                            return true;
                    }
                    catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
                }
            }
            finally { foreach (Process process in processes) process.Dispose(); }
        }
        return false;
    }

    [GeneratedRegex(@"\Ageneral(?: \([A-Za-z0-9 +_.-]+\))?\.bat\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StrategyScript();
}
