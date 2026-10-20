using PassDesk.Core.Model;
using PassDesk.Core.Search;
using PassDesk.Crypto;
using PassDesk.Storage;

namespace PassDesk.Tests;

public class StorageTests
{
    [Fact]
    public void Document_serialization_round_trips_all_fields()
    {
        using var document = new VaultDocument
        {
            LastImport = new ImportRecord(new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero), "apple-export", 3, 1, 2),
        };
        var entry = Fake.Entry("Banco \"Ficticio\"", "https://banco-ficticio.example", "usuario.prueba@example.com", Fake.Secret2, "Nota con\nsalto y \"comillas\" 🙂");
        entry.SetOtpAuth(ProtectedSecret.FromString("otpauth://totp/Prueba?secret=JBSWY3DPEHPK3PXP"));
        entry.IsCategoryManual = true;
        entry.Category = Category.Banking;
        entry.ImportedUtc = new DateTimeOffset(2026, 10, 1, 8, 30, 0, TimeSpan.Zero);
        document.Entries.Add(entry);

        using var json = VaultDocumentSerializer.Serialize(document);
        using var copy = VaultDocumentSerializer.Deserialize(json.Span);

        var restored = Assert.Single(copy.Entries);
        Assert.Equal(entry.Id, restored.Id);
        Assert.Equal(entry.Title, restored.Title);
        Assert.Equal(entry.Url, restored.Url);
        Assert.Equal(entry.Username, restored.Username);
        Assert.Equal(Fake.Secret2, restored.Password.Reveal());
        Assert.Equal(entry.Notes.Reveal(), restored.Notes.Reveal());
        Assert.Equal(entry.OtpAuth.Reveal(), restored.OtpAuth.Reveal());
        Assert.Equal(Category.Banking, restored.Category);
        Assert.True(restored.IsCategoryManual);
        Assert.Equal(entry.ImportedUtc, restored.ImportedUtc);
        Assert.Equal(document.LastImport, copy.LastImport);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no es json")]
    [InlineData("{\"entries\":[{\"id\":\"no-guid\"}]}")]
    [InlineData("{\"v\":99,\"entries\":[]}")]
    [InlineData("{\"v\":2,\"entries\":[{\"pw\":\"%%%\"}]}")]
    public void Invalid_document_content_is_rejected(string content)
    {
        Assert.Throws<VaultFormatException>(() => VaultDocumentSerializer.Deserialize(Fake.Utf8(content)));
    }

    [Fact]
    public void Dpapi_store_writes_encrypted_file_and_keeps_previous_version()
    {
        using var temp = new TempDirectory();
        var store = new DpapiVaultStore(temp.File("vault.pdv"));
        using var keyring = VaultKeyring.Create(Fake.MasterPassword, Fake.FastIterations);
        byte[] envelope = keyring.Seal(Fake.Utf8("{\"pw\":\"" + Fake.Secret + "\"}"));

        store.WriteEnvelope(envelope);
        store.WriteEnvelope(envelope);

        byte[] onDisk = File.ReadAllBytes(store.FilePath);
        Assert.Equal("PDLV"u8.ToArray(), onDisk[..4]);
        Assert.False(Fake.ContainsText(onDisk, Fake.Secret));
        Assert.True(File.Exists(store.FilePath + ".bak"));
        Assert.False(File.Exists(store.FilePath + ".tmp"));
        Assert.Equal(envelope, store.ReadEnvelope());
    }

    [Fact]
    public void Dpapi_store_rejects_foreign_files()
    {
        using var temp = new TempDirectory();
        string path = temp.File("vault.pdv");
        File.WriteAllText(path, "no es una bóveda");

        Assert.Throws<VaultFormatException>(() => new DpapiVaultStore(path).ReadEnvelope());
    }

    [Fact]
    public void Backup_file_wraps_and_validates_magic()
    {
        byte[] envelope = Fake.Utf8("sobre de prueba");
        Assert.Equal(envelope, BackupFile.Unwrap(BackupFile.Wrap(envelope)));
        Assert.Throws<VaultFormatException>(() => BackupFile.Unwrap(Fake.Utf8("XXXX12345")));
    }

    [Fact]
    public void Settings_round_trip_and_fall_back_to_defaults()
    {
        using var temp = new TempDirectory();
        var store = new SettingsStore(temp.File("settings.json"));

        var defaults = store.Load();
        Assert.Equal(5, defaults.AutoLockMinutes);
        Assert.Equal(30, defaults.ClipboardClearSeconds);

        defaults.AutoLockMinutes = 15;
        defaults.Theme = ThemePreference.Dark;
        defaults.SortMode = SortMode.ModifiedNewest;
        store.Save(defaults);

        var loaded = store.Load();
        Assert.Equal(15, loaded.AutoLockMinutes);
        Assert.Equal(ThemePreference.Dark, loaded.Theme);
        Assert.Equal(SortMode.ModifiedNewest, loaded.SortMode);

        File.WriteAllText(store.FilePath, "{ roto");
        Assert.Equal(5, store.Load().AutoLockMinutes);
    }

    [Fact]
    public void Settings_saved_with_utf8_bom_are_loaded()
    {
        using var temp = new TempDirectory();
        string path = temp.File("settings.json");
        File.WriteAllText(path, "{ \"AutoLockMinutes\": 15, \"ScreenCaptureProtection\": false, \"Theme\": \"Dark\" }", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var loaded = new SettingsStore(path).Load();

        Assert.Equal(15, loaded.AutoLockMinutes);
        Assert.False(loaded.ScreenCaptureProtection);
        Assert.Equal(ThemePreference.Dark, loaded.Theme);
    }

    [Fact]
    public void Settings_out_of_range_values_are_normalized()
    {
        var settings = new AppSettings { AutoLockMinutes = 0, ClipboardClearSeconds = 100_000, QuickSearchHotkey = "" }.Normalize();

        Assert.Equal(1, settings.AutoLockMinutes);
        Assert.Equal(600, settings.ClipboardClearSeconds);
        Assert.Equal("Ctrl+Shift+Space", settings.QuickSearchHotkey);
    }
}
