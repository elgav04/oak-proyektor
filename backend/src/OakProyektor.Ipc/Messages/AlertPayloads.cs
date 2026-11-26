using OakProyektor.Core.Output;

namespace OakProyektor.Ipc.Messages;

// Mensajes de las alertas rápidas (protocolo v15). El TEXTO de la alerta viaja solo en lo que lo necesita (mostrarla, listar las guardadas, guardarla y el snapshot de la salida,
// que es lo que se proyecta); los eventos, errores y registros llevan SOLO ids y conteos. `alert.show` y `alert.hide` van por el carril de prioridad: solo tocan memoria.

/// <summary>
/// <c>alert.show</c>: muestra una alerta y REEMPLAZA a la que hubiera. <c>DurationSec</c>: 0 = «hasta quitarla» (con un tope de 30 min), si no de 5 a 1800 s
/// (el panel ofrece 15, 30, 60, 120 y 300). El tema sale de los ajustes del backend en el momento de mostrarla.
/// </summary>
public sealed record AlertShowPayload(string Text, AlertMode Mode, int DurationSec);

/// <summary>Una alerta guardada: <c>LastUsedAt</c> (ISO-8601 UTC) nulo = nunca se mostró.</summary>
public sealed record SavedAlertDto(long Id, string Text, bool Pinned, int UseCount, string? LastUsedAt);

/// <summary>Reply de <c>alert.list</c>: las fijadas (orden alfabético) y las recientes (como mucho 10 de los últimos 14 días, la más nueva primero).</summary>
public sealed record AlertListPayload(IReadOnlyList<SavedAlertDto> Pinned, IReadOnlyList<SavedAlertDto> Recent);

/// <summary><c>alert.save</c>: crea (sin <c>Id</c>) o edita una alerta guardada y la fija o desfija. Más de 30 fijadas = <c>alert.full</c>.</summary>
public sealed record AlertSavePayload(long? Id, string Text, bool Pinned);

public sealed record AlertDeletePayload(long Id);

/// <summary>Resultado de <c>alert.save</c> / <c>alert.delete</c>, correlacionado por el id del comando. <c>Kind</c>: <c>saved</c> | <c>deleted</c>. Solo ids.</summary>
public sealed record AlertChangedPayload(string CommandId, long AlertId, string Kind);

/// <summary>Reply de <c>alert.getSettings</c> y cuerpo de <c>alert.setSettings</c> (que sustituye el conjunto y lo valida ANTES de guardar).</summary>
public sealed record AlertSettingsPayload(AlertMode Mode, int DurationSec, AlertTheme Theme, bool TickerEnabled);
