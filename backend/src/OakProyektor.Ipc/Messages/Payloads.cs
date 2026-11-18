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

/// <param name="IsWindowed">true = ventana normal (solo hay un monitor o el elegido aloja el panel); false = sin bordes a pantalla completa.</param>
public sealed record OutputWindowStatus(bool IsOpen, int? DisplayIndex, bool IsWindowed);

/// <summary>Reply de <c>output.getState</c> y evento <c>output.stateChanged</c>: snapshot completo.</summary>
public sealed record OutputStateChangedPayload(OutputState State, OutputWindowStatus Window);
