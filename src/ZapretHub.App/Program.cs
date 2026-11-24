using ZapretHub.Core;
using ZapretHub.Windows;

namespace ZapretHub.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool startInTray = args.Contains(WindowsStartup.TrayArgument, StringComparer.Ordinal);
        using var instance = new Mutex(true, @"Local\ZapretHub", out bool firstInstance);
        if (!firstInstance)
        {
            if (!startInTray)
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
                vpn, storage, NetworkIdentity.Current, TimeProvider.System, new HubTiming(), new EgorikRecoveryApproval());
            using var window = new MainWindow(hub, settings, wireGuardSystem.Profiles, new WindowsStartup(Application.ExecutablePath));
            if (startInTray)
            {
                using var context = new ApplicationContext();
                window.FormClosed += (_, _) => context.ExitThread();
                window.StartHidden();
                Application.Run(context);
                return;
            }
            Application.Run(window);
        }
        catch (Exception error)
        {
            MessageBox.Show(error.Message, "Ошибка запуска ZapretHub", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
