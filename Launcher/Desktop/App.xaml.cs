using System.Net.Http;
using System.Windows;

namespace BrennorsCall.Launcher.Desktop;

public partial class App : Application
{
    private Mutex? mutex;
    private HttpClient? http;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        mutex = new Mutex(true, @"Local\BrennorsCallLauncher", out var created);
        if (!created) { MessageBox.Show("Brennor's Call Launcher is already open."); Shutdown(); return; }
        try
        {
            var directory = Environment.GetEnvironmentVariable("BC_LAUNCHER_DATA") ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BrennorsCall", "Launcher");
            var config = DeploymentConfig.Load(System.IO.Path.Combine(AppContext.BaseDirectory, "deployment.json"));
            http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30), MaxResponseContentBufferSize = 32 * 1024 * 1024 };
            var model = new LauncherViewModel(http, config, directory);
            var window = new MainWindow { DataContext = model }; MainWindow = window;
            window.Loaded += async (_, _) =>
            {
                await model.Initialize();
                if (e.Args.Contains("--update-launcher")) model.SelfUpdateCommand.Execute(null);
            };
            window.Show();
        }
        catch (Exception error) { MessageBox.Show("The launcher could not start. Check publisher configuration and local settings.\n" + error.GetType().Name, "Brennor's Call Launcher"); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { http?.Dispose(); mutex?.Dispose(); base.OnExit(e); }
}
