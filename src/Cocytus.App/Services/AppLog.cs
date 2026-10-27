using System.Globalization;
using System.Text;

namespace Cocytus.App.Services;

/// <summary>Log local simples (logs\cocytus.log), com rotação por tamanho. Nada é enviado para fora.</summary>
public sealed class AppLog
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private readonly string _path;
    private readonly Lock _lock = new();

    public AppLog(string logsDir)
    {
        Directory.CreateDirectory(logsDir);
        _path = Path.Combine(logsDir, "cocytus.log");
    }

    public string FilePath => _path;

    public void Info(string message) => Write("INFO", message);

    public void Error(string message, Exception? ex = null) => Write("ERRO", ex is null ? message : $"{message}: {ex}");

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} [{level}] {message}{Environment.NewLine}";
        lock (_lock)
        {
            try
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(_path, _path + ".1", overwrite: true);
                }

                File.AppendAllText(_path, line, Encoding.UTF8);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // O log nunca derruba o aplicativo.
            }
        }
    }
}
