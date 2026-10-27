using System.Diagnostics;
using System.Text;
using Cocytus.Engines.ClamAv;
using Cocytus.Engines.Native;

namespace Cocytus.Update;

public sealed record UpdateResult(bool Success, bool Changed, int ExitCode, string Message, IReadOnlyList<ClamAvDatabaseFile> Databases);

/// <summary>
/// Atualiza as bases oficiais do ClamAV com o freshclam (servidor database.clamav.net, gratuito).
/// O freshclam confere a assinatura digital de cada base e, com TestDatabases, carrega a base nova
/// num motor de teste antes de substituir a anterior. Se algo falhar, as bases anteriores ficam.
/// Nenhum arquivo do usuário é enviado: o tráfego é só o download das bases.
/// </summary>
public sealed class FreshclamUpdater
{
    public const string Server = "database.clamav.net";
    private readonly ClamAvOptions _options;

    public FreshclamUpdater(ClamAvOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public string BuildConfig()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Gerado pelo Cocytus a cada atualização. Alterações manuais são sobrescritas.");
        sb.AppendLine($"DatabaseDirectory \"{_options.DatabaseDir}\"");
        sb.AppendLine($"UpdateLogFile \"{Path.Combine(_options.LogsDir, "freshclam.log")}\"");
        sb.AppendLine("LogFileMaxSize 2M");
        sb.AppendLine("LogRotate yes");
        sb.AppendLine("LogTime yes");
        sb.AppendLine($"DatabaseMirror {Server}");
        sb.AppendLine("DNSDatabaseInfo current.cvd.clamav.net");
        sb.AppendLine("ConnectTimeout 30");
        sb.AppendLine("TestDatabases yes");
        sb.AppendLine("Bytecode yes");
        return sb.ToString();
    }

    public async Task<UpdateResult> UpdateAsync(IProgress<string>? output, CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.FreshclamExe))
        {
            return new UpdateResult(false, false, -1, $"freshclam.exe não encontrado em {_options.ClamAvDir}.", ClamAvDatabase.ReadAll(_options.DatabaseDir));
        }

        Directory.CreateDirectory(_options.DatabaseDir);
        var before = Fingerprint(ClamAvDatabase.ReadAll(_options.DatabaseDir));
        Directory.CreateDirectory(_options.ConfigDir);
        Directory.CreateDirectory(_options.LogsDir);
        var configPath = Path.Combine(_options.ConfigDir, "freshclam.conf");
        await File.WriteAllTextAsync(configPath, BuildConfig(), new UTF8Encoding(false), cancellationToken);

        var startInfo = new ProcessStartInfo(_options.FreshclamExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _options.ClamAvDir,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.ArgumentList.Add("--config-file=" + configPath);
        startInfo.ArgumentList.Add("--stdout");

        var transcript = new StringBuilder();
        var transcriptLock = new Lock();
        void OnLine(string? line)
        {
            // Linhas da barra de progresso ("Time: ... ETA ...") só poluem o registro.
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("Time:", StringComparison.Ordinal))
            {
                return;
            }

            lock (transcriptLock)
            {
                transcript.AppendLine(line);
            }

            output?.Report(line);
        }

        using var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new UpdateResult(false, false, -1, "Não foi possível iniciar o freshclam: " + ex.Message, ClamAvDatabase.ReadAll(_options.DatabaseDir));
        }

        ChildProcessJob.TryAssign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Já terminou.
            }

            throw;
        }

        // Garante que as últimas linhas foram lidas.
        process.WaitForExit();
        string text;
        lock (transcriptLock)
        {
            text = transcript.ToString();
        }

        var code = process.ExitCode;
        var databases = ClamAvDatabase.ReadAll(_options.DatabaseDir);
        // O código de saída não distingue "atualizado" de "já estava atualizado" em todas as versões:
        // a mudança é detectada comparando as versões das bases antes e depois.
        var changed = before != Fingerprint(databases);
        if (code is 0 or 1)
        {
            return changed
                ? new UpdateResult(true, true, code, "Bases atualizadas e verificadas pelo freshclam.", databases)
                : new UpdateResult(true, false, code, "As bases já estavam atualizadas (conferido agora no servidor).", databases);
        }

        return new UpdateResult(false, changed, code, DescribeFailure(code, text), databases);
    }

    internal static string Fingerprint(IReadOnlyList<ClamAvDatabaseFile> databases) =>
        string.Join(";", databases.OrderBy(d => d.FileName, StringComparer.OrdinalIgnoreCase).Select(d => $"{d.FileName}={d.Version}"));

    internal static string DescribeFailure(int code, string transcript)
    {
        if (transcript.Contains("429", StringComparison.Ordinal) || transcript.Contains("cool-down", StringComparison.OrdinalIgnoreCase))
        {
            return "O servidor do ClamAV pediu para aguardar antes de novos downloads (limite de requisições). As bases anteriores foram mantidas; tente mais tarde.";
        }

        var detail = code switch
        {
            52 => "falha de rede ao contatar " + Server,
            54 => "a verificação de integridade ou da assinatura digital da base falhou; nada foi substituído",
            56 => "erro no arquivo de configuração do freshclam",
            58 => "não foi possível baixar a base do servidor",
            59 => "os servidores ainda não estão sincronizados",
            50 or 53 or 55 or 57 => "erro ao gravar ou ler arquivos na pasta de bases (permissão ou disco)",
            62 => "não foi possível abrir o arquivo de log do freshclam",
            _ => "erro desconhecido",
        };
        return $"A atualização falhou (código {code}: {detail}). As bases anteriores foram mantidas.";
    }
}
