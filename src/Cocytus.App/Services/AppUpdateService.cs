using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Windows.Threading;
using Cocytus.Data.History;
using Cocytus.Update;

namespace Cocytus.App.Services;

/// <summary>
/// Atualização do próprio Cocytus pelas releases do GitHub: consulta ao abrir e a cada 24 horas (se ligado nas
/// Configurações), avisa quando há versão nova e, se o usuário aceitar, baixa, confere e instala.
/// </summary>
public sealed class AppUpdateService : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    private readonly AppServices _services;
    private readonly HttpClient _http;
    private readonly DispatcherTimer _timer;
    private bool _checking;

    public AppUpdateService(AppServices services)
    {
        _services = services;
        _http = GitHubReleaseClient.CreateHttpClient(CurrentVersion);
        Client = new GitHubReleaseClient(_http);
        _timer = new DispatcherTimer { Interval = FirstCheckDelay };
        _timer.Tick += async (_, _) => await OnTimerAsync();
    }

    /// <summary>Versão nova encontrada (vem da thread da interface).</summary>
    public event EventHandler<AppRelease>? UpdateAvailable;

    public GitHubReleaseClient Client { get; }

    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    public string UpdatesDir => Path.Combine(_services.Paths.DataRoot, "atualizacoes");

    public void Start()
    {
        CleanOldDownloads();
        _timer.Start();
    }

    /// <summary>Consulta a última release. <paramref name="manual"/> ignora a versão pulada.</summary>
    public async Task<AppRelease?> CheckAsync(bool manual, CancellationToken cancellationToken)
    {
        var release = await Client.GetLatestAsync(cancellationToken);
        var settings = _services.Settings.Clone();
        settings.LastAppUpdateCheck = DateTimeOffset.Now;
        await _services.SaveSettingsAsync(settings, recordEvent: false);
        if (release is null || release.Version <= CurrentVersion)
        {
            _services.Log.Info($"Atualização do Cocytus: nenhuma versão nova (atual {CurrentVersion.ToString(3)}, última {release?.Version.ToString(3) ?? "nenhuma"}).");
            return null;
        }

        if (!manual && string.Equals(_services.Settings.SkippedAppVersion, release.Version.ToString(3), StringComparison.Ordinal))
        {
            _services.Log.Info($"Atualização do Cocytus: {release.Version.ToString(3)} disponível, mas foi pulada pelo usuário.");
            return null;
        }

        _services.Log.Info($"Atualização do Cocytus: {release.Version.ToString(3)} disponível ({release.PageUrl}).");
        return release;
    }

    public async Task SkipAsync(AppRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var settings = _services.Settings.Clone();
        settings.SkippedAppVersion = release.Version.ToString(3);
        await _services.SaveSettingsAsync(settings);
    }

    /// <summary>Baixa e confere o pacote, extrai e devolve a pasta com o Cocytus novo.</summary>
    public async Task<string> DownloadAndExtractAsync(AppRelease release, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        CleanOldDownloads();
        var zip = await Client.DownloadAsync(release, UpdatesDir, progress, cancellationToken);
        var folder = Path.Combine(UpdatesDir, "versao-" + release.Version.ToString(3));
        return await Task.Run(() => UpdateInstaller.Extract(zip, folder), cancellationToken);
    }

    /// <summary>
    /// Inicia o Cocytus novo (da pasta extraída) no modo de instalação. Ele espera este processo fechar, copia os arquivos
    /// e abre o Cocytus atualizado. Se a pasta do programa exigir administrador, o Windows pede a confirmação.
    /// Devolve false se o usuário recusar o aviso de administrador.
    /// </summary>
    public bool LaunchInstaller(string newVersionFolder, AppRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var target = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var start = new ProcessStartInfo(Path.Combine(newVersionFolder, UpdateInstaller.ExecutableName))
        {
            UseShellExecute = true,
            WorkingDirectory = newVersionFolder,
        };
        start.ArgumentList.Add("--aplicar-atualizacao");
        start.ArgumentList.Add(target);
        start.ArgumentList.Add("--pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (_services.Paths.UsesCustomDataRoot)
        {
            start.ArgumentList.Add("--dados");
            start.ArgumentList.Add(_services.Paths.DataRoot);
        }

        if (!UpdateInstaller.CanWrite(target))
        {
            start.Verb = "runas";
        }

        try
        {
            using var process = Process.Start(start);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }

        var message = $"Atualização para a versão {release.Version.ToString(3)} iniciada (de {CurrentVersion.ToString(3)}).";
        _services.Log.Info(message);
        _services.History.AddEvent(HistoryStore.EventUpdate, message);
        return true;
    }

    private async Task OnTimerAsync()
    {
        _timer.Interval = TimeSpan.FromHours(1);
        var settings = _services.Settings;
        if (_checking || !settings.CheckAppUpdates)
        {
            return;
        }

        // Ao abrir sempre consulta; depois, só quando passaram 24 horas da última consulta.
        var firstRun = _timer.Tag is null;
        _timer.Tag = true;
        if (!firstRun && settings.LastAppUpdateCheck is { } last && DateTimeOffset.Now - last < Interval)
        {
            return;
        }

        _checking = true;
        try
        {
            var release = await CheckAsync(manual: false, CancellationToken.None);
            if (release is not null)
            {
                UpdateAvailable?.Invoke(this, release);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
        {
            _services.Log.Info("Não foi possível procurar atualização do Cocytus: " + ex.Message);
        }
        finally
        {
            _checking = false;
        }
    }

    private void CleanOldDownloads()
    {
        if (!Directory.Exists(UpdatesDir))
        {
            return;
        }

        foreach (var entry in new DirectoryInfo(UpdatesDir).EnumerateFileSystemInfos())
        {
            try
            {
                if (entry is DirectoryInfo dir)
                {
                    dir.Delete(recursive: true);
                }
                else
                {
                    entry.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Em uso (por exemplo, o instalador ainda fechando): fica para a próxima.
            }
        }
    }

    private static Version ReadCurrentVersion()
    {
        var text = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? "0.0.0";
        return GitHubReleaseClient.TryParseVersion(text, out var version) ? version : new Version(0, 0, 0);
    }

    public void Dispose()
    {
        _timer.Stop();
        _http.Dispose();
    }
}
