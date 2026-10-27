using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Cocytus.Core;
using Cocytus.Engines.Native;

namespace Cocytus.Engines.ClamAv;

/// <summary>
/// Motor ClamAV (GPL-2.0) rodando como processo filho (clamd.exe), ouvindo só em 127.0.0.1 numa porta
/// livre escolhida a cada início. O conteúdo é enviado por INSTREAM: o clamd nunca abre caminhos do
/// usuário, o que evita problemas com acentos, espaços e permissões, e o arquivo nunca é executado.
/// </summary>
public sealed class ClamAvEngine : IScanEngine
{
    public const string EngineName = "ClamAV";
    private const int ChunkSize = 64 * 1024;
    private const int MaxConsoleLines = 40;

    private readonly ClamAvOptions _options;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly Queue<string> _console = new();
    private readonly Lock _consoleLock = new();
    private Process? _process;
    private int _port;
    private EngineInfo? _info;
    private bool _disposed;

    public ClamAvEngine(ClamAvOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public string Name => EngineName;

    public bool IsRunning
    {
        get
        {
            var process = _process;
            if (process is null || _info is null)
            {
                return false;
            }

            try
            {
                return !process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public ClamAvOptions Options => _options;

    public async Task<EngineInfo> EnsureReadyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning)
        {
            return _info!;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (IsRunning)
            {
                return _info!;
            }

            StopProcess();
            return await StartAsync(cancellationToken);
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task<EngineInfo> StartAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.ClamdExe))
        {
            throw new EngineUnavailableException($"ClamAV não encontrado em {_options.ClamAvDir}.");
        }

        if (_options.RequireOfficialDatabase && !ClamAvDatabase.HasOfficialDatabase(_options.DatabaseDir))
        {
            throw new EngineUnavailableException("As bases de assinaturas do ClamAV não estão instaladas. Abra \"Atualização\" e clique em \"Atualizar bases\".");
        }

        foreach (var dir in new[] { _options.ConfigDir, _options.TempDir, _options.LogsDir })
        {
            Directory.CreateDirectory(dir);
        }

        if (_options.InstallTestSignature)
        {
            ClamAvDatabase.EnsureTestSignature(_options.DatabaseDir);
        }

        var port = GetFreeLoopbackPort();
        var configPath = Path.Combine(_options.ConfigDir, "clamd.conf");
        await File.WriteAllTextAsync(configPath, _options.BuildClamdConfig(port), new UTF8Encoding(false), cancellationToken);

        lock (_consoleLock)
        {
            _console.Clear();
        }

        var startInfo = new ProcessStartInfo(_options.ClamdExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = _options.ClamAvDir,
        };
        startInfo.ArgumentList.Add("--config-file=" + configPath);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new EngineUnavailableException("Não foi possível iniciar o clamd.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new EngineUnavailableException("Não foi possível iniciar o clamd: " + ex.Message, ex);
        }

        ChildProcessJob.TryAssign(process);
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (InvalidOperationException)
        {
            // O processo pode ter terminado logo ao iniciar; tratado abaixo.
        }

        process.OutputDataReceived += (_, e) => AddConsole(e.Data);
        process.ErrorDataReceived += (_, e) => AddConsole(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;
        _port = port;

        var deadline = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                var code = process.ExitCode;
                StopProcess();
                throw new EngineUnavailableException($"O clamd encerrou ao iniciar (código {code}). {LastConsoleLines()}");
            }

            try
            {
                using var ping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                ping.CancelAfter(TimeSpan.FromSeconds(3));
                var reply = await CommandAsync(port, "zPING\0", ping.Token);
                if (reply == "PONG")
                {
                    break;
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested
                                       && ex is SocketException or IOException or OperationCanceledException)
            {
                // Ainda carregando as bases.
            }

            if (deadline.Elapsed > _options.StartupTimeout)
            {
                StopProcess();
                throw new EngineUnavailableException($"O clamd não respondeu em {_options.StartupTimeout.TotalMinutes:0} min. {LastConsoleLines()}");
            }

            await Task.Delay(500, cancellationToken);
        }

        string engineVersion;
        string? dbVersion;
        try
        {
            (engineVersion, dbVersion) = ClamdProtocol.ParseVersion(await CommandAsync(port, "zVERSION\0", cancellationToken));
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            StopProcess();
            throw new EngineUnavailableException("O clamd iniciou mas não respondeu ao comando VERSION: " + ex.Message, ex);
        }

        var daily = ClamAvDatabase.ReadAll(_options.DatabaseDir)
            .FirstOrDefault(d => d.Name.Equals("daily", StringComparison.OrdinalIgnoreCase));
        _info = new EngineInfo(EngineName, engineVersion, dbVersion ?? daily?.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            daily?.BuildTime, _options.DescribeLimitations());
        return _info;
    }

    public async Task<EngineVerdict> ScanAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        await EnsureReadyAsync(cancellationToken);
        var port = _port;

        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        }
        catch (SocketException ex)
        {
            throw new EngineException("Não foi possível conectar ao clamd: " + ex.Message, ex);
        }

        var network = client.GetStream();
        var buffer = ArrayPool<byte>.Shared.Rent(ChunkSize + 4);
        try
        {
            await WriteToEngineAsync(network, Encoding.ASCII.GetBytes("zINSTREAM\0"), cancellationToken);
            while (true)
            {
                // Erros de leitura do arquivo sobem como IOException (erro de leitura, não do motor).
                var read = await content.ReadAsync(buffer.AsMemory(4, ChunkSize), cancellationToken);
                BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)read);
                await WriteToEngineAsync(network, buffer.AsMemory(0, 4 + read), cancellationToken);
                if (read == 0)
                {
                    break;
                }
            }

            string reply;
            try
            {
                reply = await ReadReplyAsync(network, cancellationToken);
            }
            catch (IOException ex)
            {
                throw new EngineException("O clamd encerrou a conexão sem responder: " + ex.Message, ex);
            }

            return ClamdProtocol.ParseScanResponse(reply);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WriteToEngineAsync(NetworkStream network, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        try
        {
            await network.WriteAsync(data, cancellationToken);
        }
        catch (IOException ex)
        {
            throw new EngineException("Falha ao enviar o conteúdo ao clamd: " + ex.Message, ex);
        }
    }

    private static async Task<string> CommandAsync(int port, string command, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        var network = client.GetStream();
        await network.WriteAsync(Encoding.ASCII.GetBytes(command), cancellationToken);
        return await ReadReplyAsync(network, cancellationToken);
    }

    private static async Task<string> ReadReplyAsync(NetworkStream network, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await network.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (Array.IndexOf(buffer, (byte)0, total - read, read) >= 0)
            {
                break;
            }
        }

        if (total == 0)
        {
            throw new IOException("Resposta vazia do clamd.");
        }

        return Encoding.UTF8.GetString(buffer, 0, total).TrimEnd('\0', '\n', '\r');
    }

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private void AddConsole(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_consoleLock)
        {
            _console.Enqueue(line);
            while (_console.Count > MaxConsoleLines)
            {
                _console.Dequeue();
            }
        }
    }

    private string LastConsoleLines()
    {
        lock (_consoleLock)
        {
            var lines = _console.TakeLast(8).ToArray();
            return lines.Length == 0 ? "Sem saída do processo." : "Últimas mensagens: " + string.Join(" | ", lines);
        }
    }

    /// <summary>Encerra o clamd (libera a memória das bases). Ele volta a subir na próxima varredura.</summary>
    public async Task StopAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            StopProcess();
        }
        finally
        {
            _startLock.Release();
        }
    }

    private void StopProcess()
    {
        _info = null;
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Já terminou.
        }
        finally
        {
            process.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync();
        _disposed = true;
        _startLock.Dispose();
    }
}
