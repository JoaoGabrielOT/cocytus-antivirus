using System.Security.Cryptography;
using Cocytus.Data.Native;

namespace Cocytus.Data.Quarantine;

public sealed record QuarantineItem(
    string Id,
    string OriginalPath,
    string Sha256,
    long Size,
    string Reason,
    string Engine,
    string? DatabaseVersion,
    DateTimeOffset QuarantinedAt);

public sealed record QuarantineAddResult(QuarantineItem Item, bool OriginalStillPresent);

public class QuarantineException : Exception
{
    public QuarantineException()
    {
    }

    public QuarantineException(string message)
        : base(message)
    {
    }

    public QuarantineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>O destino da restauração já existe; nada foi sobrescrito.</summary>
public sealed class RestoreConflictException : QuarantineException
{
    public RestoreConflictException()
    {
        TargetPath = string.Empty;
    }

    public RestoreConflictException(string targetPath)
        : base($"Já existe um arquivo ou pasta em \"{targetPath}\". O Cocytus não sobrescreve arquivos ao restaurar.")
    {
        TargetPath = targetPath;
    }

    public RestoreConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
        TargetPath = string.Empty;
    }

    public string TargetPath { get; }
}

/// <summary>
/// Quarentena reversível. O original é aberto por um handle que impede escrita e exclusão por outros
/// processos; o conteúdo é cifrado (AES-256, chave aleatória por item), a cópia é conferida pelo
/// SHA-256 e só então o original é removido pelo mesmo handle. A cifragem serve para impedir execução
/// acidental e remoção da cópia por outros antivírus; a chave fica junto do registro, então não é sigilo.
/// </summary>
public sealed class QuarantineStore
{
    private const int FormatVersion = 1;
    private const long FreeSpaceMargin = 64L * 1024 * 1024;
    private readonly string _dir;
    private readonly Lock _lock = new();

    public QuarantineStore(string directory)
    {
        _dir = Path.GetFullPath(directory);
        Directory.CreateDirectory(_dir);
    }

    public string FolderPath => _dir;

    private sealed class Record
    {
        public int FormatVersion { get; set; }

        public QuarantineItem? Item { get; set; }

        public string Key { get; set; } = "";

        public string IV { get; set; } = "";
    }

    public IReadOnlyList<QuarantineItem> List()
    {
        lock (_lock)
        {
            return Directory.EnumerateFiles(_dir, "*.json")
                .Select(f => AtomicFile.ReadJson<Record>(f)?.Item)
                .OfType<QuarantineItem>()
                .OrderByDescending(i => i.QuarantinedAt)
                .ToList();
        }
    }

    /// <summary>
    /// Move o arquivo para a quarentena. Se <paramref name="expectedSha256"/> for informado e o conteúdo
    /// tiver mudado desde a detecção, nada é feito.
    /// </summary>
    public QuarantineAddResult Add(string path, string reason, string engine, string? databaseVersion, string? expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        lock (_lock)
        {
            if (new FileInfo(fullPath).LinkTarget is not null)
            {
                throw new QuarantineException("O caminho é um link simbólico ou junção. Nada foi movido.");
            }

            FileStream original;
            try
            {
                original = new FileStream(FileNative.OpenForQuarantine(fullPath), FileAccess.Read, 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new QuarantineException($"Não foi possível abrir o arquivo com acesso exclusivo: {ex.Message}", ex);
            }

            using (original)
            {
                var attributes = File.GetAttributes(original.SafeFileHandle);
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    throw new QuarantineException("O caminho aponta para uma pasta, não para um arquivo. Nada foi movido.");
                }

                var size = original.Length;
                EnsureFreeSpace(size);

                var id = Guid.NewGuid().ToString("N");
                var blobPath = BlobPath(id);
                var recordPath = RecordPath(id);
                var tempBlob = blobPath + ".tmp";
                using var aes = Aes.Create();
                aes.KeySize = 256;
                aes.GenerateKey();
                aes.GenerateIV();

                string sha;
                try
                {
                    sha = EncryptTo(original, tempBlob, aes);
                    if (expectedSha256 is not null && !sha.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new QuarantineException("O conteúdo do arquivo mudou desde a detecção (SHA-256 diferente). Nada foi movido; verifique de novo.");
                    }

                    var check = HashDecrypted(tempBlob, aes.Key, aes.IV);
                    if (!check.Equals(sha, StringComparison.Ordinal))
                    {
                        throw new QuarantineException("A cópia cifrada não conferiu com o original. Nada foi movido.");
                    }

                    File.Move(tempBlob, blobPath);
                    var item = new QuarantineItem(id, fullPath, sha, size, reason, engine, databaseVersion, DateTimeOffset.Now);
                    AtomicFile.WriteJson(recordPath, new Record
                    {
                        FormatVersion = FormatVersion,
                        Item = item,
                        Key = Convert.ToBase64String(aes.Key),
                        IV = Convert.ToBase64String(aes.IV),
                    });

                    try
                    {
                        FileNative.MarkForDeletion(original.SafeFileHandle);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        TryDelete(blobPath);
                        TryDelete(recordPath);
                        throw new QuarantineException($"Não foi possível remover o original ({ex.Message}). A cópia foi descartada e o arquivo continua no lugar.", ex);
                    }

                    original.Dispose();
                    return new QuarantineAddResult(item, File.Exists(fullPath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
                {
                    TryDelete(tempBlob);
                    TryDelete(blobPath);
                    TryDelete(recordPath);
                    throw new QuarantineException($"Falha ao gravar na quarentena: {ex.Message}. O original não foi alterado.", ex);
                }
                catch (QuarantineException)
                {
                    TryDelete(tempBlob);
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Restaura o arquivo no caminho original ou em <paramref name="destinationPath"/>. Nunca sobrescreve:
    /// se o destino existir, lança <see cref="RestoreConflictException"/>.
    /// </summary>
    public string Restore(string id, string? destinationPath = null)
    {
        lock (_lock)
        {
            var record = LoadRecord(id);
            var item = record.Item!;
            var target = Path.GetFullPath(destinationPath ?? item.OriginalPath);
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new RestoreConflictException(target);
            }

            var folder = Path.GetDirectoryName(target) ?? throw new QuarantineException("Destino inválido.");
            Directory.CreateDirectory(folder);
            var temp = Path.Combine(folder, $".cocytus-restaurando-{Guid.NewGuid():N}.tmp");
            try
            {
                string sha;
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var input = OpenDecrypted(BlobPath(id), Convert.FromBase64String(record.Key), Convert.FromBase64String(record.IV)))
                {
                    sha = CopyWithHash(input, output);
                    output.Flush(flushToDisk: true);
                }

                if (!sha.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new QuarantineException("A cópia na quarentena está corrompida (SHA-256 não confere). Nada foi restaurado.");
                }

                try
                {
                    File.Move(temp, target, overwrite: false);
                }
                catch (IOException) when (File.Exists(target))
                {
                    throw new RestoreConflictException(target);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                throw new QuarantineException($"Falha ao restaurar: {ex.Message}", ex);
            }
            finally
            {
                TryDelete(temp);
            }

            TryDelete(BlobPath(id));
            TryDelete(RecordPath(id));
            return target;
        }
    }

    /// <summary>Exclui definitivamente o item da quarentena.</summary>
    public void Delete(string id)
    {
        lock (_lock)
        {
            LoadRecord(id);
            File.Delete(BlobPath(id));
            File.Delete(RecordPath(id));
        }
    }

    private Record LoadRecord(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new QuarantineException("Identificador de quarentena inválido.");
        }

        var record = AtomicFile.ReadJson<Record>(RecordPath(id));
        if (record?.Item is null || !File.Exists(BlobPath(id)))
        {
            throw new QuarantineException("Item não encontrado na quarentena.");
        }

        return record;
    }

    private void EnsureFreeSpace(long size)
    {
        var root = Path.GetPathRoot(_dir);
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        try
        {
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < size + FreeSpaceMargin)
            {
                throw new QuarantineException("Espaço livre insuficiente no disco da quarentena. Nada foi movido.");
            }
        }
        catch (ArgumentException)
        {
            // Caminho de rede: sem checagem prévia; uma falha de gravação é tratada adiante.
        }
    }

    private static string EncryptTo(Stream source, string destination, Aes aes)
    {
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        string sha;
        using (var encryptor = aes.CreateEncryptor())
        using (var crypto = new CryptoStream(output, encryptor, CryptoStreamMode.Write, leaveOpen: true))
        {
            sha = CopyWithHash(source, crypto);
            crypto.FlushFinalBlock();
        }

        output.Flush(flushToDisk: true);
        return sha;
    }

    private static string HashDecrypted(string blobPath, byte[] key, byte[] iv)
    {
        using var input = OpenDecrypted(blobPath, key, iv);
        return CopyWithHash(input, Stream.Null);
    }

    private static CryptoStream OpenDecrypted(string blobPath, byte[] key, byte[] iv)
    {
        var file = new FileStream(blobPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        return new CryptoStream(file, aes.CreateDecryptor(), CryptoStreamMode.Read, leaveOpen: false);
    }

    private static string CopyWithHash(Stream input, Stream output)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private string BlobPath(string id) => Path.Combine(_dir, id + ".cqz");

    private string RecordPath(string id) => Path.Combine(_dir, id + ".json");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Melhor esforço.
        }
    }
}
