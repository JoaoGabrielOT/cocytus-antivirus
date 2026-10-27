using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cocytus.Update;

/// <summary>Uma release do Cocytus publicada no GitHub, com o pacote para Windows x64.</summary>
public sealed record AppRelease(
    Version Version,
    string Tag,
    string Title,
    string Notes,
    DateTimeOffset? PublishedAt,
    string PageUrl,
    Uri PackageUrl,
    string PackageName,
    long PackageSize,
    Uri ChecksumUrl);

/// <summary>
/// Consulta as releases do Cocytus no GitHub (repositório público) e baixa o pacote de atualização.
/// A única informação enviada é a requisição HTTP (com "Cocytus/versão" no User-Agent); nada do computador é enviado.
/// </summary>
public sealed class GitHubReleaseClient
{
    public const string Repository = "JoaoGabrielOT/cocytus-antivirus";
    public const string RepositoryUrl = "https://github.com/" + Repository;
    private readonly HttpClient _http;
    private readonly string _repository;

    public GitHubReleaseClient(HttpClient http, string repository = Repository)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _repository = repository;
    }

    public static HttpClient CreateHttpClient(Version currentVersion)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Cocytus", currentVersion.ToString(3)));
        return http;
    }

    public static string PackageNameFor(Version version) => $"Cocytus-{version.ToString(3)}-win-x64.zip";

    /// <summary>Última release estável, ou null se o repositório ainda não tem release.</summary>
    public async Task<AppRelease?> GetLatestAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Forbidden || (int)response.StatusCode == 429)
        {
            throw new HttpRequestException("O GitHub limitou as consultas deste computador por enquanto. Tente de novo mais tarde.", null, response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseRelease(json);
    }

    /// <summary>Interpreta a resposta de /releases/latest. Ignora rascunhos, pré-releases e releases sem pacote ou sem SHA-256.</summary>
    public static AppRelease? ParseRelease(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (Bool(root, "draft") || Bool(root, "prerelease"))
        {
            return null;
        }

        var tag = Text(root, "tag_name");
        if (tag is null || !TryParseVersion(tag, out var version))
        {
            return null;
        }

        var packageName = PackageNameFor(version);
        Uri? package = null;
        Uri? checksum = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name");
                var url = Text(asset, "browser_download_url");
                if (name is null || url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                {
                    continue;
                }

                if (name.Equals(packageName, StringComparison.OrdinalIgnoreCase))
                {
                    package = uri;
                    size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                }
                else if (name.Equals(packageName + ".sha256", StringComparison.OrdinalIgnoreCase))
                {
                    checksum = uri;
                }
            }
        }

        if (package is null || checksum is null)
        {
            return null;
        }

        DateTimeOffset? published = DateTimeOffset.TryParse(Text(root, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
        return new AppRelease(version, tag, Text(root, "name") ?? tag, Text(root, "body") ?? "", published,
            Text(root, "html_url") ?? RepositoryUrl + "/releases", package, packageName, size, checksum);
    }

    /// <summary>Aceita "v1.2.3" e "1.2.3".</summary>
    public static bool TryParseVersion(string text, out Version version)
    {
        version = new Version(0, 0, 0);
        var trimmed = text.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
        {
            trimmed = trimmed[1..];
        }

        var plus = trimmed.IndexOfAny(['+', '-']);
        if (plus >= 0)
        {
            trimmed = trimmed[..plus];
        }

        if (!Version.TryParse(trimmed, out var parsed) || parsed.Build < 0)
        {
            return false;
        }

        version = new Version(parsed.Major, parsed.Minor, parsed.Build);
        return true;
    }

    /// <summary>Baixa o pacote para <paramref name="folder"/> e confere o SHA-256 publicado junto. Devolve o caminho do zip.</summary>
    public async Task<string> DownloadAsync(AppRelease release, string folder, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        Directory.CreateDirectory(folder);
        var expected = ParseChecksum(await _http.GetStringAsync(release.ChecksumUrl, cancellationToken), release.PackageName);
        var target = Path.Combine(folder, release.PackageName);
        var partial = target + ".parcial";
        using (var response = await _http.GetAsync(release.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.PackageSize;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                done += read;
                if (total > 0)
                {
                    progress?.Report(Math.Min(1.0, (double)done / total));
                }
            }
        }

        string actual;
        await using (var stream = File.OpenRead(partial))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        }

        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new InvalidDataException($"O pacote baixado não confere com o SHA-256 publicado (esperado {expected}, obtido {actual}). A atualização foi cancelada.");
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>Formato do sha256sum: "hash  nome-do-arquivo" (ou só o hash).</summary>
    public static string ParseChecksum(string content, string packageName)
    {
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit)
                && (parts.Length == 1 || parts[1].TrimStart('*').Equals(packageName, StringComparison.OrdinalIgnoreCase)))
            {
                return parts[0].ToLowerInvariant();
            }
        }

        throw new InvalidDataException("O arquivo .sha256 da release não tem um SHA-256 válido para " + packageName + ".");
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}

/// <summary>
/// Instala um pacote baixado: extrai numa pasta própria e, depois que o Cocytus antigo fecha, copia os arquivos novos
/// por cima da pasta do programa, guardando cópia dos substituídos para voltar atrás se algo falhar.
/// Os dados do usuário (bases, quarentena, histórico, configurações) ficam em outra pasta e não são tocados.
/// </summary>
public static class UpdateInstaller
{
    public const string ExecutableName = "Cocytus.exe";

    /// <summary>Extrai o zip em <paramref name="destination"/> (vazia) e devolve a pasta que contém o Cocytus.exe.</summary>
    public static string Extract(string zipPath, string destination)
    {
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                var target = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("O pacote tem um caminho fora da pasta de destino: " + entry.FullName);
                }

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }

        if (File.Exists(Path.Combine(destination, ExecutableName)))
        {
            return destination;
        }

        var nested = Directory.GetDirectories(destination).FirstOrDefault(d => File.Exists(Path.Combine(d, ExecutableName)));
        return nested ?? throw new InvalidDataException("O pacote não contém o " + ExecutableName + ".");
    }

    /// <summary>
    /// Copia todos os arquivos de <paramref name="source"/> para <paramref name="target"/>. Arquivos substituídos vão para
    /// <paramref name="backup"/>; em caso de erro, tudo é restaurado e a exceção é repassada.
    /// </summary>
    public static int Apply(string source, string target, string backup, Action<string>? log = null, TimeSpan? lockWait = null)
    {
        var wait = lockWait ?? TimeSpan.FromSeconds(30);
        if (Directory.Exists(backup))
        {
            Directory.Delete(backup, recursive: true);
        }

        Directory.CreateDirectory(backup);
        var replaced = new List<string>();
        var created = new List<string>();
        var files = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in files)
            {
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (File.Exists(destination))
                {
                    var saved = Path.Combine(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    Retry(() => File.Copy(destination, saved, overwrite: true), wait);
                    replaced.Add(relative);
                }
                else
                {
                    created.Add(relative);
                }

                Retry(() => File.Copy(file, destination, overwrite: true), wait);
            }

            log?.Invoke($"{files.Length} arquivo(s) copiados para {target}.");
            return files.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke("Falha ao copiar; restaurando a versão anterior: " + ex.Message);
            foreach (var relative in replaced)
            {
                TryCopy(Path.Combine(backup, relative), Path.Combine(target, relative));
            }

            foreach (var relative in created)
            {
                TryDelete(Path.Combine(target, relative));
            }

            throw;
        }
    }

    /// <summary>A pasta do programa aceita escrita sem administrador?</summary>
    public static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, ".cocytus-teste-escrita-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Retry(Action action, TimeSpan wait)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                action();
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                // Arquivo ainda em uso (o Cocytus antigo ou o clamd terminando de fechar).
                Thread.Sleep(500);
            }
        }
    }

    private static void TryCopy(string from, string to)
    {
        try
        {
            File.Copy(from, to, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Melhor esforço na restauração.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Melhor esforço na restauração.
        }
    }
}
