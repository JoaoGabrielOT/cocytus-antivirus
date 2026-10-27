using System.Security.Cryptography;
using System.Text;
using Cocytus.Data.Quarantine;

namespace Cocytus.Tests;

public sealed class QuarantineTests
{
    private const string Marker = "CONTEUDO-DESCARTAVEL-DE-TESTE-7d1f";

    private static string Sha(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Move_para_quarentena_e_restaura_sem_perda()
    {
        using var dir = new TempDir();
        var file = dir.WriteFile(Path.Combine("pasta ação", "arquivo de teste ç.txt"), Marker + " " + new string('x', 300_000));
        var original = File.ReadAllBytes(file);
        var sha = Sha(file);
        var store = new QuarantineStore(dir.Combine("quarentena"));

        var result = store.Add(file, "Teste.Assinatura", "ClamAV", "28163", sha);

        Assert.False(File.Exists(file));
        Assert.False(result.OriginalStillPresent);
        Assert.Equal(sha, result.Item.Sha256);
        Assert.Equal(file, result.Item.OriginalPath);
        Assert.Equal(original.LongLength, result.Item.Size);
        var item = Assert.Single(store.List());
        Assert.Equal("Teste.Assinatura", item.Reason);

        // A cópia guardada não contém o conteúdo em claro (não executável, não reconhecível).
        foreach (var stored in Directory.EnumerateFiles(store.FolderPath))
        {
            Assert.DoesNotContain(Marker, Encoding.Latin1.GetString(File.ReadAllBytes(stored)), StringComparison.Ordinal);
        }

        var restored = store.Restore(item.Id);

        Assert.Equal(file, restored);
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.Empty(store.List());
        Assert.Empty(Directory.EnumerateFiles(store.FolderPath));
    }

    [Fact]
    public void Conteudo_alterado_desde_a_deteccao_nao_e_movido()
    {
        using var dir = new TempDir();
        var file = dir.WriteFile("a.txt", Marker);
        var store = new QuarantineStore(dir.Combine("q"));

        Assert.Throws<QuarantineException>(() => store.Add(file, "x", "ClamAV", null, new string('0', 64)));

        Assert.True(File.Exists(file));
        Assert.Equal(Marker, File.ReadAllText(file));
        Assert.Empty(store.List());
        Assert.Empty(Directory.EnumerateFiles(store.FolderPath));
    }

    [Fact]
    public void Restauracao_nao_sobrescreve_arquivo_existente()
    {
        using var dir = new TempDir();
        var file = dir.WriteFile("doc.txt", Marker);
        var store = new QuarantineStore(dir.Combine("q"));
        var item = store.Add(file, "x", "ClamAV", null, Sha(file)).Item;
        File.WriteAllText(file, "arquivo novo do usuário");

        var conflict = Assert.Throws<RestoreConflictException>(() => store.Restore(item.Id));

        Assert.Equal(file, conflict.TargetPath);
        Assert.Equal("arquivo novo do usuário", File.ReadAllText(file));
        Assert.Single(store.List());

        var alternative = dir.Combine("doc (restaurado).txt");
        Assert.Equal(alternative, store.Restore(item.Id, alternative));
        Assert.Equal(Marker, File.ReadAllText(alternative));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Restaura_mesmo_se_a_pasta_original_sumiu()
    {
        using var dir = new TempDir();
        var file = dir.WriteFile(Path.Combine("sumiu", "b.txt"), Marker);
        var store = new QuarantineStore(dir.Combine("q"));
        var item = store.Add(file, "x", "ClamAV", null, Sha(file)).Item;
        Directory.Delete(dir.Combine("sumiu"));

        store.Restore(item.Id);

        Assert.Equal(Marker, File.ReadAllText(file));
    }

    [Fact]
    public void Arquivo_em_uso_nao_e_movido()
    {
        using var dir = new TempDir();
        var file = dir.WriteFile("aberto.txt", Marker);
        var store = new QuarantineStore(dir.Combine("q"));

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<QuarantineException>(() => store.Add(file, "x", "ClamAV", null, null));
        }

        Assert.Equal(Marker, File.ReadAllText(file));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Arquivo_somente_leitura_e_movido_e_restaurado()
    {
        using var dir = new TempDir();
        var file = dir.WriteFile("ro.txt", Marker);
        File.SetAttributes(file, FileAttributes.ReadOnly);
        var store = new QuarantineStore(dir.Combine("q"));

        var item = store.Add(file, "x", "ClamAV", null, Sha(file)).Item;
        Assert.False(File.Exists(file));

        store.Restore(item.Id);
        Assert.Equal(Marker, File.ReadAllText(file));
    }

    [Fact]
    public void Exclusao_definitiva_remove_o_item()
    {
        using var dir = new TempDir();
        var file = dir.WriteFile("c.txt", Marker);
        var store = new QuarantineStore(dir.Combine("q"));
        var item = store.Add(file, "x", "ClamAV", null, null).Item;

        store.Delete(item.Id);

        Assert.Empty(store.List());
        Assert.Empty(Directory.EnumerateFiles(store.FolderPath));
        Assert.Throws<QuarantineException>(() => store.Restore(item.Id));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Copia_corrompida_nao_e_restaurada()
    {
        using var dir = new TempDir();
        var file = dir.WriteFile("d.txt", Marker + new string('y', 10_000));
        var store = new QuarantineStore(dir.Combine("q"));
        var item = store.Add(file, "x", "ClamAV", null, null).Item;
        var blob = Directory.EnumerateFiles(store.FolderPath, "*.cqz").Single();
        var bytes = File.ReadAllBytes(blob);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(blob, bytes);

        Assert.Throws<QuarantineException>(() => store.Restore(item.Id));

        Assert.False(File.Exists(file));
        Assert.DoesNotContain(Directory.EnumerateFiles(dir.Path), f => f.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.Single(store.List());
    }

    [Fact]
    public void Identificador_invalido_e_recusado()
    {
        using var dir = new TempDir();
        var store = new QuarantineStore(dir.Combine("q"));

        Assert.Throws<QuarantineException>(() => store.Restore("..\\..\\x"));
        Assert.Throws<QuarantineException>(() => store.Delete("abc"));
    }
}
