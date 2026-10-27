using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cocytus.Core;
using Cocytus.Update;

namespace Cocytus.Tests;

public sealed class AppUpdateTests
{
    private const string Repo = "https://github.com/JoaoGabrielOT/cocytus-antivirus/releases/download/v1.2.3/";

    private static string ReleaseJson(bool draft = false, bool prerelease = false, bool withChecksum = true) => $$"""
        {
          "tag_name": "v1.2.3",
          "name": "Cocytus 1.2.3",
          "body": "Novidades da versão",
          "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}},
          "published_at": "2026-10-26T12:00:00Z",
          "html_url": "https://github.com/JoaoGabrielOT/cocytus-antivirus/releases/tag/v1.2.3",
          "assets": [
            { "name": "outro.txt", "browser_download_url": "{{Repo}}outro.txt", "size": 1 },
            { "name": "Cocytus-1.2.3-win-x64.zip", "browser_download_url": "{{Repo}}Cocytus-1.2.3-win-x64.zip", "size": 1234 }
            {{(withChecksum ? $", {{ \"name\": \"Cocytus-1.2.3-win-x64.zip.sha256\", \"browser_download_url\": \"{Repo}Cocytus-1.2.3-win-x64.zip.sha256\", \"size\": 90 }}" : "")}}
          ]
        }
        """;

    [Fact]
    public void Interpreta_a_release_do_github()
    {
        var release = GitHubReleaseClient.ParseRelease(ReleaseJson());

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 2, 3), release.Version);
        Assert.Equal("Cocytus 1.2.3", release.Title);
        Assert.Equal("Novidades da versão", release.Notes);
        Assert.Equal("Cocytus-1.2.3-win-x64.zip", release.PackageName);
        Assert.Equal(1234, release.PackageSize);
        Assert.EndsWith(".zip.sha256", release.ChecksumUrl.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero), release.PublishedAt);
    }

    [Fact]
    public void Ignora_rascunho_pre_release_e_release_sem_sha256()
    {
        Assert.Null(GitHubReleaseClient.ParseRelease(ReleaseJson(draft: true)));
        Assert.Null(GitHubReleaseClient.ParseRelease(ReleaseJson(prerelease: true)));
        Assert.Null(GitHubReleaseClient.ParseRelease(ReleaseJson(withChecksum: false)));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("0.2.0+abcdef", "0.2.0")]
    [InlineData("V10.0.1-beta", "10.0.1")]
    public void Le_versoes(string text, string expected)
    {
        Assert.True(GitHubReleaseClient.TryParseVersion(text, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("versao")]
    [InlineData("")]
    public void Recusa_versoes_invalidas(string text) => Assert.False(GitHubReleaseClient.TryParseVersion(text, out _));

    [Fact]
    public void Le_o_arquivo_sha256()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, GitHubReleaseClient.ParseChecksum($"{hash.ToUpperInvariant()}  Cocytus-1.2.3-win-x64.zip\n", "Cocytus-1.2.3-win-x64.zip"));
        Assert.Equal(hash, GitHubReleaseClient.ParseChecksum(hash, "Cocytus-1.2.3-win-x64.zip"));
        Assert.Throws<InvalidDataException>(() => GitHubReleaseClient.ParseChecksum($"{hash}  outro.zip", "Cocytus-1.2.3-win-x64.zip"));
        Assert.Throws<InvalidDataException>(() => GitHubReleaseClient.ParseChecksum("xyz", "Cocytus-1.2.3-win-x64.zip"));
    }

    [Fact]
    public async Task Baixa_confere_e_recusa_pacote_alterado()
    {
        using var dir = new TempDir();
        var package = Encoding.UTF8.GetBytes("conteúdo do pacote");
        var hash = Convert.ToHexStringLower(SHA256.HashData(package));
        var release = GitHubReleaseClient.ParseRelease(ReleaseJson())!;

        using (var http = new HttpClient(new FakeHandler(release, package, $"{hash}  {release.PackageName}")))
        {
            var zip = await new GitHubReleaseClient(http).DownloadAsync(release, dir.Path, null, CancellationToken.None);
            Assert.Equal(package, await File.ReadAllBytesAsync(zip));
        }

        using var other = new TempDir();
        using (var http = new HttpClient(new FakeHandler(release, package, $"{new string('0', 64)}  {release.PackageName}")))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubReleaseClient(http).DownloadAsync(release, other.Path, null, CancellationToken.None));
        }

        Assert.Empty(Directory.GetFiles(other.Path));
    }

    [Fact]
    public async Task Sem_release_publicada_devolve_nulo()
    {
        using var http = new HttpClient(new FakeHandler(null, [], ""));

        Assert.Null(await new GitHubReleaseClient(http).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public void Extrai_pacote_e_recusa_caminho_fora_da_pasta()
    {
        using var dir = new TempDir();
        var zip = dir.Combine("pacote.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Write(archive, "Cocytus.exe", "exe");
            Write(archive, @"engines/clamav/clamd.exe", "clamd");
        }

        var folder = UpdateInstaller.Extract(zip, dir.Combine("extraido"));
        Assert.Equal("clamd", File.ReadAllText(Path.Combine(folder, "engines", "clamav", "clamd.exe")));

        var nestedZip = dir.Combine("aninhado.zip");
        using (var archive = ZipFile.Open(nestedZip, ZipArchiveMode.Create))
        {
            Write(archive, "Cocytus/Cocytus.exe", "exe");
        }

        Assert.Equal(dir.Combine("aninhado", "Cocytus"), UpdateInstaller.Extract(nestedZip, dir.Combine("aninhado")));

        var evil = dir.Combine("ruim.zip");
        using (var archive = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            Write(archive, "../fora.txt", "x");
        }

        Assert.Throws<InvalidDataException>(() => UpdateInstaller.Extract(evil, dir.Combine("ruim")));
        Assert.False(File.Exists(dir.Combine("fora.txt")));
    }

    [Fact]
    public void Instala_por_cima_guardando_copia_e_restaura_se_falhar()
    {
        using var dir = new TempDir();
        dir.WriteFile(@"novo\Cocytus.exe", "exe novo");
        dir.WriteFile(@"novo\engines\clamav\clamd.exe", "clamd novo");
        dir.WriteFile(@"novo\zzz-novo.dll", "dll nova");
        dir.WriteFile(@"programa\Cocytus.exe", "exe antigo");
        dir.WriteFile(@"programa\engines\clamav\clamd.exe", "clamd antigo");
        dir.WriteFile(@"programa\so-antigo.txt", "fica");

        var copied = UpdateInstaller.Apply(dir.Combine("novo"), dir.Combine("programa"), dir.Combine("copia"));

        Assert.Equal(3, copied);
        Assert.Equal("exe novo", File.ReadAllText(dir.Combine("programa", "Cocytus.exe")));
        Assert.Equal("clamd novo", File.ReadAllText(dir.Combine("programa", "engines", "clamav", "clamd.exe")));
        Assert.Equal("fica", File.ReadAllText(dir.Combine("programa", "so-antigo.txt")));
        Assert.Equal("exe antigo", File.ReadAllText(dir.Combine("copia", "Cocytus.exe")));

        // Falha no meio: um arquivo do programa preso por outro processo. Tudo volta como estava.
        dir.WriteFile(@"novo2\Cocytus.exe", "exe 3");
        dir.WriteFile(@"novo2\zzz-novo.dll", "dll 3");
        dir.WriteFile(@"novo2\zzzz-criado.dll", "criado");
        using (new FileStream(dir.Combine("programa", "zzz-novo.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() =>
                UpdateInstaller.Apply(dir.Combine("novo2"), dir.Combine("programa"), dir.Combine("copia2"), lockWait: TimeSpan.FromMilliseconds(300)));
        }

        Assert.Equal("exe novo", File.ReadAllText(dir.Combine("programa", "Cocytus.exe")));
        Assert.Equal("dll nova", File.ReadAllText(dir.Combine("programa", "zzz-novo.dll")));
        Assert.False(File.Exists(dir.Combine("programa", "zzzz-criado.dll")));
        Assert.True(UpdateInstaller.CanWrite(dir.Path));
    }

    [Fact]
    public void Linha_de_comando_do_instalador()
    {
        var options = CommandLineOptions.Parse(["--aplicar-atualizacao", @"C:\Programa\Cocytus", "--pid", "4321", "--dados", @"D:\dados"]);

        Assert.Equal(@"C:\Programa\Cocytus", options.ApplyUpdateTarget);
        Assert.Equal(4321, options.WaitForProcessId);
        Assert.Empty(options.Paths);
        Assert.True(CommandLineOptions.Parse(["--atualizado"]).JustUpdated);
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(content);
    }

    private sealed class FakeHandler(AppRelease? release, byte[] package, string checksum) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            HttpResponseMessage response;
            if (release is null)
            {
                response = new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            else if (uri == release.PackageUrl)
            {
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) };
            }
            else if (uri == release.ChecksumUrl)
            {
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(checksum) };
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return Task.FromResult(response);
        }
    }
}
