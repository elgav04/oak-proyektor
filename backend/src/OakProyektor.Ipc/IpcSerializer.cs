using System.Text.Json;
using System.Text.Json.Serialization;

namespace OakProyektor.Ipc;

public static class IpcSerializer
{
    /// <summary>Tamaño máximo aceptado por mensaje entrante.</summary>
    public const int MaxMessageChars = 1_000_000;

    /// <summary>Opciones compartidas. Son de SOLO LECTURA desde el primer momento (hallazgo M3): ningún código puede alterarlas después.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
            NumberHandling = JsonNumberHandling.Strict,              // "1" no es un número válido
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,   // los null se envían explícitos (el TS los espera como "| null")
        };
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    public static string Serialize(IpcEnvelope envelope) => JsonSerializer.Serialize(envelope, Options);

    /// <summary>
    /// Interpreta un mensaje entrante. Nunca lanza: devuelve false con el motivo si el mensaje es inválido
    /// (JSON roto, versión no soportada, campos faltantes, demasiado grande).
    /// </summary>
    public static bool TryParse(string? raw, out IpcEnvelope? envelope, out string? error)
    {
        envelope = null;
        if (string.IsNullOrWhiteSpace(raw)) { error = "mensaje vacío"; return false; }
        if (raw.Length > MaxMessageChars) { error = $"mensaje demasiado grande ({raw.Length} caracteres)"; return false; }

        RawEnvelope? parsed;
        try { parsed = JsonSerializer.Deserialize<RawEnvelope>(raw, Options); }
        catch (JsonException ex) { error = $"JSON inválido: {ex.Message}"; return false; }

        if (parsed is null) { error = "el mensaje no es un objeto"; return false; }
        if (parsed.V != IpcCatalog.ProtocolVersion) { error = $"versión de protocolo no soportada: {parsed.V}"; return false; }
        if (string.IsNullOrEmpty(parsed.Id) || string.IsNullOrEmpty(parsed.Kind) || string.IsNullOrEmpty(parsed.Type))
        { error = "faltan id, kind o type"; return false; }

        var payload = parsed.Payload is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } p
            ? p
            : JsonSerializer.SerializeToElement(new { }, Options);   // sin payload = {}
        envelope = new IpcEnvelope(parsed.V, parsed.Id, parsed.Kind, parsed.Type, payload);
        error = null;
        return true;
    }

    private sealed record RawEnvelope(int V, string? Id, string? Kind, string? Type, JsonElement? Payload);
}

