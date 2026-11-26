/** Combinación de emergencia dentro de la ventana de salida: respaldo si el atajo global no se pudo registrar. */
export function isEmergencyKey(e: KeyboardEvent): boolean {
  return e.ctrlKey && e.altKey && e.shiftKey && e.code === "KeyM";
}

/** Quitar la alerta (respaldo del atajo global Ctrl+Alt+Shift+A). Solo QUITA: nunca la muestra. */
export function isHideAlertKey(e: KeyboardEvent): boolean {
  return e.ctrlKey && e.altKey && e.shiftKey && e.code === "KeyA";
}
