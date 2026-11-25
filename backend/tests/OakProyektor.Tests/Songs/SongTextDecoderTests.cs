using System.Text;
using OakProyektor.Core.Songs;
using OakProyektor.Data.Songs;

namespace OakProyektor.Tests.Songs;

/// <summary>Lectura de un .txt: UTF-8 estricto, respaldo a Windows-1252 indicándolo, y rechazo claro de lo que no es texto. Sin letra real: todo inventado.</summary>
public sealed class SongTextDecoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oak-proyektor-tests", "songtxt-" + Guid.NewGuid().ToString("N"));
    public SongTextDecoderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static byte[] Utf8(string text, bool bom = false) => [.. (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : []), .. Encoding.UTF8.GetBytes(text)];

    private const string Accents = "Lumarí señor ñandú tenóko ¿qué? ¡sí! — «cita» €";

    [Fact]
    public void Valid_utf8_is_read_as_utf8_without_warnings()
    {
        var d = SongTextDecoder.Decode(Utf8(Accents));

        Assert.Equal(Accents, d.Text);
        Assert.Equal(("utf-8", false), (d.Encoding, d.FellBack));
        Assert.Empty(d.Warnings);
    }

    [Fact]
    public void A_utf8_bom_is_detected_and_removed()
    {
        var d = SongTextDecoder.Decode(Utf8(Accents, bom: true));

        Assert.Equal(Accents, d.Text);
        Assert.Equal("utf-8-bom", d.Encoding);
    }

    [Fact]
    public void Text_that_is_not_utf8_falls_back_to_windows_1252_and_says_so()
    {
        // «Lumarí señor ñandú» en Windows-1252: í = E D, ñ = F1, ú = FA; y «€» = 80, comillas tipográficas = 93/94.
        byte[] bytes = [.. "Lumar"u8, 0xED, .. " se"u8, 0xF1, .. "or "u8, 0xF1, .. "and"u8, 0xFA, .. " "u8, 0x80, 0x20, 0x93, .. "x"u8, 0x94];

        var d = SongTextDecoder.Decode(bytes);

        Assert.Equal("Lumarí señor ñandú € “x”", d.Text);
        Assert.Equal(("windows-1252", true), (d.Encoding, d.FellBack));
        Assert.Contains(d.Warnings, w => w.Contains("no está en UTF-8") && w.Contains("Windows-1252"));
    }

    [Fact]
    public void Latin1_accents_are_never_decoded_as_utf8_garbage()
    {
        byte[] bytes = [.. "caf"u8, 0xE9];   // «café» en Latin-1: E9 solo no es UTF-8 válido

        Assert.Equal("café", SongTextDecoder.Decode(bytes).Text);
    }

    [Fact]
    public void Bytes_with_no_windows_1252_equivalent_become_replacement_characters_with_a_warning()
    {
        byte[] bytes = [.. "a"u8, 0x81, .. "b"u8, 0x9D, .. "c"u8];

        var d = SongTextDecoder.Decode(bytes);

        Assert.Equal("a�b�c", d.Text);
        Assert.Contains(d.Warnings, w => w.Contains("2 byte(s)"));
        Assert.Contains(d.Warnings, w => w.Contains("�"));
    }

    [Fact]
    public void Utf16_with_bom_is_decoded_in_both_byte_orders()
    {
        var le = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes(Accents)).ToArray();
        var be = new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(Accents)).ToArray();

        Assert.Equal((Accents, "utf-16le"), (SongTextDecoder.Decode(le).Text, SongTextDecoder.Decode(le).Encoding));
        Assert.Equal((Accents, "utf-16be"), (SongTextDecoder.Decode(be).Text, SongTextDecoder.Decode(be).Encoding));
    }

    [Fact]
    public void Invalid_utf16_is_rejected_as_not_text()
    {
        var ex = Assert.Throws<SongException>(() => SongTextDecoder.Decode(new byte[] { 0xFF, 0xFE, 0x00, 0xD8 }));   // un sustituto alto sin pareja

        Assert.Equal(SongErrorCodes.NotText, ex.Code);
    }

    [Fact]
    public void Doubly_encoded_text_is_flagged_as_probable_mojibake_but_still_read()
    {
        var garbled = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes("señor")).Select(c => c).ToArray();
        // «señor» guardado como UTF-8 y vuelto a leer como Windows-1252 da «seÃ±or»; si eso se guarda otra vez como UTF-8, el archivo es UTF-8 válido pero está dañado.
        var text = "seÃ±or caf" + "Ã©";

        var d = SongTextDecoder.Decode(Utf8(text));

        Assert.Equal(text, d.Text);
        Assert.Contains(d.Warnings, w => w.Contains("acentos parecen dañados"));
        Assert.True(SongTextDecoder.LooksMojibake(text));
        Assert.False(SongTextDecoder.LooksMojibake(Accents));
        Assert.NotEmpty(garbled);
    }

    [Fact]
    public void Replacement_characters_in_the_source_are_reported()
    {
        var d = SongTextDecoder.Decode(Utf8("una linea con � dentro"));

        Assert.Contains(d.Warnings, w => w.Contains("�"));
    }

    [Fact]
    public void Empty_files_decode_to_empty_text()
    {
        Assert.Equal("", SongTextDecoder.Decode([]).Text);
    }

    [Theory]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 }, "Word o PowerPoint")]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }, "PDF")]
    [InlineData(new byte[] { 0x7B, 0x5C, 0x72, 0x74, 0x66, 0x31 }, "RTF")]
    [InlineData(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1 }, "documento antiguo de Office")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }, "imagen")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "imagen")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "imagen")]
    [InlineData(new byte[] { 0x61, 0x62, 0x00, 0x63 }, "binario")]
    public void Files_that_are_not_plain_text_are_rejected_with_a_clear_message(byte[] bytes, string expected)
    {
        var ex = Assert.Throws<SongException>(() => SongTextDecoder.Decode(bytes));

        Assert.Equal(SongErrorCodes.NotText, ex.Code);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Many_control_characters_mean_binary()
    {
        var bytes = Enumerable.Range(0, 400).Select(i => (byte)(i % 3 == 0 ? 0x01 : 0x61)).ToArray();

        Assert.Equal(SongErrorCodes.NotText, Assert.Throws<SongException>(() => SongTextDecoder.Decode(bytes)).Code);
    }

    [Fact]
    public void Oversized_input_is_rejected_by_size()
    {
        var ex = Assert.Throws<SongException>(() => SongTextDecoder.Decode(new byte[SongLimits.MaxFileBytes + 1]));

        Assert.Equal(SongErrorCodes.FileTooLarge, ex.Code);
    }

    // ───────────────────────────── desde un archivo ─────────────────────────────

    [Fact]
    public async Task A_file_is_read_and_decoded()
    {
        var path = Path.Combine(_dir, "a.txt");
        await File.WriteAllBytesAsync(path, Utf8("[Coro]\n" + Accents));

        var d = await SongTextFile.ReadAsync(path, CancellationToken.None);

        Assert.Equal("[Coro]\n" + Accents, d.Text);
    }

    [Fact]
    public async Task A_missing_file_or_a_folder_gives_a_clear_error()
    {
        Assert.Equal(SongErrorCodes.FileMissing, (await Assert.ThrowsAsync<SongException>(() => SongTextFile.ReadAsync(Path.Combine(_dir, "no-existe.txt"), CancellationToken.None))).Code);
        Assert.Equal(SongErrorCodes.FileMissing, (await Assert.ThrowsAsync<SongException>(() => SongTextFile.ReadAsync(_dir, CancellationToken.None))).Code);
        Assert.Equal(SongErrorCodes.FileMissing, (await Assert.ThrowsAsync<SongException>(() => SongTextFile.ReadAsync("", CancellationToken.None))).Code);
    }

    [Fact]
    public async Task A_big_file_is_refused_before_reading_it_all()
    {
        var path = Path.Combine(_dir, "grande.txt");
        await File.WriteAllBytesAsync(path, new byte[SongLimits.MaxFileBytes + 10]);

        var ex = await Assert.ThrowsAsync<SongException>(() => SongTextFile.ReadAsync(path, CancellationToken.None));

        Assert.Equal(SongErrorCodes.FileTooLarge, ex.Code);
        Assert.Contains("máximo", ex.Message);
    }

    [Fact]
    public async Task A_binary_file_with_a_txt_name_is_refused()
    {
        var path = Path.Combine(_dir, "falso.txt");
        await File.WriteAllBytesAsync(path, [0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0]);

        var ex = await Assert.ThrowsAsync<SongException>(() => SongTextFile.ReadAsync(path, CancellationToken.None));

        Assert.Equal(SongErrorCodes.NotText, ex.Code);
    }

    [Fact]
    public async Task A_file_open_for_reading_elsewhere_can_still_be_read()
    {
        var path = Path.Combine(_dir, "abierto.txt");
        await File.WriteAllBytesAsync(path, Utf8("[Coro]\nuno"));
        await using var other = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Assert.Equal("[Coro]\nuno", (await SongTextFile.ReadAsync(path, CancellationToken.None)).Text);
    }
}
