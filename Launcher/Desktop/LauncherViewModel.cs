using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BrennorsCall.Launcher.Desktop;

public sealed class ActionCommand(Func<Task> action, Func<bool>? allowed = null) : ICommand
{
    public bool CanExecute(object? parameter) => allowed?.Invoke() ?? true;
    public async void Execute(object? parameter) { if (CanExecute(parameter)) await action(); }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
public sealed record AccountRow(string Name, string ApiUrl, AccountSession? Session)
{
    public string Display => Name + (Session is null || Session.Expires <= DateTimeOffset.UtcNow ? "  ·  sign in" : "  ·  signed in");
}
public sealed class LauncherViewModel : INotifyPropertyChanged
{
    private readonly HttpClient http;
    private readonly DeploymentConfig config;
    private readonly string directory;
    private readonly SettingsStore store;
    private readonly AccountVault vault;
    private readonly AuthenticationApi api;
    private readonly ClientManager manager;
    private readonly DispatcherTimer timer;
    private CancellationTokenSource? operation;
    private VerifiedRelease? latest;
    private bool busy, ready;
    private string status = "Welcome, wayfarer.", transferLabel = "", versionLabel = "Game not installed", serverStatus = "Account service · checking", primaryLabel = "INSTALL", news = "Welcome to Brennor's Call.", patchNotes = "Check for a signed release to read the latest news.";
    private double percent;
    private AccountRow? selectedAccount;
    private CharacterChoice? selectedCharacter;
    private int tabIndex;
    private string appliedChannel;
    private string appliedDirectory;
    public LauncherSettings Settings { get; private set; }
    public bool Idle => !busy;
    public ObservableCollection<AccountRow> Accounts { get; } = [];
    public ObservableCollection<CharacterChoice> Characters { get; } = [];
    public ObservableCollection<RunningClient> Clients { get; } = [];
    public ObservableCollection<string> AvailableChannels { get; } = ["live"];
    public AccountRow? SelectedAccount { get => selectedAccount; set { selectedAccount = value; selectedCharacter = null; Characters.Clear(); Changed(); Changed(nameof(SelectedCharacter)); if (!busy && value is not null) _ = Run(RefreshAccount); } }
    public CharacterChoice? SelectedCharacter { get => selectedCharacter; set { selectedCharacter = value; Changed(); } }
    public RunningClient? SelectedClient { get; set; }
    public string LoginName { get; set; } = "";
    public string CharacterName { get; set; } = "";
    public int TabIndex { get => tabIndex; set { tabIndex = value; Changed(); } }
    public string Status { get => status; private set { status = value; Changed(); } }
    public string TransferLabel { get => transferLabel; private set { transferLabel = value; Changed(); } }
    public string VersionLabel { get => versionLabel; private set { versionLabel = value; Changed(); } }
    public string ServerStatus { get => serverStatus; private set { serverStatus = value; Changed(); } }
    public string PrimaryLabel { get => primaryLabel; private set { primaryLabel = value; Changed(); } }
    public string News { get => news; private set { news = value; Changed(); } }
    public string PatchNotes { get => patchNotes; private set { patchNotes = value; Changed(); } }
    public double Percent { get => percent; private set { percent = value; Changed(); } }
    public string RunningSummary => $"{Clients.Count} client(s) running · launch another account anytime";
    public string Footer => $"LAUNCHER {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}  /  {appliedChannel.ToUpperInvariant()}";
    public ICommand PrimaryCommand { get; }
    public ICommand CheckCommand { get; }
    public ICommand RepairCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AccountsCommand { get; }
    public ICommand RefreshAccountCommand { get; }
    public ICommand CreateCharacterCommand { get; }
    public ICommand LogoutCommand { get; }
    public ICommand RemoveAccountCommand { get; }
    public ICommand CloseClientCommand { get; }
    public ICommand EndClientCommand { get; }
    public ICommand RefreshClientsCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DiagnosticsCommand { get; }
    public ICommand SelfUpdateCommand { get; }
    public ICommand CleanupCommand { get; }

    public LauncherViewModel(HttpClient http, DeploymentConfig config, string directory)
    {
        this.http = http; this.config = config; this.directory = directory; Directory.CreateDirectory(directory);
        store = new(directory); vault = new(directory); api = new(http, config); manager = new(directory);
        try { Settings = store.Load(); }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidDataException)
        {
            File.Copy(Path.Combine(directory, "settings.json"), Path.Combine(directory, "settings.invalid-" + DateTime.UtcNow.Ticks + ".json"));
            Settings = new(); Status = "Invalid settings were backed up. Review settings and save before playing.";
        }
        if (!File.Exists(Path.Combine(directory, "settings.json")))
        {
            Settings.Channel = config.DefaultChannel;
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\BrennorsCall", "GameDirectory", null) is string gamePath) Settings.GameDirectory = gamePath;
        }
        appliedChannel = Settings.Channel; appliedDirectory = Settings.GameDirectory;
        AvailableChannels.Clear();
        AvailableChannels.Add("live");
        if (Settings.Channel != "live" && config.Channels.ContainsKey(Settings.Channel)) AvailableChannels.Add(Settings.Channel);
        ActionCommand Command(Func<Task> action) => new(() => Run(action), () => !busy);
        PrimaryCommand = Command(Primary); CheckCommand = Command(Check); RepairCommand = Command(Repair);
        CancelCommand = new ActionCommand(() => { operation?.Cancel(); return Task.CompletedTask; }, () => busy);
        AccountsCommand = new ActionCommand(() => { TabIndex = 1; return Task.CompletedTask; });
        RefreshAccountCommand = Command(RefreshAccount); CreateCharacterCommand = Command(CreateCharacter);
        LogoutCommand = Command(Logout); RemoveAccountCommand = Command(RemoveAccount);
        CloseClientCommand = Command(() => CloseClient(false)); EndClientCommand = Command(() => CloseClient(true));
        RefreshClientsCommand = Command(() => { RefreshClients(); return Task.CompletedTask; });
        SaveCommand = Command(Save); DiagnosticsCommand = Command(() => { Process.Start(new ProcessStartInfo("explorer.exe", directory) { UseShellExecute = true }); return Task.CompletedTask; });
        CleanupCommand = Command(() => { Updater().Cleanup(); Status = "Unused old versions cleaned; active clients and the previous version are retained."; return Task.CompletedTask; });
        SelfUpdateCommand = Command(SelfUpdate);
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) }; timer.Tick += (_, _) => RefreshClients(); timer.Start();
    }
    private GameUpdater Updater() => new(http, config, Settings, () => SelectedAccount?.Session);
    private CancellationToken Token => operation?.Token ?? CancellationToken.None;
    private async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true; operation = new(); Changed(nameof(Idle)); CommandManager.InvalidateRequerySuggested();
        try { await action(); }
        catch (OperationCanceledException) { Status = "Operation stopped. Partial downloads are retained; retry when ready."; }
        catch (Exception error)
        {
            Status = error is HttpRequestException ? "Network or account request failed. Check connectivity and retry. " + error.Message : error.Message;
            // Never persist exception text, HTTP payloads, command lines or launch credentials.
            File.AppendAllText(Path.Combine(directory, "launcher.log"), $"{DateTimeOffset.UtcNow:O} {action.Method.Name} {error.GetType().Name}\n");
        }
        finally { operation.Dispose(); operation = null; busy = false; Changed(nameof(Idle)); CommandManager.InvalidateRequerySuggested(); RefreshClients(); }
    }
    public async Task Initialize()
    {
        await Run(async () =>
        {
            foreach (var saved in vault.List())
            {
                AccountSession? session = null; try { session = vault.Restore(saved); } catch (Exception e) when (e is InvalidOperationException or CryptographicException or FormatException) { }
                Accounts.Add(new(saved.Name, saved.ApiUrl, session));
            }
            selectedAccount = Accounts.FirstOrDefault(); Changed(nameof(SelectedAccount)); RefreshClients();
            var current = Updater().Installed;
            if (current is not null) VersionLabel = "Installed " + Updater().ReadInstalled(current).Manifest.Version;
            if (selectedAccount?.Session is not null) await RefreshAccount();
            if (Settings.AutomaticUpdates)
            {
                await Check();
                if (!ready && (!config.Endpoint(Settings.Channel).PrivateDownloads || selectedAccount?.Session is { } signedIn && signedIn.Expires > DateTimeOffset.UtcNow)) await Install();
                else if (!ready) { Status = "Sign in to download the private game build from GitHub."; TabIndex = 1; }
            }
        });
    }
    public Task Login(string password, bool register) => Run(async () =>
    {
        var endpoint = config.Endpoint(Settings.Channel);
        var session = await api.Login(endpoint.ApiUrl, LoginName.Trim(), password, register, Token);
        var old = Accounts.FirstOrDefault(a => a.Name == session.Name && a.ApiUrl == session.ApiUrl);
        if (old is not null) Accounts.Remove(old);
        var row = new AccountRow(session.Name, session.ApiUrl, session); Accounts.Add(row); SelectedAccount = row;
        if (Settings.RememberAccounts) vault.Save(session);
        await RefreshAccount();
        if (Settings.AutomaticUpdates && config.Endpoint(Settings.Channel).PrivateDownloads && !ready) { await Check(); if (!ready) await Install(); }
        Status = "Signed in. Select a character and return to Play.";
    });
    private AccountSession Session()
    {
        var session = SelectedAccount?.Session;
        if (session is null || session.Expires <= DateTimeOffset.UtcNow) { TabIndex = 1; throw new InvalidOperationException("Select an account and sign in."); }
        if (session.ApiUrl != config.Endpoint(Settings.Channel).ApiUrl.TrimEnd('/')) throw new InvalidOperationException("Sign in to an account on the selected channel's account service.");
        return session;
    }
    private async Task RefreshAccount()
    {
        var session = Session(); var roster = await api.Characters(session, Token);
        Characters.Clear(); foreach (var character in roster) Characters.Add(character); SelectedCharacter = Characters.FirstOrDefault();
        var permitted = (await api.Channels(session, Token)).Where(config.Channels.ContainsKey).ToHashSet();
        // Retain the selected item during collection updates: WPF would otherwise write null
        // through the two-way channel binding as soon as a selected item was removed.
        foreach (var channel in permitted) if (!AvailableChannels.Contains(channel)) AvailableChannels.Add(channel);
        foreach (var channel in AvailableChannels.ToArray())
            if (!permitted.Contains(channel) && channel != Settings.Channel && channel != "live") AvailableChannels.Remove(channel);
        ServerStatus = "Account service · online"; Status = "Account ready. Select a wayfarer.";
    }
    private async Task RequireChannel()
    {
        EnsureSaved(); config.Endpoint(Settings.Channel);
        if (Settings.Channel != "live" && !(await api.Channels(Session(), Token)).Contains(Settings.Channel))
            throw new InvalidOperationException("Your account has no access to this release channel. Select Live or contact the publisher.");
    }
    private void EnsureSaved()
    {
        if (Settings.Channel != appliedChannel || Path.GetFullPath(Settings.GameDirectory) != Path.GetFullPath(appliedDirectory))
            throw new InvalidOperationException("Save installation and channel settings before continuing.");
    }
    private async Task Check()
    {
        await RequireChannel(); ready = false; latest = null; PrimaryLabel = "INSTALL";
        // A CDN outage must not disable an already authenticated, verified installed build.
        var cached = Updater().Installed;
        if (cached is not null && cached.Channel == Settings.Channel)
        {
            var known = Updater().ReadInstalled(cached);
            ready = (await Updater().Required(known.Manifest, SafePath.Under(Updater().VersionPath(cached), "content"), Progress(), Token)).Count == 0;
            if (ready) PrimaryLabel = "PLAY";
        }
        Status = "Checking signed release…";
        ServerStatus = await api.Healthy(config.Endpoint(Settings.Channel).ApiUrl, Token) ? "Account service · online" : "Account service · unavailable";
        latest = await Updater().Latest(ct: Token); News = latest.Manifest.News; PatchNotes = latest.Manifest.PatchNotes;
        var installed = Updater().Installed;
        var source = installed is null ? null : SafePath.Under(Updater().VersionPath(installed), "content");
        var files = await Updater().Required(latest.Manifest, source, Progress(), Token);
        ready = installed is not null && installed.Channel == Settings.Channel && installed.ManifestHash == latest.Hash && files.Count == 0;
        VersionLabel = $"{(installed is null ? "Not installed" : "Installed " + Updater().ReadInstalled(installed).Manifest.Version)}  ·  Latest {latest.Manifest.Version}";
        PrimaryLabel = ready ? "PLAY" : installed is null ? "INSTALL" : "UPDATE";
        Status = ready ? "Your journey is ready. Select an account and Play." : $"{files.Count} file(s) required. Click {PrimaryLabel.ToLowerInvariant()} to continue.";
        Percent = ready ? 100 : 0; TransferLabel = "Signed release verified · " + Settings.Channel;
    }
    private IProgress<UpdateProgress> Progress() => new Progress<UpdateProgress>(p =>
    {
        Status = p.Stage; Percent = p.Total == 0 ? 0 : 100.0 * p.Completed / p.Total;
        TransferLabel = $"{p.Completed / 1048576.0:N1} / {p.Total / 1048576.0:N1} MiB" + (p.BytesPerSecond > 0 ? $"  ·  {p.BytesPerSecond / 1048576:N1} MiB/s  ·  {p.Remaining?.ToString(@"hh\:mm\:ss")} remaining" : "");
    });
    private async Task Primary()
    {
        await RequireChannel();
        if (!ready) { latest ??= await Updater().Latest(ct: Token); await Install(); return; }
        var session = Session(); var character = SelectedCharacter ?? throw new InvalidOperationException("Select or create a character in Accounts.");
        Status = "Verifying game and preparing an independent client…";
        var client = await manager.Launch(Updater(), Settings, session, character.Id, config.Endpoint(Settings.Channel).WorldAddress, ct => api.Ticket(session, character.Id, ct), Token);
        Status = $"Client started for {client.Account}. You can select another account and Play again.";
    }
    private async Task Install()
    {
        await Updater().Install(latest!, Progress(), Token); ready = true; PrimaryLabel = "PLAY"; VersionLabel = "Installed " + latest!.Manifest.Version;
        Status = "Installation verified. Sign in, select a character, and Play.";
    }
    private async Task Repair() { await RequireChannel(); latest = await Updater().Latest(ct: Token); await Install(); }
    private async Task CreateCharacter() { await api.CreateCharacter(Session(), CharacterName.Trim(), Token); await RefreshAccount(); }
    private async Task Logout()
    {
        var session = Session(); await api.Logout(session, Token); await RemoveAccount(); Status = "Signed out. Existing game clients keep their independent character leases.";
    }
    private Task RemoveAccount()
    {
        if (SelectedAccount is { } account) { vault.Remove(account.Name, account.ApiUrl); Accounts.Remove(account); SelectedAccount = Accounts.FirstOrDefault(); Characters.Clear(); }
        Status = "Account removed from this launcher."; return Task.CompletedTask;
    }
    private Task CloseClient(bool force)
    {
        if (SelectedClient is { } client)
        {
            if (force && MessageBox.Show("End this client immediately? Unsaved gameplay may be lost.", "End client", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return Task.CompletedTask;
            manager.Close(client, force); Status = force ? "Client stopped." : "Close requested for selected client.";
        }
        return Task.CompletedTask;
    }
    private void RefreshClients() { var running = manager.Running(); if (!Clients.Select(c => c.Pid).SequenceEqual(running.Select(c => c.Pid))) { Clients.Clear(); foreach (var client in running) Clients.Add(client); Changed(nameof(RunningSummary)); } }
    public void SetDirectory(string path) { Settings.GameDirectory = path; Changed(nameof(Settings)); }
    private async Task Save()
    {
        Settings.Validate(); config.Endpoint(Settings.Channel);
        if (Settings.Channel != "live" && !(await api.Channels(Session(), Token)).Contains(Settings.Channel)) throw new InvalidOperationException("This account has no preview-channel grant.");
        if (Path.GetFullPath(Settings.GameDirectory).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Choose a game directory separate from launcher files.");
        store.Save(Settings);
        using var startup = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (Settings.StartWithWindows) startup.SetValue("BrennorsCallLauncher", "\"" + Environment.ProcessPath + "\""); else startup.DeleteValue("BrennorsCallLauncher", false);
        if (!Settings.RememberAccounts) foreach (var account in vault.List()) vault.Remove(account.Name, account.ApiUrl);
        appliedChannel = Settings.Channel; appliedDirectory = Settings.GameDirectory; latest = null; ready = false; PrimaryLabel = "INSTALL"; Changed(nameof(Footer));
        await Check();
    }
    private async Task SelfUpdate()
    {
        await RequireChannel();
        var release = await Updater().Latest("launcher", Token);
        if (!Version.TryParse(release.Manifest.Version, out var version)) throw new InvalidDataException("Launcher version must be a numeric Windows version.");
        if (version <= Assembly.GetExecutingAssembly().GetName().Version) { Status = "Launcher is up to date."; return; }
        if (release.Manifest.Files.Length != 1) throw new InvalidDataException("Launcher release must contain exactly one installer.");
        if (Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))?.Name != "app")
            throw new InvalidOperationException("Install the launcher with its Windows installer before applying a launcher update.");
        var updateDirectory = Path.Combine(directory, "updates", release.Hash); Directory.CreateDirectory(updateDirectory);
        var installer = Path.Combine(updateDirectory, "setup.exe");
        Status = "Downloading verified launcher installer…";
        await Updater().Download(release.Manifest.Files[0], installer, config.Endpoint(Settings.Channel).BaseUrl, new(Settings.BandwidthKiB * 1024L), ct: Token, release: release);
        var envelope = Path.Combine(updateDirectory, "launcher.json"); AtomicFile.Write(envelope, release.Envelope);
        var helper = Path.Combine(updateDirectory, "BrennorsCall.UpdateHost.exe");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "BrennorsCall.UpdateHost.exe"), helper, true);
        var info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { Environment.ProcessId.ToString(), Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(), envelope, installer, Path.Combine(AppContext.BaseDirectory, "deployment.json"), Settings.Channel, AppContext.BaseDirectory, directory }) info.ArgumentList.Add(arg);
        Process.Start(info); Application.Current.Shutdown();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
