namespace OakProyektor.Core.Display;

/// <summary>Un monitor. Coordenadas en píxeles físicos del escritorio virtual de Windows.</summary>
/// <param name="Index">Posición en la enumeración de Photino (no estable entre cambios de monitores).</param>
/// <param name="IsPrimary">El monitor principal de Windows siempre tiene su origen en (0,0).</param>
/// <param name="HostsPanel">La ventana del panel de control está (su centro) en este monitor.</param>
public sealed record DisplayInfo(int Index, int X, int Y, int Width, int Height, double Scale, bool IsPrimary, bool HostsPanel);
