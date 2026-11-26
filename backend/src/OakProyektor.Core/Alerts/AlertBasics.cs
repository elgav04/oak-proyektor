namespace OakProyektor.Core.Alerts;

/// <summary>Fallo esperado del módulo de alertas. El mensaje NUNCA cita el texto de una alerta.</summary>
public sealed class AlertException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public static class AlertErrorCodes
{
    /// <summary>El texto (o la duración o el modo) de la alerta no es válido.</summary>
    public const string InvalidContent = "alert.invalidContent";
    public const string InvalidSettings = "alert.invalidSettings";
    public const string NotFound = "alert.notFound";
    /// <summary>Ya hay 30 alertas fijadas.</summary>
    public const string Full = "alert.full";
    /// <summary>Ya existe otra alerta con ese texto (sin distinguir mayúsculas ni espacios).</summary>
    public const string Duplicate = "alert.duplicate";
    /// <summary>Otra operación larga retiene el bloqueo de escritura de la base de datos; no se cambió nada.</summary>
    public const string Busy = "alert.busy";
    /// <summary>La salida está en pausa: una alerta no sobrevive a una pausa y no se mostró.</summary>
    public const string OutputPaused = "alert.outputPaused";
}

public static class AlertLimits
{
    /// <summary>Alertas fijadas (explícitas, perennes).</summary>
    public const int MaxPinned = 30;
    /// <summary>Alertas NO fijadas que se conservan (las más antiguas se podan al anotar una nueva).</summary>
    public const int MaxRecentStored = 50;
    /// <summary>Cuántas «recientes» muestra el panel.</summary>
    public const int RecentShown = 10;
    /// <summary>Las «recientes» envejecen: una no usada en este tiempo deja de mostrarse (no se borra hasta que la poda la alcance).</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(14);
}

/// <summary>Una alerta guardada. <c>LastUsedAt</c> nulo = nunca se mostró (solo se guardó).</summary>
public sealed record SavedAlert(long Id, string Text, bool Pinned, int UseCount, DateTimeOffset? LastUsedAt);

/// <summary>Fijadas (orden alfabético estable) y recientes (las últimas usadas, la más nueva primero).</summary>
public sealed record SavedAlertList(IReadOnlyList<SavedAlert> Pinned, IReadOnlyList<SavedAlert> Recent);
