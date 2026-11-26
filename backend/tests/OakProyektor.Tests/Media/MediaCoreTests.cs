using System.Diagnostics;
using OakProyektor.Core.Media;
using OakProyektor.Core.Output;
using OakProyektor.Tests.Core;

namespace OakProyektor.Tests.Media;

/// <summary>Reglas de nombres, validación de la fuente y sonda de cabeceras. Todo con archivos SINTÉTICOS generados por código.</summary>
public class MediaCoreTests
{
    private static MediaProbeResult Probe(byte[] bytes, string ext) { using var ms = new MemoryStream(bytes); return MediaProbe.Probe(ms, ext); }

    // ───────────── nombres ─────────────

    [Fact]
    public void Names_are_normalized_trimmed_and_never_empty_and_the_name_on_disk_is_always_plain_ascii()
    {
        Assert.Equal("Video de prueba", MediaRules.NormalizeName("  Video \t de\r\n prueba  "));
        Assert.Equal("Sin nombre", MediaRules.NormalizeName("   "));
        Assert.Equal("Sin nombre", MediaRules.NormalizeName(null));
        Assert.Equal(MediaLimits.MaxNameChars, MediaRules.NormalizeName(new string('a', 500)).Length);
        Assert.Equal("Caf\u00E9", MediaRules.NormalizeName("Cafe\u0301"));
        Assert.DoesNotContain('\u0007', MediaRules.NormalizeName("a\u0007b"));

        var name = MediaRules.StoredFileName("AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12CD34EF56AB12", ".MP4");
        Assert.Matches("^[a-f0-9]{64}\\.mp4$", name);
        Assert.Equal("ab", MediaRules.ShardOf("AB12"));
        // El nombre de un archivo del operador con caracteres raros solo llega a la base de datos, nunca al disco.
        Assert.Equal("Anuncio 100% #1 & más", MediaRules.NameFromFile("Anuncio 100% #1 & más.mp4"));
    }

    [Theory]
    [InlineData(".mp4", MediaItemKind.Video)] [InlineData(".M4V", MediaItemKind.Video)] [InlineData(".webm", MediaItemKind.Video)]
    [InlineData(".mp3", MediaItemKind.Audio)] [InlineData(".m4a", MediaItemKind.Audio)] [InlineData(".aac", MediaItemKind.Audio)] [InlineData(".wav", MediaItemKind.Audio)] [InlineData(".flac", MediaItemKind.Audio)]
    [InlineData(".jpg", MediaItemKind.Image)] [InlineData(".jpeg", MediaItemKind.Image)] [InlineData(".png", MediaItemKind.Image)] [InlineData(".webp", MediaItemKind.Image)]
    public void The_closed_list_of_extensions_maps_to_its_kind(string ext, MediaItemKind kind) => Assert.Equal(kind, MediaRules.KindOfExtension(ext));

    [Theory]
    [InlineData(".avi")] [InlineData(".mkv")] [InlineData(".mov")] [InlineData(".wmv")] [InlineData(".ogg")] [InlineData(".opus")] [InlineData(".wma")] [InlineData(".exe")] [InlineData("")]
    public void Anything_outside_the_list_has_no_kind(string ext) => Assert.Null(MediaRules.KindOfExtension(ext));

    // ───────────── fuente de la importación ─────────────

    private static ImportSourceValidation Validate(string? path, FakeMediaFileSystem? fs = null) => ImportSourceValidator.Validate(path, fs ?? new FakeMediaFileSystem());

    [Theory]
    [InlineData(@"C:\Videos\anuncio.mp4")]
    [InlineData(@"""C:\Videos\anuncio.mp4""")]
    [InlineData(@"E:\USB\musica.mp3")]
    [InlineData(@"\\servidor\recurso\fondos\a.webm")]
    public void Local_removable_and_network_sources_are_accepted_because_importing_reads_them_once(string path)
    {
        var fs = new FakeMediaFileSystem();
        fs.Drives[@"E:\"] = DriveType.Removable;
        var v = Validate(path, fs);
        Assert.True(v.IsValid, v.Message);
        Assert.Equal(1024, v.Source!.Length);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")]
    [InlineData(@"relativa\a.mp4")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\C:\a.mp4")]
    [InlineData(@"C:\a.mp4:stream")]
    [InlineData("C:\\a|b.mp4")]
    [InlineData(@"C:\con.mp4")]
    [InlineData(@"\\servidor\solo.mp4")]
    public void Device_paths_streams_relative_and_malformed_paths_are_rejected(string? path) => Assert.False(Validate(path).IsValid);

    [Fact]
    public void Unsupported_extensions_say_which_formats_are_accepted_and_how_to_convert()
    {
        var v = Validate(@"C:\Videos\anuncio.avi");
        Assert.False(v.IsValid);
        Assert.Equal(MediaErrorCodes.UnsupportedContainer, v.Code);
        Assert.Contains("MP4", v.Message);
        Assert.Contains("HandBrake", v.Message);
        Assert.DoesNotContain("anuncio", v.Message);   // el mensaje no cita el nombre del archivo
    }

    [Fact]
    public void Empty_missing_and_oversized_files_are_rejected_with_their_own_code()
    {
        var fs = new FakeMediaFileSystem();
        fs.Files[@"C:\v\vacio.mp4"] = new FileProbe(true, 0, null);
        fs.Files[@"C:\v\falta.mp4"] = new FileProbe(false, 0, null);
        fs.Files[@"C:\v\enorme.mp4"] = new FileProbe(true, MediaLimits.MaxBytes + 1, null);
        fs.Files[@"C:\v\limite.mp4"] = new FileProbe(true, MediaLimits.MaxBytes, null);
        Assert.False(Validate(@"C:\v\vacio.mp4", fs).IsValid);
        Assert.False(Validate(@"C:\v\falta.mp4", fs).IsValid);
        Assert.Equal(MediaErrorCodes.TooLarge, Validate(@"C:\v\enorme.mp4", fs).Code);
        Assert.True(Validate(@"C:\v\limite.mp4", fs).IsValid);
    }

    // ───────────── sonda de cabeceras ─────────────

    [Fact]
    public void An_mp4_reports_container_codecs_duration_and_size_with_moov_at_the_start_or_at_the_end()
    {
        foreach (var atEnd in new[] { false, true })
        {
            var p = Probe(SyntheticMedia.Mp4("avc1", "mp4a", 1280, 720, 12_500, moovAtEnd: atEnd), ".mp4");
            Assert.Equal(("MP4", "H.264", "AAC", 12_500L, 1280, 720, true), (p.Container, p.VideoCodec, p.AudioCodec, p.DurationMs, p.Width, p.Height, p.HasAudio));
        }
        var silent = Probe(SyntheticMedia.Mp4("avc1", null), ".mp4");
        Assert.False(silent.HasAudio);
        Assert.Null(silent.AudioCodec);
    }

    [Theory]
    [InlineData("hvc1", "HEVC")] [InlineData("hev1", "HEVC")] [InlineData("av01", "AV1")] [InlineData("mp4v", "MPEG-4")] [InlineData("apch", "ProRes")]
    public void Video_fourccs_map_to_readable_codec_names(string fourcc, string expected) =>
        Assert.Equal(expected, Probe(SyntheticMedia.Mp4(fourcc), ".mp4").VideoCodec);

    [Fact]
    public void A_webm_reports_codecs_duration_and_size()
    {
        var p = Probe(SyntheticMedia.Webm("V_VP9", "A_OPUS", 1920, 1080, 7000), ".webm");
        Assert.Equal(("WebM", "VP9", "Opus", 7000L, 1920, 1080), (p.Container, p.VideoCodec, p.AudioCodec, p.DurationMs, p.Width, p.Height));
        Assert.Equal("H.264", Probe(SyntheticMedia.Webm("V_MPEG4/ISO/AVC", null), ".webm").VideoCodec);
    }

    [Fact]
    public void Audio_formats_report_codec_and_duration_and_an_mp3_without_xing_is_marked_approximate()
    {
        var xing = Probe(SyntheticMedia.Mp3(xingFrames: 1000, id3: true), ".mp3");
        Assert.Equal(("MP3", "MP3", false), (xing.Container, xing.AudioCodec, xing.DurationApproximate));
        Assert.Equal((long)(1000 * 1152 / 44100.0 * 1000), xing.DurationMs);

        var plain = Probe(SyntheticMedia.Mp3(xingFrames: null, plainFrames: 50), ".mp3");
        Assert.True(plain.DurationApproximate);
        Assert.InRange(plain.DurationMs!.Value, 1, 10_000);

        var wav = Probe(SyntheticMedia.Wav(44100, 2, 2.5), ".wav");
        Assert.Equal(("WAV", "PCM", 2500L), (wav.Container, wav.AudioCodec, wav.DurationMs));
        Assert.Equal("WAV-0x2", Probe(SyntheticMedia.Wav(formatTag: 2), ".wav").AudioCodec);

        var flac = Probe(SyntheticMedia.Flac(44100, 44100 * 3), ".flac");
        Assert.Equal(("FLAC", "FLAC", 3000L), (flac.Container, flac.AudioCodec, flac.DurationMs));
        Assert.Equal("AAC", Probe(SyntheticMedia.Aac(), ".aac").AudioCodec);
    }

    [Fact]
    public void Images_report_their_dimensions()
    {
        Assert.Equal((1920, 1080), (Probe(SyntheticMedia.Png(1920, 1080), ".png").Width ?? 0, Probe(SyntheticMedia.Png(1920, 1080), ".png").Height ?? 0));
        var jpeg = Probe(SyntheticMedia.Jpeg(640, 360), ".jpg");
        Assert.Equal(("JPEG", 640, 360), (jpeg.Container, jpeg.Width, jpeg.Height));
        var webp = Probe(SyntheticMedia.WebpExtended(800, 600), ".webp");
        Assert.Equal(("WebP", 800, 600), (webp.Container, webp.Width, webp.Height));
    }

    [Fact]
    public void The_verdict_accepts_h264_warns_about_hevc_and_ac3_and_rejects_what_cannot_play()
    {
        MediaVerdict J(byte[] b, string ext, MediaItemKind kind) => MediaProbe.Judge(kind, Probe(b, ext));
        Assert.Equal(new MediaVerdict(null, null), J(SyntheticMedia.Mp4("avc1", "mp4a"), ".mp4", MediaItemKind.Video));
        Assert.Equal(new MediaVerdict(null, MediaErrorCodes.VideoCodecWarning), J(SyntheticMedia.Mp4("hvc1"), ".mp4", MediaItemKind.Video));   // HEVC: se acepta CON aviso (decisión aprobada)
        Assert.Equal(new MediaVerdict(null, MediaErrorCodes.AudioCodecWarning), J(SyntheticMedia.Mp4("avc1", "ac-3"), ".mp4", MediaItemKind.Video));
        Assert.Equal(MediaErrorCodes.VideoCodecUnsupported, J(SyntheticMedia.Mp4("mp4v"), ".mp4", MediaItemKind.Video).RejectCode);
        Assert.Equal(MediaErrorCodes.VideoCodecUnsupported, J(SyntheticMedia.Mp4("apch"), ".mp4", MediaItemKind.Video).RejectCode);
        Assert.Equal(MediaErrorCodes.UnsupportedContainer, J(new byte[2000], ".mp4", MediaItemKind.Video).RejectCode);   // no es un MP4
        Assert.Equal(new MediaVerdict(null, null), J(SyntheticMedia.Webm("V_VP8", "A_VORBIS"), ".webm", MediaItemKind.Video));
        Assert.Equal(new MediaVerdict(null, MediaErrorCodes.AudioCodecUnsupported), J(SyntheticMedia.Mp4("avc1", "alac"), ".mp4", MediaItemKind.Video));   // el video sirve de fondo (va en silencio)
        // Audio
        Assert.Equal(new MediaVerdict(null, null), J(SyntheticMedia.Mp3(), ".mp3", MediaItemKind.Audio));
        Assert.Equal(MediaErrorCodes.AudioCodecUnsupported, J(SyntheticMedia.Wav(formatTag: 2), ".wav", MediaItemKind.Audio).RejectCode);
        Assert.Equal(MediaErrorCodes.AudioCodecUnsupported, J(SyntheticMedia.Mp4("", "ac-3"), ".m4a", MediaItemKind.Audio).RejectCode);
        // Imágenes
        Assert.Equal(new MediaVerdict(null, null), J(SyntheticMedia.Png(), ".png", MediaItemKind.Image));
        Assert.Equal(MediaErrorCodes.UnsupportedContainer, J(new byte[100], ".png", MediaItemKind.Image).RejectCode);
    }

    [Fact]
    public void Hostile_truncated_and_random_inputs_never_throw_and_stay_within_budget()
    {
        var valid = new[] { (SyntheticMedia.Mp4(), ".mp4"), (SyntheticMedia.Mp4(moovAtEnd: true), ".mp4"), (SyntheticMedia.Webm(), ".webm"), (SyntheticMedia.Mp3(), ".mp3"), (SyntheticMedia.Wav(), ".wav"),
            (SyntheticMedia.Flac(), ".flac"), (SyntheticMedia.Png(), ".png"), (SyntheticMedia.Jpeg(), ".jpg"), (SyntheticMedia.WebpExtended(), ".webp"), (SyntheticMedia.Aac(), ".aac") };
        var random = new Random(20261126);
        var clock = Stopwatch.StartNew();
        var runs = 0;
        foreach (var (bytes, ext) in valid)
            for (var cut = 0; cut <= bytes.Length; cut += Math.Max(1, bytes.Length / 40)) { Probe(bytes[..cut], ext); runs++; }   // cada archivo cortado en 40 sitios
        foreach (var ext in new[] { ".mp4", ".m4a", ".webm", ".mp3", ".wav", ".flac", ".aac", ".png", ".jpg", ".webp" })
            for (var i = 0; i < 300; i++)
            {
                var noise = new byte[random.Next(0, 4096)]; random.NextBytes(noise);
                Probe(noise, ext); runs++;
                var mutated = (byte[])valid[random.Next(valid.Length)].Item1.Clone();   // un archivo válido con bytes alterados
                for (var k = 0; k < 8 && mutated.Length > 0; k++) mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
                Probe(mutated, ext); runs++;
            }
        Assert.True(runs > 6000);
        Assert.True(clock.ElapsedMilliseconds < 8000, $"{runs} sondas tardaron {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_box_with_an_absurd_size_and_a_deeply_nested_moov_do_not_blow_up()
    {
        var absurd = SyntheticMedia.Mp4();
        absurd[32] = 0xFF; absurd[33] = 0xFF; absurd[34] = 0xFF; absurd[35] = 0xF0;   // el tamaño del siguiente átomo es enorme
        _ = Probe(absurd, ".mp4");

        var nested = SyntheticMedia.Box("trak");
        for (var i = 0; i < 200; i++) nested = SyntheticMedia.Box("mdia", nested);
        var bomb = SyntheticMedia.Box("ftyp", new byte[8]).Concat(SyntheticMedia.Box("moov", nested)).ToArray();
        var p = Probe(bomb, ".mp4");
        Assert.Equal("MP4", p.Container);   // reconocido, sin datos, sin desbordar la pila
    }

    [Fact]
    public void A_stream_that_cannot_seek_is_unknown_not_an_error()
    {
        using var s = new NonSeekable(SyntheticMedia.Mp4());
        Assert.Equal(MediaProbeResult.Unknown, MediaProbe.Probe(s, ".mp4"));
    }

    private sealed class NonSeekable(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
