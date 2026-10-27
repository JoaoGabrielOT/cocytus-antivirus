namespace Cocytus.Core;

/// <summary>
/// Motor de detecção. Recebe o conteúdo já aberto pelo orquestrador; nunca executa o arquivo.
/// </summary>
public interface IScanEngine : IAsyncDisposable
{
    string Name { get; }

    /// <summary>Garante que o motor está carregado e devolve sua identificação. Lança <see cref="EngineUnavailableException"/>.</summary>
    Task<EngineInfo> EnsureReadyAsync(CancellationToken cancellationToken);

    /// <summary>Analisa o conteúdo. Lança <see cref="EngineException"/> em falha do motor.</summary>
    Task<EngineVerdict> ScanAsync(Stream content, CancellationToken cancellationToken);
}

/// <summary>O motor não pode ser usado (não instalado, sem bases, falhou ao iniciar).</summary>
public class EngineUnavailableException : Exception
{
    public EngineUnavailableException()
    {
    }

    public EngineUnavailableException(string message)
        : base(message)
    {
    }

    public EngineUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Falha do motor ao analisar um conteúdo específico.</summary>
public class EngineException : Exception
{
    public EngineException()
    {
    }

    public EngineException(string message)
        : base(message)
    {
    }

    public EngineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
