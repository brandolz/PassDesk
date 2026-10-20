using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using PassDesk.Core.Model;
using PassDesk.Crypto;
using PassDesk.Import;
using PassDesk.Import.Sources;

namespace PassDesk.Tests;

public class ImportTests
{
    // Exportación ficticia con el formato de Contraseñas de Apple (Title,URL,Username,Password,Notes,OTPAuth).
    private const string AppleCsv =
        "Title,URL,Username,Password,Notes,OTPAuth\r\n" +
        "Facebook,https://www.facebook.com/,perfil.prueba@example.com,Ficticia#Clave-2026!,,\r\n" +
        "Banco Ficticio,https://banca.banco-ficticio.example/login,12345678Z,\"Con,coma y \"\"comillas\"\"\",\"Nota\nmultilínea\",\r\n" +
        "Steam,https://store.steampowered.com,jugador_ficticio,Zz9_FakePass,,otpauth://totp/Steam:jugador?secret=JBSWY3DPEHPK3PXP&issuer=Steam\r\n";

    [Fact]
    public void Parses_apple_passwords_export()
    {
        using var result = ExportFileReader.ParseText(AppleCsv, "Prueba");

        Assert.Equal(3, result.Items.Count);
        Assert.Empty(result.Issues);
        Assert.True(result.SourceWasPlaintext);
        Assert.Contains("Apple", result.FormatName);

        var facebook = result.Items[0];
        Assert.Equal("Facebook", facebook.Title);
        Assert.Equal("perfil.prueba@example.com", facebook.Username);
        Assert.Equal(Fake.Secret, facebook.Password.Reveal());

        var bank = result.Items[1];
        Assert.Equal("Con,coma y \"comillas\"", bank.Password.Reveal());
        Assert.Equal("Nota\nmultilínea", bank.Notes.Reveal());

        var steam = result.Items[2];
        Assert.False(steam.OtpAuth.IsEmpty);
        Assert.True(result.SourceHadVerificationCodes);
    }

    [Fact]
    public void Parses_icloud_windows_and_browser_style_header()
    {
        const string csv = "name,url,username,password\nCorreo,https://mail.example.com,yo@example.com,Clave-Ficticia-1\n";
        using var result = ExportFileReader.ParseText(csv, "Prueba");

        var item = Assert.Single(result.Items);
        Assert.Equal("Correo", item.Title);
        Assert.Equal("Clave-Ficticia-1", item.Password.Reveal());
        Assert.Contains("iCloud", result.FormatName);
    }

    [Fact]
    public void Parses_semicolon_csv_with_bom_and_spanish_headers()
    {
        string csv = "﻿Nombre;Sitio web;Usuario;Contraseña;Notas\r\nTienda;https://tienda.example;cliente;Ficticia;1\r\n";
        using var result = ExportFileReader.ParseCsv(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv[1..])).ToArray(), "Prueba");

        var item = Assert.Single(result.Items);
        Assert.Equal("Tienda", item.Title);
        Assert.Equal("cliente", item.Username);
        Assert.Equal("Ficticia", item.Password.Reveal());
    }

    [Fact]
    public void Parses_utf16_and_latin1_encodings()
    {
        const string csv = "Title,URL,Username,Password\r\nCafé,https://cafe.example,josé,Contraseña-ñ\r\n";

        using (var utf16 = ExportFileReader.ParseCsv(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(csv)).ToArray(), "Prueba"))
        {
            Assert.Equal("Contraseña-ñ", Assert.Single(utf16.Items).Password.Reveal());
        }

        using var latin1 = ExportFileReader.ParseCsv(Encoding.Latin1.GetBytes(csv), "Prueba");
        var item = Assert.Single(latin1.Items);
        Assert.Equal("Café", item.Title);
        Assert.Equal("Contraseña-ñ", item.Password.Reveal());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    [InlineData("Title,URL,Username\r\nA,https://a.example,b\r\n")]
    [InlineData("columna1,columna2\r\n1,2\r\n")]
    [InlineData("Title,URL,Username,Password\r\nA,https://a.example,b,\"XyzSecreto\r\n")]
    [InlineData("Title,URL,Username,Password\r\nA,https://a.example,b,\"x\"QwBasura\r\n")]
    public void Invalid_files_are_rejected_with_a_safe_message(string content)
    {
        var ex = Assert.Throws<ImportFileException>(() => ExportFileReader.ParseText(content, "Prueba"));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.DoesNotContain("XyzSecreto", ex.Message);
        Assert.DoesNotContain("QwBasura", ex.Message);
    }

    [Fact]
    public void Binary_garbage_is_rejected()
    {
        byte[] garbage = RandomNumberGenerator.GetBytes(4096);
        garbage[10] = 0;
        Assert.Throws<ImportFileException>(() => ExportFileReader.ParseCsv(garbage, "Prueba"));
    }

    [Fact]
    public void Invalid_rows_are_reported_by_line_without_content()
    {
        const string csv =
            "Title,URL,Username,Password\r\n" +
            "Sin clave,https://a.example,usuario,\r\n" +
            ",,,ClaveSinIdentidad\r\n" +
            "Demasiadas,https://b.example,u,p,extra\r\n" +
            "Valida,https://c.example,u,ClaveValida\r\n";
        using var result = ExportFileReader.ParseText(csv, "Prueba");

        Assert.Single(result.Items);
        Assert.Equal(
            new[] { ParseIssueKind.MissingPassword, ParseIssueKind.MissingIdentity, ParseIssueKind.ColumnCountMismatch },
            result.Issues.Select(i => i.Kind).ToArray());
        Assert.Equal(new[] { 2, 3, 4 }, result.Issues.Select(i => i.Line).ToArray());
    }

    [Fact]
    public void Title_falls_back_to_domain()
    {
        using var result = ExportFileReader.ParseText("Title,URL,Username,Password\r\n,https://www.ejemplo.example/x,u,p\r\n", "Prueba");
        Assert.Equal("ejemplo.example", Assert.Single(result.Items).Title);
    }

    [Fact]
    public void Reads_csv_inside_safari_zip_export_without_extracting_to_disk()
    {
        using var temp = new TempDirectory();
        string zipPath = temp.File("Safari Export.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "Bookmarks.html", "<html></html>");
            WriteEntry(zip, "Passwords.csv", AppleCsv);
        }

        int filesBefore = Directory.GetFiles(temp.Path).Length;
        using var result = ExportFileReader.ReadFile(zipPath);

        Assert.Equal(3, result.Items.Count);
        Assert.Contains("ZIP", result.FormatName);
        Assert.Equal(filesBefore, Directory.GetFiles(temp.Path).Length);
    }

    [Fact]
    public void Zip_without_csv_and_corrupt_zip_are_rejected()
    {
        using var temp = new TempDirectory();
        string emptyZip = temp.File("vacio.zip");
        using (var zip = ZipFile.Open(emptyZip, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "History.json", "{}");
        }

        Assert.Throws<ImportFileException>(() => ExportFileReader.ReadFile(emptyZip));

        string corrupt = temp.File("roto.zip");
        File.WriteAllBytes(corrupt, [0x50, 0x4B, 0x03, 0x04, 1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Throws<ImportFileException>(() => ExportFileReader.ReadFile(corrupt));
    }

    [Fact]
    public void Reading_never_modifies_the_source_file()
    {
        using var temp = new TempDirectory();
        string path = temp.File("Passwords.csv");
        File.WriteAllText(path, AppleCsv);
        byte[] before = SHA256.HashData(File.ReadAllBytes(path));
        var writeTime = File.GetLastWriteTimeUtc(path);

        using (ExportFileReader.ReadFile(path))
        {
        }

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Missing_file_and_empty_file_are_rejected()
    {
        using var temp = new TempDirectory();
        Assert.Throws<ImportFileException>(() => ExportFileReader.ReadFile(temp.File("no-existe.csv")));
        File.WriteAllBytes(temp.File("vacio.csv"), []);
        Assert.Throws<ImportFileException>(() => ExportFileReader.ReadFile(temp.File("vacio.csv")));
    }

    [Fact]
    public void Detects_new_duplicate_conflict_and_in_file_duplicates()
    {
        var existing = new List<CredentialEntry>
        {
            Fake.Entry("Facebook", "https://facebook.com", "Perfil.Prueba@example.com", Fake.Secret),
            Fake.Entry("Banco Ficticio", "https://banca.banco-ficticio.example", "12345678Z", "Clave-Antigua"),
        };

        const string csv =
            "Title,URL,Username,Password,Notes,OTPAuth\r\n" +
            "Facebook,https://www.facebook.com/,perfil.prueba@example.com,Ficticia#Clave-2026!,,\r\n" +   // idéntica (dominio y usuario normalizados)
            "Banco Ficticio,https://banca.banco-ficticio.example/login,12345678Z,Clave-Nueva,,\r\n" +      // conflicto
            "Nueva,https://nueva.example,u,ClaveNueva,,\r\n" +                                               // nueva
            "Nueva,https://nueva.example,u,ClaveNueva,,\r\n";                                                // repetida en el archivo
        using var parsed = ExportFileReader.ParseText(csv, "Prueba");
        var preview = ImportAnalyzer.Analyze(existing, parsed);

        Assert.Equal(
            new[] { ImportItemStatus.Duplicate, ImportItemStatus.Conflict, ImportItemStatus.New, ImportItemStatus.DuplicateInFile },
            preview.Items.Select(i => i.Status).ToArray());
        Assert.Equal(1, preview.NewCount);
        Assert.Equal(2, preview.DuplicateCount);
        Assert.Equal(1, preview.ConflictCount);
        Assert.Equal(existing[1].Id, preview.Items[1].ExistingId);

        foreach (var entry in existing)
        {
            entry.Dispose();
        }
    }

    [Theory]
    [InlineData(ConflictResolution.UpdateExisting, 1, 1, "Clave-Nueva")]
    [InlineData(ConflictResolution.KeepBoth, 2, 0, "Clave-Antigua")]
    [InlineData(ConflictResolution.Skip, 1, 0, "Clave-Antigua")]
    public void Applies_conflict_resolution(ConflictResolution resolution, int added, int updated, string expectedPassword)
    {
        var original = Fake.Entry("Banco Ficticio", "https://banca.banco-ficticio.example", "12345678Z", "Clave-Antigua", "nota local");
        var entries = new List<CredentialEntry> { original };
        const string csv =
            "Title,URL,Username,Password\r\n" +
            "Banco Ficticio,https://banca.banco-ficticio.example,12345678Z,Clave-Nueva\r\n" +
            "Otra,https://otra.example,u,ClaveOtra\r\n";
        using var parsed = ExportFileReader.ParseText(csv, "Prueba");
        var preview = ImportAnalyzer.Analyze(entries, parsed);
        var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

        var outcome = ImportApplier.Apply(entries, preview, resolution, now, EntrySources.AppleExport);

        Assert.Equal(added, outcome.Added);
        Assert.Equal(updated, outcome.Updated);
        Assert.Equal(expectedPassword, original.Password.Reveal());
        Assert.Equal("nota local", original.Notes.Reveal()); // nunca se borran notas existentes
        Assert.All(entries.Where(e => e != original), e => Assert.Equal<DateTimeOffset?>(now, e.ImportedUtc));

        // La importación copia los secretos: liberar el resultado no afecta a la bóveda.
        parsed.Dispose();
        Assert.Equal("ClaveOtra", entries.Single(e => e.Title == "Otra").Password.Reveal());

        foreach (var entry in entries)
        {
            entry.Dispose();
        }
    }

    [Fact]
    public void Imported_entries_are_categorized()
    {
        var entries = new List<CredentialEntry>();
        using var parsed = ExportFileReader.ParseText(AppleCsv, "Prueba");
        ImportApplier.Apply(entries, ImportAnalyzer.Analyze(entries, parsed), ConflictResolution.Skip, DateTimeOffset.UtcNow, EntrySources.AppleExport);

        Assert.Equal(new[] { Category.Social, Category.Banking, Category.Gaming }, entries.Select(e => e.Category).ToArray());
        foreach (var entry in entries)
        {
            entry.Dispose();
        }
    }

    [Fact]
    public void Secure_delete_overwrites_and_removes_the_export()
    {
        using var temp = new TempDirectory();
        string path = temp.File("Passwords.csv");
        File.WriteAllText(path, AppleCsv);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var result = SecureFileDeleter.Delete(path);

        Assert.True(result.Deleted);
        Assert.Null(result.Error);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(temp.Path));
        Assert.Contains(SecureFileDeleter.GeneralWarning, result.Warnings);

        var missing = SecureFileDeleter.Delete(path);
        Assert.False(missing.Deleted);
    }

    [Fact]
    public async Task No_automatic_sync_source_is_available()
    {
        var registry = CredentialSourceRegistry.CreateDefault();

        Assert.Null(registry.FindAutomaticSyncSource());

        var icloud = registry.Get("icloud-direct");
        Assert.Equal(SourceAvailability.Unavailable, icloud.GetStatus().Availability);
        Assert.Contains("API", icloud.GetStatus().Explanation);
        await Assert.ThrowsAsync<SyncSourceUnavailableException>(() => icloud.FetchAsync(new CredentialSourceRequest(null), CancellationToken.None));

        var file = registry.Get("apple-export");
        Assert.Equal(SourceAvailability.Available, file.GetStatus().Availability);
        await Assert.ThrowsAsync<ImportFileException>(() => file.FetchAsync(new CredentialSourceRequest(null), CancellationToken.None));
    }

    [Fact]
    public async Task Export_file_source_reads_selected_file()
    {
        using var temp = new TempDirectory();
        string path = temp.File("Passwords.csv");
        File.WriteAllText(path, AppleCsv);

        using var result = await new AppleExportFileSource().FetchAsync(new CredentialSourceRequest(path), CancellationToken.None);
        Assert.Equal(3, result.Items.Count);
    }

    [Fact]
    public void Backup_entries_keep_their_metadata()
    {
        var entry = Fake.Entry("Manual", "https://manual.example", "u", Fake.Secret, category: Category.Shopping);
        entry.IsCategoryManual = true;
        using var parsed = ParseResult.FromEntries([entry], "Copia");
        var target = new List<CredentialEntry>();

        ImportApplier.Apply(target, ImportAnalyzer.Analyze(target, parsed), ConflictResolution.Skip, DateTimeOffset.UtcNow, EntrySources.Backup);

        var restored = Assert.Single(target);
        Assert.Equal(Category.Shopping, restored.Category);
        Assert.True(restored.IsCategoryManual);
        Assert.Equal(entry.CreatedUtc, restored.CreatedUtc);
        Assert.False(parsed.SourceWasPlaintext);
        entry.Dispose();
        restored.Dispose();
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
