using System.Globalization;
using System.Reflection;

namespace OakProyektor.Data.Migrations;

/// <summary>Una migración numerada: el script SQL embebido <c>NNNN_Nombre.sql</c>.</summary>
public sealed record Migration(int Version, string Name, string Sql)
{
    private const string ResourceMarker = ".Migrations.";

    /// <summary>Carga y valida (versiones consecutivas desde 1) las migraciones embebidas del ensamblado.</summary>
    public static IReadOnlyList<Migration> LoadEmbedded(Assembly assembly)
    {
        var list = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var at = resource.IndexOf(ResourceMarker, StringComparison.Ordinal);
            if (at < 0 || !resource.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) continue;

            var file = resource[(at + ResourceMarker.Length)..^4];          // "0001_Initial"
            var underscore = file.IndexOf('_');
            var number = underscore < 0 ? file : file[..underscore];
            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
                throw new InvalidOperationException($"Nombre de migración inválido: {resource}");

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"No se pudo leer la migración {resource}");
            using var reader = new StreamReader(stream);
            list.Add(new Migration(version, underscore < 0 ? file : file[(underscore + 1)..], reader.ReadToEnd()));
        }

        list.Sort(static (a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Version != i + 1)
                throw new InvalidOperationException($"Las migraciones deben ser consecutivas desde 1; falta la {i + 1}.");
        }
        return list;
    }
}
