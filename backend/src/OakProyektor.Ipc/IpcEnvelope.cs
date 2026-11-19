using System.Text.Json;
using System.Text.Json.Serialization;

namespace OakProyektor.Ipc;

/// <summary>Sobre común: <c>{ v, id, kind, type, payload }</c>.</summary>
public sealed record IpcEnvelope(int V, string Id, string Kind, string Type, JsonElement Payload)
{
    /// <summary>
    /// Número de llegada, asignado por el enrutador de entrada (<c>OrderClock</c>) al recibir el mensaje; NO viaja en el JSON. Permite ordenar
    /// entre los dos carriles de entrada las órdenes del operador (p. ej. una pausa de emergencia posterior a un <c>output.open</c> aún en
    /// curso). 0 = sin numerar.
    /// </summary>
    [JsonIgnore]
    public long Order { get; init; }

    public static IpcEnvelope Create(string kind, string type, object payload, string? id = null) =>
        new(IpcCatalog.ProtocolVersion, id ?? Guid.NewGuid().ToString("D"), kind, type,
            JsonSerializer.SerializeToElement(payload, payload.GetType(), IpcSerializer.Options));

    public T GetPayload<T>() where T : class =>
        Payload.Deserialize<T>(IpcSerializer.Options)
        ?? throw new JsonException($"El payload de {Type} es nulo.");
}
