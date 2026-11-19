using OakProyektor.Core.Display;
using OakProyektor.Core.Output;

namespace OakProyektor.Ipc.Messages;

/// <summary>Payload vacío: se serializa como <c>{}</c>.</summary>
public sealed record EmptyPayload;

/// <summary><c>output.open</c>. <c>DisplayIndex</c> nulo = automático (primer monitor que no aloja el panel).</summary>
public sealed record OutputOpenPayload(int? DisplayIndex);

public sealed record OutputSetMasterPayload(MasterMode Master);

/// <summary>Reply de <c>display.list</c> y evento <c>display.changed</c>.</summary>
public sealed record DisplayListPayload(IReadOnlyList<DisplayInfo> Displays);

/// <summary>Reanudar/restaurar automáticamente al reaparecer el monitor (<c>settings.setAutoRestore</c>).</summary>
public sealed record SetAutoRestorePayload(bool Enabled);

/// <param name="DisplayIndex">Índice ACTUAL del monitor de la salida; null si ese monitor ya no está conectado.</param>
/// <param name="IsWindowed">true = ventana normal (solo hay un monitor o el elegido aloja el panel); false = sin bordes a pantalla completa.</param>
/// <param name="IsPaused">La salida está minimizada y sin topmost, sin cerrarse ni perder su estado.</param>
/// <param name="PauseReason">Motivo de la pausa; null si no está en pausa.</param>
/// <param name="AutoRestore">Ajuste: reanudar sola al reaparecer el monitor.</param>
/// <param name="EmergencyHotkey">Atajo de emergencia global registrado (p. ej. "Ctrl+Alt+Shift+M"); null si no se pudo registrar.</param>
public sealed record OutputWindowStatus(
    bool IsOpen, int? DisplayIndex, bool IsWindowed, bool IsPaused, OutputPauseReason? PauseReason, bool AutoRestore, string? EmergencyHotkey);

/// <summary>Reply de <c>output.getState</c> y evento <c>output.stateChanged</c>: snapshot completo.</summary>
public sealed record OutputStateChangedPayload(OutputState State, OutputWindowStatus Window);

