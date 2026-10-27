using System.Security.Cryptography;

namespace Cocytus.Core.Scanning;

/// <summary>
/// Analisa um único arquivo: abre sem bloquear outros programas, aplica os limites de tamanho e de tempo,
/// envia o conteúdo ao motor e classifica o resultado. Usado pela varredura sob demanda e pelo tempo real.
/// </summary>
public sealed class FileScanner
{
    private readonly IScanEngine _engine;
    private readonly ScanOptions _options;

    public FileScanner(IScanEngine engine, ScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        _engine = engine;
        _options = options;
    }

    public async Task<FileScanResult> ScanAsync(string path, CancellationToken token)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
                BufferSize = 0,
            });
        }
        catch (Exception ex) when (FileEnumerator.IsIoError(ex))
        {
            return ReadError(path, FileEnumerator.DescribeIoError(ex));
        }

        await using (stream)
        {
            long size;
            try
            {
                size = stream.Length;
            }
            catch (Exception ex) when (FileEnumerator.IsIoError(ex))
            {
                return ReadError(path, FileEnumerator.DescribeIoError(ex));
            }

            if (size > _options.MaxFileSizeBytes)
            {
                return new FileScanResult(path, ScanOutcome.NotScanned, NotScannedReason.TooLarge, null,
                    $"Tamanho {ScanOrchestrator.FormatBytes(size)} acima do limite de {ScanOrchestrator.FormatBytes(_options.MaxFileSizeBytes)}", size, null);
            }

            EngineVerdict verdict;
            using (var perFile = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                perFile.CancelAfter(_options.PerFileTimeout);
                try
                {
                    verdict = await _engine.ScanAsync(stream, perFile.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    return new FileScanResult(path, ScanOutcome.NotScanned, NotScannedReason.Timeout, null,
                        $"Análise passou do limite de {_options.PerFileTimeout.TotalSeconds:0} s", size, null);
                }
                catch (EngineException ex)
                {
                    return new FileScanResult(path, ScanOutcome.NotScanned, NotScannedReason.EngineError, null, ex.Message, size, null);
                }
                catch (Exception ex) when (FileEnumerator.IsIoError(ex))
                {
                    return ReadError(path, FileEnumerator.DescribeIoError(ex), size);
                }
            }

            switch (verdict.Kind)
            {
                case VerdictKind.NoDetection:
                    return new FileScanResult(path, ScanOutcome.NoThreatDetected, NotScannedReason.None, null, null, size, null);
                case VerdictKind.LimitsExceeded:
                    return new FileScanResult(path, ScanOutcome.NotScanned, NotScannedReason.ArchiveLimitsExceeded, verdict.SignatureName,
                        verdict.Detail ?? "Limites de compactado excedidos; o conteúdo não foi analisado por completo", size, null);
                case VerdictKind.Error:
                    return new FileScanResult(path, ScanOutcome.NotScanned, NotScannedReason.EngineError, null, verdict.Detail, size, null);
                default:
                    var outcome = verdict.Kind == VerdictKind.Detected ? ScanOutcome.Threat : ScanOutcome.Suspicious;
                    var sha = await TryHashAsync(stream, token);
                    return new FileScanResult(path, outcome, NotScannedReason.None, verdict.SignatureName, verdict.Detail, size, sha);
            }
        }
    }

    private static async Task<string?> TryHashAsync(Stream stream, CancellationToken token)
    {
        try
        {
            stream.Position = 0;
            var hash = await SHA256.HashDataAsync(stream, token);
            return Convert.ToHexStringLower(hash);
        }
        catch (Exception ex) when (FileEnumerator.IsIoError(ex))
        {
            return null;
        }
    }

    private static FileScanResult ReadError(string path, string detail, long? size = null) =>
        new(path, ScanOutcome.ReadError, NotScannedReason.None, null, detail, size, null);
}
