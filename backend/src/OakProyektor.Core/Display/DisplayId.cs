namespace OakProyektor.Core.Display;

/// <summary>
/// Identificador estable de un monitor. Photino solo expone el rectángulo (no un ID de dispositivo), así que el ID se deriva de él:
/// sobrevive a desconexiones, reconexiones y cambios en el ORDEN de enumeración (lo que rompía a los índices) y cambia solo si el
/// monitor cambia de posición o resolución, que para la política de seguridad ya es «otro monitor».
/// Es el único punto donde se define el formato: si algún día se consigue un ID de dispositivo real, se cambia aquí.
/// </summary>
public static class DisplayId
{
    public static string From(int x, int y, int width, int height) => $"{x},{y},{width}x{height}";
}
