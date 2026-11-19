/** Combinación de emergencia dentro de la ventana de salida: respaldo si el atajo global no se pudo registrar. */
export function isEmergencyKey(e: KeyboardEvent): boolean {
  return e.ctrlKey && e.altKey && e.shiftKey && e.code === "KeyM";
}
