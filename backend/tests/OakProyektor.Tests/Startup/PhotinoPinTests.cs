using System.Xml.Linq;

namespace OakProyektor.Tests.Startup;

/// <summary>
/// I7: Photino.NET y Photino.Native están fijados a versión EXACTA. Este test falla si alguien los cambia: es el recordatorio de que
/// el spike y las pruebas manuales de CLAUDE.md (apertura de la salida con WaitForClose y aislamiento panel/salida) valen solo para estas versiones.
/// Si cambias las versiones: 1) repite la prueba manual obligatoria de CLAUDE.md, 2) actualiza las constantes de aquí, 3) actualiza la fecha de
/// la verificación en CLAUDE.md y en el comentario de ApplicationHost.cs.
/// </summary>
public class PhotinoPinTests
{
    private const string VerifiedPhotinoNet = "4.0.16";
    private const string VerifiedPhotinoNative = "4.0.22";

    private static string FindUp(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"No se encontró {relative} subiendo desde {AppContext.BaseDirectory}");
    }

    private static string? PinnedVersion(string package) =>
        XDocument.Load(FindUp(Path.Combine("backend", "src", "OakProyektor.App", "OakProyektor.App.csproj")))
            .Descendants("PackageReference").FirstOrDefault(e => (string?)e.Attribute("Include") == package)?.Attribute("Version")?.Value;

    [Theory]
    [InlineData("Photino.NET", VerifiedPhotinoNet)]
    [InlineData("Photino.Native", VerifiedPhotinoNative)]
    public void The_package_reference_is_an_exact_version_not_a_minimum(string package, string version)
    {
        // "[4.0.16]" = exactamente esa; "4.0.16" significaría "esa o superior".
        Assert.Equal($"[{version}]", PinnedVersion(package));
    }

    [Fact]
    public void The_Photino_assembly_actually_loaded_is_the_verified_one()
    {
        var loaded = typeof(Photino.NET.PhotinoWindow).Assembly.GetName().Version!.ToString(3);
        Assert.Equal(VerifiedPhotinoNet, loaded);
    }

    [Fact]
    public void The_native_package_that_was_actually_restored_is_the_verified_one()
    {
        var assets = FindUp(Path.Combine("backend", "tests", "OakProyektor.Tests", "obj", "project.assets.json"));
        var text = File.ReadAllText(assets);
        Assert.Contains($"\"Photino.Native/{VerifiedPhotinoNative}\"", text);
        Assert.Contains($"\"Photino.NET/{VerifiedPhotinoNet}\"", text);
    }
}