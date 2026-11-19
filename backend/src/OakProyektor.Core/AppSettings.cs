namespace OakProyektor.Core;

/// <summary>Configuración persistente (data/settings.json).</summary>
/// <param name="AutoRestoreOutput">Restaurar la salida sola cuando reaparece su monitor.</param>
public sealed record AppSettings(bool AutoRestoreOutput = true);
