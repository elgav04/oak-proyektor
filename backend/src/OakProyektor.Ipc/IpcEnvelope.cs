using System.Text.Json;

namespace OakProyektor.Ipc;

/// <summary>Sobre común: <c>{ v, id, kind, type, payload }</c>.</summary>
public sealed record IpcEnvelope(int V, string Id, string Kind, string Type, JsonElement Payload)
{
    public static IpcEnvelope Create(string kind, string type, object payload, string? id = null) =>
        new(IpcCatalog.ProtocolVersion, id ?? Guid.NewGuid().ToString("D"), kind, type,
            JsonSerializer.SerializeToElement(payload, payload.GetType(), IpcSerializer.Options));

    public T GetPayload<T>() where T : class =>
        Payload.Deserialize<T>(IpcSerializer.Options)
        ?? throw new JsonException($"El payload de {Type} es nulo.");
}
