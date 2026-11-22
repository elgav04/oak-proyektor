using OakProyektor.Core.Database;
using OakProyektor.Core.Display;
using OakProyektor.Core.Output;

namespace OakProyektor.Ipc.Messages;

/// <summary>Payload vacío: se serializa como <c>{}</c>.</summary>
public sealed record EmptyPayload;

/// <summary>Error de protocolo. <c>Code</c> es estable (para programas); <c>Message</c> es texto para el operador.</summary>
public sealed record IpcError(string Code, string Message);

/// <summary>
/// Payload de TODO reply: <c>{ ok: true, result, error: null }</c> o <c>{ ok: false, result: null, error }</c>.
/// El cliente rechaza la promesa de la query con el error real en vez de esperar al timeout.
/// </summary>
public sealed record IpcReply(bool Ok, object? Result, IpcError? Error);

/// <summary>Evento <c>command.failed</c>: un comando (que no tiene reply) falló. Se envía a la ventana que lo mandó.</summary>
/// <param name="CommandId">El <c>id</c> del comando que falló.</param>
public sealed record CommandFailedPayload(string CommandId, string CommandType, IpcError Error);

/// <summary><c>output.open</c>. <c>DisplayId</c> nulo = automático (primer monitor que no aloja el panel).</summary>
public sealed record OutputOpenPayload(string? DisplayId);

public sealed record OutputSetMasterPayload(MasterMode Master);

/// <summary>
/// <c>output.recover</c>: pausa pedida por el operador. <c>Reason</c> distingue «Recuperar control» del panel (<c>manual</c>) del atajo de
/// emergencia del respaldo de la página de salida (<c>emergency</c>). Ausente, nulo o cualquier otro valor = <c>manual</c>.
/// </summary>
public sealed record OutputRecoverPayload(OutputPauseReason? Reason);

/// <summary>
/// <c>output.setBackground</c>. <c>Kind</c> none/color no usan <c>Path</c>; image/video exigen una ruta local que el backend valida (ver <c>MediaPathValidator</c>);
/// su extensión debe corresponder al tipo. <c>Color</c> es el color del fondo (color) o el de reserva si el medio falla (null = el actual o #000000).
/// </summary>
public sealed record OutputSetBackgroundPayload(BackgroundKind Kind, string? Path, string? Color, bool Muted, int TransitionMs);

/// <summary><c>output.showContent</c>: un slide (los textos de sus bloques) con su tema y la duración del crossfade.</summary>
public sealed record OutputShowContentPayload(IReadOnlyList<string> Blocks, TextTheme Theme, int TransitionMs);

/// <summary><c>output.clearContent</c>: quita el slide (queda solo el fondo). Distinto del control maestro Clear, que solo lo oculta.</summary>
public sealed record OutputClearContentPayload(int TransitionMs);

/// <summary>
/// <c>output.reportBackgroundFailure</c>: la SALIDA informa de que no pudo reproducir su fondo (ya cayó al color de reserva). <c>MediaUrl</c> identifica
/// de qué fondo habla: si ya no es el actual, el backend lo ignora. <c>Detail</c> es técnico (solo para el log).
/// </summary>
public sealed record OutputReportBackgroundFailurePayload(string MediaUrl, BackgroundFailureReason Reason, string? Detail);

/// <summary>Evento <c>output.backgroundFailed</c> (al panel): el fondo actual no se pudo reproducir; <c>Message</c> explica el motivo.</summary>
public sealed record OutputBackgroundFailedPayload(BackgroundFailureReason Reason, string Message, string? FileName);

/// <summary>Reanudar/restaurar automáticamente al reaparecer el monitor (<c>settings.setAutoRestore</c>).</summary>
public sealed record SetAutoRestorePayload(bool Enabled);

/// <summary>Reply de <c>display.list</c> y evento <c>display.changed</c>.</summary>
/// <param name="Seq">Secuencia global creciente del estado de monitores: las réplicas descartan snapshots con <c>seq</c> menor o igual al último aplicado.</param>
public sealed record DisplayListPayload(long Seq, IReadOnlyList<DisplayInfo> Displays);

/// <param name="DisplayId">ID del monitor asignado a la salida (aunque ya no esté conectado); null si no hay salida.</param>
/// <param name="IsWindowed">true = ventana normal (solo hay un monitor o el elegido aloja el panel); false = sin bordes a pantalla completa.</param>
/// <param name="IsPaused">La salida está minimizada y sin topmost, sin cerrarse ni perder su estado.</param>
/// <param name="PauseReason">Motivo de la pausa; null si no está en pausa.</param>
/// <param name="AutoRestore">Ajuste: reanudar sola al reaparecer el monitor.</param>
/// <param name="EmergencyHotkey">Atajo de emergencia global registrado (p. ej. "Ctrl+Alt+Shift+M"); null si no se pudo registrar.</param>
/// <param name="PreferredDisplayId">Monitor preferido del operador (al que la salida siempre intenta volver); null si no eligió ninguno.</param>
/// <param name="OnDisplayLost">Ajuste: qué hacer si el monitor de la salida desaparece (<c>warnAndWait</c> | <c>moveToBest</c>).</param>
/// <param name="PanelUnresponsive">El JavaScript del panel lleva ~10 s sin dar señales (atascado); la salida sigue proyectando. Informativo.</param>
/// <param name="PanelAlertOnOutput">Ajuste: la salida muestra un aviso discreto cuando <c>PanelUnresponsive</c> (por defecto false).</param>
/// <param name="Hotkeys">Atajos globales de respaldo (Negro, Limpiar texto, Logo, recargar el panel) y si se registraron.</param>
public sealed record OutputWindowStatus(
    bool IsOpen, string? DisplayId, bool IsWindowed, bool IsPaused, OutputPauseReason? PauseReason, bool AutoRestore, string? EmergencyHotkey,
    string? PreferredDisplayId, DisplayLostPolicy OnDisplayLost,
    bool PanelUnresponsive, bool PanelAlertOnOutput, IReadOnlyList<HotkeyStatus> Hotkeys);

/// <summary>Un atajo global de respaldo (independiente del panel): su acción, la combinación y si Windows permitió registrarla (otra aplicación puede haberla tomado).</summary>
/// <param name="Action"><c>black</c> | <c>clearText</c> | <c>logo</c> | <c>reloadPanel</c>.</param>
public sealed record HotkeyStatus(string Action, string Keys, bool Registered);

/// <summary><c>settings.setPanelAlertOnOutput</c>: mostrar «PANEL SIN RESPUESTA» en la propia salida (por defecto NO: la proyección no muestra nada).</summary>
public sealed record SetPanelAlertOnOutputPayload(bool Enabled);

/// <summary><c>settings.setDisplayLostPolicy</c>: «Si el monitor de la salida desaparece».</summary>
public sealed record SetDisplayLostPolicyPayload(DisplayLostPolicy Policy);

/// <summary>
/// Reply de <c>output.getState</c> y evento <c>output.stateChanged</c>: snapshot completo.
/// <c>Seq</c> cambia con CUALQUIER cambio del estado (incluido el de la ventana de salida); las réplicas descartan <c>seq</c> menor o igual.
/// </summary>
public sealed record OutputStateChangedPayload(long Seq, OutputState State, OutputWindowStatus Window);

/// <summary>
/// Reply de <c>database.getStatus</c> y evento <c>database.statusChanged</c>. Si <c>State</c> es <c>failed</c>, <c>Message</c> explica
/// qué pasó y <c>BackupPath</c> (si lo hay) es la copia previa a la migración, con los datos originales.
/// </summary>
public sealed record DatabaseStatusPayload(long Seq, DatabaseState State, string? Message, string? BackupPath);

/// <summary>Reply de <c>settings.getStatus</c> (v10): avisos de la carga de <c>settings.json</c> (bloques inválidos que volvieron a su valor por defecto, archivo dañado); vacío si todo estaba bien.</summary>
public sealed record SettingsStatusPayload(IReadOnlyList<string> Warnings);

