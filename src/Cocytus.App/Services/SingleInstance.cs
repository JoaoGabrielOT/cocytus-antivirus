using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Cocytus.App.Services;

/// <summary>Mensagem enviada por uma segunda execução do Cocytus para a instância já aberta.</summary>
public sealed record InstanceMessage(string Command, string[] Paths)
{
    public const string Scan = "verificar";
    public const string Show = "mostrar";
}

/// <summary>
/// Garante uma única instância por usuário. As execuções seguintes (por exemplo, uma por arquivo selecionado
/// no menu do botão direito) entregam os caminhos à instância aberta por um named pipe restrito ao usuário atual.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Cocytus.Instancia.v1";
    private readonly Mutex _mutex;
    private CancellationTokenSource? _cts;

    private SingleInstance(Mutex mutex, bool isPrimary)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }

    private static string PipeName
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return $"Cocytus.Instancia.{identity.User?.Value}.{Process.GetCurrentProcess().SessionId}";
        }
    }

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(true, MutexName, out var createdNew);
        return new SingleInstance(mutex, createdNew);
    }

    /// <summary>Entrega a mensagem à instância aberta. Espera até <paramref name="timeout"/> (ela pode estar iniciando).</summary>
    public static bool TrySend(InstanceMessage message, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var payload = JsonSerializer.Serialize(message);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
                var remaining = (int)Math.Max(100, (deadline - DateTime.UtcNow).TotalMilliseconds);
                client.Connect(remaining);
                using var writer = new StreamWriter(client, new UTF8Encoding(false));
                writer.WriteLine(payload);
                writer.Flush();
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
            catch (IOException)
            {
                Thread.Sleep(150);
            }
        }

        return false;
    }

    public void StartServer(Action<InstanceMessage> onMessage)
    {
        if (!IsPrimary || _cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => ServeAsync(onMessage, token), CancellationToken.None);
    }

    private static async Task ServeAsync(Action<InstanceMessage> onMessage, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(token);
                if (!string.IsNullOrWhiteSpace(line) && JsonSerializer.Deserialize<InstanceMessage>(line) is { } message)
                {
                    onMessage(message);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Cliente desconectou ou mandou lixo: segue esperando o próximo.
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Liberado em outra thread.
            }
        }

        _mutex.Dispose();
    }
}
