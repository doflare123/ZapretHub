using ZapretHub.Core;
using ZapretHub.Windows;

namespace ZapretHub.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var instance = new Mutex(true, @"Local\ZapretHub", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("ZapretHub уже запущен. Откройте его из области уведомлений.", "ZapretHub");
            return;
        }

        try
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ZapretHub", "Data");
            var storage = new LocalState(data);
            HubSettings settings = storage.LoadSettings();
            using var probes = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                PooledConnectionLifetime = TimeSpan.Zero
            }) { Timeout = Timeout.InfiniteTimeSpan };
            using var github = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var runner = new FlowsealRunner();
            var connectivity = new HttpConnectivity(probes, TimeSpan.FromSeconds(4), TimeProvider.System);
            var wireGuardSystem = new WireGuardSystem();
            var wireGuard = new WireGuardVpn(wireGuardSystem,
                token => connectivity.IsControlReachableAsync(storage.LoadSettings(), token), TimeProvider.System);
            var vpn = new VpnFallback(new ManualVpnLauncher(), wireGuard, TimeProvider.System, TimeSpan.FromSeconds(25));
            var hub = new Hub(settings, new GitHubReleaseStore(github, Path.Combine(data, "Releases")), runner, connectivity,
                vpn, storage, NetworkIdentity.Current, TimeProvider.System, new HubTiming());
            using var window = new MainWindow(hub, settings, wireGuardSystem.Profiles);
            Application.Run(window);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Ошибка запуска ZapretHub", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
