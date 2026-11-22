using OakProyektor.Core.Output;

namespace OakProyektor.Tests.Core;

/// <summary>Sistema de archivos falso: no toca el disco. Por defecto cualquier archivo «existe», tiene contenido y está en una unidad fija.</summary>
internal sealed class FakeMediaFileSystem : IMediaFileSystem
{
    public Dictionary<string, DriveType> Drives { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FileProbe> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Probed { get; } = [];
    public DriveType DriveTypeOf(string root) => Drives.TryGetValue(root, out var d) ? d : DriveType.Fixed;
    public FileProbe Probe(string path) { Probed.Add(path); return Files.TryGetValue(path, out var f) ? f : new FileProbe(true, 1024, null); }
}

public class MediaPathValidatorTests
{
    private static MediaValidation Validate(string? path, FakeMediaFileSystem? fs = null, MediaKind? kind = null) =>
        MediaPathValidator.Validate(path, fs ?? new FakeMediaFileSystem(), kind);

    [Theory]
    [InlineData(@"C:\Videos\fondo.mp4", MediaKind.Video)]
    [InlineData(@"C:\Videos\fondo.M4V", MediaKind.Video)]
    [InlineData(@"C:\Videos\fondo.webm", MediaKind.Video)]
    [InlineData(@"D:\Fotos\a.jpg", MediaKind.Image)]
    [InlineData(@"D:\Fotos\a.JPEG", MediaKind.Image)]
    [InlineData(@"D:\Fotos\a.png", MediaKind.Image)]
    [InlineData(@"D:\Fotos\a.webp", MediaKind.Image)]
    public void Allowed_extensions_are_accepted_with_their_kind(string path, MediaKind kind)
    {
        var r = Validate(path);
        Assert.True(r.IsValid, r.Message);
        Assert.Equal(kind, r.Media!.Kind);
    }

    [Theory]
    [InlineData(@"C:\Videos\fondo.mkv")]
    [InlineData(@"C:\Videos\fondo.avi")]
    [InlineData(@"C:\Videos\fondo.mov")]
    [InlineData(@"C:\Videos\fondo.gif")]
    [InlineData(@"C:\Videos\fondo")]
    [InlineData(@"C:\Videos\fondo.mp4.exe")]
    public void Other_extensions_are_rejected(string path)
    {
        var r = Validate(path);
        Assert.Equal(MediaRejectionReason.UnsupportedExtension, r.Reason);
        Assert.Contains("no soportado", r.Message);
    }

    [Theory]
    [InlineData(@"\\servidor\recurso\fondo.mp4")]
    [InlineData("//servidor/recurso/fondo.mp4")]
    [InlineData(@"\\?\UNC\servidor\recurso\fondo.mp4")]
    [InlineData(@"\\?\C:\Videos\fondo.mp4")]
    [InlineData(@"\\.\C:\Videos\fondo.mp4")]
    public void Network_and_device_paths_are_rejected_without_touching_the_disk(string path)
    {
        var fs = new FakeMediaFileSystem();
        var r = Validate(path, fs);
        Assert.Equal(MediaRejectionReason.NetworkPath, r.Reason);
        Assert.Empty(fs.Probed);
    }

    [Fact]
    public void A_mapped_network_drive_is_rejected_before_probing_the_file()
    {
        var fs = new FakeMediaFileSystem();
        fs.Drives[@"Z:\"] = DriveType.Network;
        var r = Validate(@"Z:\fondo.mp4", fs);
        Assert.Equal(MediaRejectionReason.NetworkDrive, r.Reason);
        Assert.Empty(fs.Probed);   // una unidad de red caída puede bloquear Exists durante segundos
    }

    [Theory]
    [InlineData(@"fondo.mp4")]
    [InlineData(@"Videos\fondo.mp4")]
    [InlineData(@"\Videos\fondo.mp4")]
    [InlineData(@"C:fondo.mp4")]
    [InlineData(@"..\fondo.mp4")]
    public void Relative_paths_are_rejected(string path) => Assert.Equal(MediaRejectionReason.NotAbsolute, Validate(path).Reason);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_path_is_rejected(string? path) => Assert.Equal(MediaRejectionReason.Empty, Validate(path).Reason);

    [Theory]
    [InlineData("C:\\Videos\\fon*do.mp4")]
    [InlineData("C:\\Videos\\fon?do.mp4")]
    [InlineData("C:\\Videos\\fon\0do.mp4")]
    [InlineData(@"C:\Videos\fondo.mp4:secreto")]
    public void Invalid_characters_and_alternate_streams_are_rejected(string path) => Assert.Equal(MediaRejectionReason.InvalidPath, Validate(path).Reason);

    [Fact]
    public void The_path_is_canonicalized_so_dot_segments_cannot_hide_anything()
    {
        var r = Validate(@"C:\Videos\..\Otros\.\fondo.mp4");
        Assert.True(r.IsValid);
        Assert.Equal(@"C:\Otros\fondo.mp4", r.Media!.Path);
        Assert.Equal("file:///C:/Otros/fondo.mp4", r.Media.Url);
    }

    [Fact]
    public void The_url_escapes_spaces_accents_and_special_characters()
    {
        var r = Validate(@"C:\Mis videos\Alabanza #1 — ñandú%20.mp4");
        Assert.True(r.IsValid, r.Message);
        Assert.StartsWith("file:///C:/Mis%20videos/", r.Media!.Url);
        Assert.Contains("%23", r.Media.Url);       // '#' no puede quedar sin escapar: empezaría el fragmento
        Assert.DoesNotContain("#", r.Media.Url);
        Assert.Equal(r.Media.Path, new Uri(r.Media.Url).LocalPath);   // ida y vuelta
    }

    [Fact]
    public void A_missing_or_empty_file_is_rejected()
    {
        var fs = new FakeMediaFileSystem();
        fs.Files[@"C:\a.mp4"] = new FileProbe(false, 0, null);
        fs.Files[@"C:\b.mp4"] = new FileProbe(true, 0, null);
        Assert.Equal(MediaRejectionReason.NotFound, Validate(@"C:\a.mp4", fs).Reason);
        Assert.Equal(MediaRejectionReason.EmptyFile, Validate(@"C:\b.mp4", fs).Reason);
    }

    [Theory]
    [InlineData(DriveType.Removable, true)]
    [InlineData(DriveType.CDRom, true)]
    [InlineData(DriveType.Fixed, false)]
    [InlineData(DriveType.Unknown, false)]
    public void Removable_drives_are_accepted_but_flagged(DriveType drive, bool removable)
    {
        var fs = new FakeMediaFileSystem();
        fs.Drives[@"E:\"] = drive;
        var r = Validate(@"E:\fondo.mp4", fs);
        Assert.True(r.IsValid);
        Assert.Equal(removable, r.Media!.Removable);
    }

    [Fact]
    public void The_requested_kind_must_match_the_extension()
    {
        Assert.Equal(MediaRejectionReason.KindMismatch, Validate(@"C:\a.png", kind: MediaKind.Video).Reason);
        Assert.Equal(MediaRejectionReason.KindMismatch, Validate(@"C:\a.mp4", kind: MediaKind.Image).Reason);
        Assert.True(Validate(@"C:\a.mp4", kind: MediaKind.Video).IsValid);
    }

    [Fact]
    public void A_symbolic_link_is_validated_by_its_real_target()
    {
        var fs = new FakeMediaFileSystem();
        fs.Files[@"C:\enlace.mp4"] = new FileProbe(true, 10, @"\\servidor\x\real.mp4");
        fs.Files[@"C:\otro.mp4"] = new FileProbe(true, 10, @"E:\real.mp4");
        fs.Drives[@"E:\"] = DriveType.Removable;

        Assert.Equal(MediaRejectionReason.NetworkPath, Validate(@"C:\enlace.mp4", fs).Reason);   // el destino es de red
        var ok = Validate(@"C:\otro.mp4", fs);
        Assert.True(ok.IsValid);
        Assert.True(ok.Media!.Removable);   // el destino está en una unidad extraíble
        Assert.Equal("otro.mp4", ok.Media.FileName);
    }

    [Fact]
    public void A_folder_junction_or_link_in_the_middle_of_the_path_to_a_network_share_is_rejected()
    {
        // `C:\Fondos` es un enlace de carpeta a un recurso de red: el archivo «es local» por su ruta, pero la ruta FINAL del sistema es de red (H9).
        var fs = new FakeMediaFileSystem();
        fs.Files[@"C:\Fondos\a.mp4"] = new FileProbe(true, 10, @"\\servidor\recurso\a.mp4");
        var r = Validate(@"C:\Fondos\a.mp4", fs);
        Assert.False(r.IsValid);
        Assert.Equal(MediaRejectionReason.NetworkPath, r.Reason);
    }

    [Fact]
    public void A_folder_junction_to_a_mapped_network_drive_is_rejected()
    {
        var fs = new FakeMediaFileSystem();
        fs.Drives[@"Z:\"] = DriveType.Network;
        fs.Files[@"C:\Fondos\a.mp4"] = new FileProbe(true, 10, @"Z:\carpeta\a.mp4");
        Assert.Equal(MediaRejectionReason.NetworkDrive, Validate(@"C:\Fondos\a.mp4", fs).Reason);
    }

    [Fact]
    public void A_subst_drive_is_judged_by_the_real_drive_behind_it()
    {
        var fs = new FakeMediaFileSystem();
        // S: es un `subst` de una carpeta de E: (USB): el archivo se acepta y se avisa de que está en una unidad extraíble.
        fs.Drives[@"S:\"] = DriveType.Fixed;
        fs.Drives[@"E:\"] = DriveType.Removable;
        fs.Files[@"S:\a.mp4"] = new FileProbe(true, 10, @"E:\Fondos\a.mp4");
        var ok = Validate(@"S:\a.mp4", fs);
        Assert.True(ok.IsValid, ok.Message);
        Assert.True(ok.Media!.Removable);
        Assert.Equal("file:///E:/Fondos/a.mp4", ok.Media.Url);   // la salida recibe el destino REAL
        Assert.Equal("a.mp4", ok.Media.FileName);

        // Y un `subst` (o una unión) que acaba en una unidad de red también se rechaza.
        fs.Drives[@"Z:\"] = DriveType.Network;
        fs.Files[@"S:\b.mp4"] = new FileProbe(true, 10, @"Z:\b.mp4");
        Assert.Equal(MediaRejectionReason.NetworkDrive, Validate(@"S:\b.mp4", fs).Reason);
    }

    [Fact]
    public void A_link_that_ends_on_another_unsupported_file_is_rejected_by_its_real_extension()
    {
        var fs = new FakeMediaFileSystem();
        fs.Files[@"C:\enlace.mp4"] = new FileProbe(true, 10, @"C:\real.exe");
        Assert.Equal(MediaRejectionReason.UnsupportedExtension, Validate(@"C:\enlace.mp4", fs).Reason);
    }

    [Theory]
    [InlineData(@"\\?\C:\Videos\a.mp4", @"C:\Videos\a.mp4")]
    [InlineData(@"\\?\UNC\servidor\recurso\a.mp4", @"\\servidor\recurso\a.mp4")]
    [InlineData(@"\\?\unc\servidor\recurso\a.mp4", @"\\servidor\recurso\a.mp4")]
    [InlineData(@"C:\ya\normal.mp4", @"C:\ya\normal.mp4")]
    public void The_final_path_prefix_is_normalized(string raw, string expected) => Assert.Equal(expected, FinalPathResolver.Normalize(raw));

    [Fact]
    public void The_real_file_system_sees_through_a_real_folder_junction()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "oak-junction-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "destino");
        var junction = Path.Combine(root, "atajo");
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllBytes(Path.Combine(target, "fondo.mp4"), [1, 2, 3]);
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using (var p = System.Diagnostics.Process.Start(psi)!) { p.WaitForExit(10000); Assert.Equal(0, p.ExitCode); }

            var viaJunction = MediaPathValidator.Validate(Path.Combine(junction, "fondo.mp4"), new PhysicalMediaFileSystem(), MediaKind.Video);
            Assert.True(viaJunction.IsValid, viaJunction.Message);
            // La ruta que se valida y se envía a la salida es la FINAL (la de la carpeta de destino), no la del atajo.
            Assert.Equal(Path.Combine(target, "fondo.mp4"), viaJunction.Media!.Path, ignoreCase: true);
            Assert.DoesNotContain("atajo", viaJunction.Media.Url);
        }
        finally
        {
            try { if (Directory.Exists(junction)) Directory.Delete(junction); } catch (IOException) { }
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void The_real_file_system_finds_a_real_file_and_a_missing_one()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oak-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "prueba.webm");
            File.WriteAllBytes(file, [1, 2, 3]);
            var fs = new PhysicalMediaFileSystem();

            var ok = MediaPathValidator.Validate(file, fs, MediaKind.Video);
            Assert.True(ok.IsValid, ok.Message);
            Assert.Equal(new Uri(file).AbsoluteUri, ok.Media!.Url);

            Assert.Equal(MediaRejectionReason.NotFound, MediaPathValidator.Validate(Path.Combine(dir, "falta.webm"), fs).Reason);
            File.WriteAllBytes(Path.Combine(dir, "vacio.mp4"), []);
            Assert.Equal(MediaRejectionReason.EmptyFile, MediaPathValidator.Validate(Path.Combine(dir, "vacio.mp4"), fs).Reason);
            // una carpeta con nombre de video no es un archivo
            Directory.CreateDirectory(Path.Combine(dir, "carpeta.mp4"));
            Assert.Equal(MediaRejectionReason.NotFound, MediaPathValidator.Validate(Path.Combine(dir, "carpeta.mp4"), fs).Reason);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

public class ContentValidatorTests
{
    private static string? Slide(IReadOnlyList<string>? blocks, TextTheme? theme = null, int ms = 400) => ContentValidator.ValidateSlide(blocks, theme ?? TextTheme.Default, ms);

    [Fact]
    public void The_default_theme_and_a_normal_slide_are_valid()
    {
        Assert.Null(ContentValidator.ValidateTheme(TextTheme.Default));
        Assert.Null(Slide(["Hola"]));
    }

    [Fact]
    public void A_missing_slide_is_rejected() => Assert.NotNull(Slide(null));

    [Fact]
    public void Empty_or_blank_or_oversized_slides_are_rejected()
    {
        Assert.NotNull(Slide([]));
        Assert.NotNull(Slide(["", "   "]));
        Assert.NotNull(Slide([new string('a', ContentValidator.MaxBlockChars + 1)]));
        Assert.NotNull(Slide(Enumerable.Repeat("x", ContentValidator.MaxBlocks + 1).ToList()));
        Assert.NotNull(Slide(Enumerable.Repeat(new string('a', ContentValidator.MaxBlockChars), 6).ToList()));   // 24 000 > 20 000
        Assert.NotNull(ContentValidator.ValidateSlide(["a"], null, 400));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(5001)]
    public void The_transition_must_be_between_0_and_5000_ms(int ms) => Assert.NotNull(Slide(["Hola"], ms: ms));

    [Theory]
    [InlineData("#FFF")]
    [InlineData("red")]
    [InlineData("#GGGGGG")]
    [InlineData("#12345")]
    [InlineData("url(javascript:alert(1))")]
    [InlineData("#112233; background: red")]
    [InlineData("")]
    public void Colors_must_be_hex(string color)
    {
        Assert.False(ContentValidator.IsColor(color));
        Assert.NotNull(ContentValidator.ValidateTheme(TextTheme.Default with { Color = color }));
    }

    [Theory]
    [InlineData("#112233")]
    [InlineData("#11223344")]
    [InlineData("#abcdef")]
    public void Hex_colors_with_or_without_alpha_are_accepted(string color) => Assert.True(ContentValidator.IsColor(color));

    [Theory]
    [InlineData("Arial; color: red")]
    [InlineData("a{b}")]
    [InlineData("url(x)")]
    [InlineData("")]
    public void The_font_family_cannot_inject_css(string family) => Assert.NotNull(ContentValidator.ValidateTheme(TextTheme.Default with { FontFamily = family }));

    [Fact]
    public void Theme_limits_are_enforced()
    {
        var t = TextTheme.Default;
        Assert.NotNull(ContentValidator.ValidateTheme(t with { FontSize = 7 }));
        Assert.NotNull(ContentValidator.ValidateTheme(t with { FontSize = 401 }));
        Assert.NotNull(ContentValidator.ValidateTheme(t with { MinFontSize = t.FontSize + 1 }));
        Assert.NotNull(ContentValidator.ValidateTheme(t with { OutlineWidth = 41 }));
        Assert.NotNull(ContentValidator.ValidateTheme(t with { LineHeight = 0.5 }));
        Assert.NotNull(ContentValidator.ValidateTheme(t with { LineHeight = double.NaN }));
        Assert.NotNull(ContentValidator.ValidateTheme(t with { MarginLeft = -1 }));
        Assert.NotNull(ContentValidator.ValidateTheme(t with { MarginLeft = 900, MarginRight = 900 }));   // sin espacio para el texto
        Assert.NotNull(ContentValidator.ValidateTheme(t with { Align = (TextAlign)9 }));
        Assert.NotNull(ContentValidator.ValidateTheme(t with { VerticalAlign = (VerticalAlign)9 }));
    }
}

public class BackgroundFailuresTests
{
    [Fact]
    public void An_unsupported_format_message_says_so_and_names_hevc()
    {
        var m = BackgroundFailures.Describe(BackgroundFailureReason.UnsupportedFormat, "culto.mp4");
        Assert.Contains("culto.mp4", m);
        Assert.Contains("HEVC", m);
        Assert.Contains("H.264", m);
        Assert.Contains("color de reserva", m);
    }

    [Fact]
    public void Every_reason_has_its_own_message()
    {
        var messages = Enum.GetValues<BackgroundFailureReason>().Select(r => BackgroundFailures.Describe(r, null)).ToList();
        Assert.Equal(messages.Count, messages.Distinct().Count());
        Assert.All(messages, m => Assert.Contains("el fondo", m));
    }
}
