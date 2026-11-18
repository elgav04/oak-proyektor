namespace OakProyektor.Ipc;

/// <summary>Atiende un (kind, type) entrante. Los comandos devuelven null; las queries devuelven el payload del reply.</summary>
public interface IIpcHandler
{
    string Kind { get; }
    string Type { get; }
    Task<object?> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken);
}
