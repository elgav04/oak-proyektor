namespace OakProyektor.Ipc;

/// <summary>
/// Fallo esperado de un handler: el dispatcher lo convierte en un error de protocolo con este <see cref="Code"/> y este mensaje
/// (reply con <c>ok: false</c> para una query, evento <c>command.failed</c> para un comando). Cualquier otra excepción se
/// reporta como <c>internal</c> sin exponer sus detalles.
/// </summary>
public sealed class IpcException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
