using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PassDesk.App.Views;
using PassDesk.Auth;
using PassDesk.Core.Model;
using PassDesk.Crypto;
using PassDesk.Import;
using PassDesk.Storage;

namespace PassDesk.Tests;

/// <summary>Argon2id para la clave de la bóveda (y por tanto de las copias) y política de contraseña maestra.</summary>
public class KdfHardeningTests
{
    [Fact]
    public void New_keyrings_use_argon2id_with_64_mib_and_round_trip()
    {
        using var keyring = VaultKeyring.Create(Fake.MasterPassword);
        Assert.Equal(KdfAlgorithm.Argon2id64MiB, keyring.Header.Kdf.Algorithm);
        Assert.Equal(KdfSettings.Argon2DefaultPasses, keyring.Header.Kdf.Cost);
        Assert.Equal(64 * 1024, KdfSettings.Argon2MemoryKiB);

        byte[] envelope = keyring.Seal(Fake.Utf8(Fake.Secret));
        using var reopened = VaultKeyring.Unlock(VaultKeyring.ReadHeader(envelope), Fake.MasterPassword);
        using var plain = reopened.Open(envelope);
        Assert.Equal(Fake.Utf8(Fake.Secret), plain.Span.ToArray());
        Assert.Throws<InvalidMasterPasswordException>(() => VaultKeyring.Unlock(VaultKeyring.ReadHeader(envelope), Fake.OtherMasterPassword));
    }

    [Fact]
    public void Argon2id_output_is_stable()
    {
        // Valor fijado con libsodium (64 MiB, 3 pasadas, 1 hilo). Si cambiara, las bóvedas existentes dejarían de abrirse.
        byte[] salt = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];
        byte[] key = new byte[32];
        KdfSettings.Argon2id().Derive("Ficticia-Argon2-Prueba", salt, key);
        Assert.Equal("E636CA810451CEB0C94898CC64888568539A96AF3B1FB7CAC3DBB0E419FDA3D3", Convert.ToHexString(key));
    }

    [Fact]
    public void Kdf_parameters_cannot_be_downgraded_or_forged()
    {
        using var keyring = VaultKeyring.Create(Fake.MasterPassword);
        byte[] envelope = keyring.Seal(Fake.Utf8("datos"));

        // Menos pasadas (dentro del rango): la cabecera está autenticada, así que no abre.
        byte[] fewerPasses = (byte[])envelope.Clone();
        BitConverter.GetBytes(KdfSettings.Argon2MinPasses).CopyTo(fewerPasses, 6);
        Assert.Throws<InvalidMasterPasswordException>(() => VaultKeyring.Unlock(VaultKeyring.ReadHeader(fewerPasses), Fake.MasterPassword));

        // Cambiar a PBKDF2 con 3 «iteraciones» o pasadas fuera de rango: formato rechazado.
        byte[] pbkdf2 = (byte[])envelope.Clone();
        pbkdf2[5] = (byte)KdfAlgorithm.Pbkdf2Sha256;
        Assert.Throws<VaultFormatException>(() => VaultKeyring.ReadHeader(pbkdf2));
        byte[] tooMany = (byte[])envelope.Clone();
        BitConverter.GetBytes(KdfSettings.Argon2MaxPasses + 1).CopyTo(tooMany, 6);
        Assert.Throws<VaultFormatException>(() => VaultKeyring.ReadHeader(tooMany));
    }

    [Fact]
    public void Rewrap_keeps_the_same_data_key_under_argon2id()
    {
        using var legacy = VaultKeyring.Create(Fake.MasterPassword, Fake.FastIterations);
        using var upgraded = legacy.Rewrap(Fake.MasterPassword, KdfSettings.Default);
        Assert.Equal(KdfAlgorithm.Pbkdf2Sha256, legacy.Header.Kdf.Algorithm);
        Assert.Equal(KdfAlgorithm.Argon2id64MiB, upgraded.Header.Kdf.Algorithm);

        // Misma clave de datos (comparada en tiempo constante) y se abre con la contraseña bajo la cabecera nueva.
        Assert.True(Fake.DataKey(legacy).ContentEquals(Fake.DataKey(upgraded)));

        byte[] envelope = upgraded.Seal(Fake.Utf8(Fake.Secret));
        using var reopened = VaultKeyring.Unlock(VaultKeyring.ReadHeader(envelope), Fake.MasterPassword);
        using var plain = reopened.Open(envelope);
        Assert.Equal(Fake.Utf8(Fake.Secret), plain.Span.ToArray());
    }

    [Theory]
    [InlineData("caballoverde", false)]            // dos palabras comunes: antes se aceptaba
    [InlineData("Abc-1234!xY", false)]             // fuerte pero de 11 caracteres
    [InlineData("Caballo-Verde-Bateria-7", true)]
    [InlineData("tres palabras largas siete", true)]
    public void Master_password_policy_requires_twelve_characters_and_strong(string candidate, bool acceptable)
    {
        Assert.Equal(acceptable, MasterPasswordPolicy.IsAcceptable(candidate));
    }

    [Fact]
    public void Legacy_pbkdf2_vault_is_upgraded_to_argon2id_on_unlock()
    {
        var store = new InMemoryVaultStore();
        var clock = new FakeClock();
        using (var legacy = new VaultSession(store, new InMemorySecurityStateStore(), clock, Fake.FastIterations))
        {
            legacy.CreateVault(Fake.MasterPassword);
            legacy.AddEntry(Fake.Entry("Antigua", "https://antigua.example", "u", Fake.Secret));
        }

        Assert.Equal((byte)KdfAlgorithm.Pbkdf2Sha256, store.Envelope![5]);

        using var session = new VaultSession(store, new InMemorySecurityStateStore(), clock);
        Assert.True(session.Unlock(Fake.MasterPassword).Succeeded);

        Assert.Equal((byte)KdfAlgorithm.Argon2id64MiB, store.Envelope![5]);
        Assert.Equal(Fake.Secret, Assert.Single(session.Entries).Password.Reveal());

        // Las copias de seguridad heredan la cabecera nueva y se abren con la misma contraseña.
        byte[] backup = session.CreateBackupEnvelope();
        Assert.Equal((byte)KdfAlgorithm.Argon2id64MiB, backup[5]);
        using (var restored = VaultSession.OpenBackupEnvelope(backup, Fake.MasterPassword))
        {
            Assert.Single(restored.Entries);
        }

        session.Lock(LockReason.Manual);
        Assert.True(session.Unlock(Fake.MasterPassword).Succeeded);
        Assert.Equal(UnlockStatus.WrongPassword, LockAndUnlock(session, Fake.OtherMasterPassword).Status);
    }

    [Fact]
    public void Failed_upgrade_keeps_the_vault_usable_and_retries_later()
    {
        var store = new FlakyVaultStore();
        var clock = new FakeClock();
        using (var legacy = new VaultSession(store, new InMemorySecurityStateStore(), clock, Fake.FastIterations))
        {
            legacy.CreateVault(Fake.MasterPassword);
        }

        using var session = new VaultSession(store, new InMemorySecurityStateStore(), clock);
        store.FailNextWrites = 1;
        Assert.True(session.Unlock(Fake.MasterPassword).Succeeded);
        Assert.Equal((byte)KdfAlgorithm.Pbkdf2Sha256, store.Envelope![5]);

        session.AddEntry(Fake.Entry("Guardada", "https://guardada.example", "u", Fake.Secret));
        session.Lock(LockReason.Manual);
        Assert.True(session.Unlock(Fake.MasterPassword).Succeeded);
        Assert.Equal((byte)KdfAlgorithm.Argon2id64MiB, store.Envelope![5]);
        Assert.Single(session.Entries);
    }

    [Theory]
    [InlineData("PlatformNotSupported")]
    [InlineData("TypeInitialization")]
    [InlineData("DllNotFound")]
    [InlineData("CryptoUnavailable")]
    public void Argon2id_failure_never_blocks_unlocking_a_legacy_vault(string failure)
    {
        var clock = new FakeClock();
        var store = Fake.NewVault(clock, Fake.Entry("Antigua", "https://antigua.example", "u", Fake.Secret));
        using (var session = new VaultSession(store, new InMemorySecurityStateStore(), clock))
        {
            // Como si libsodium no cargara (antivirus, AppLocker…): la re-envoltura con Argon2id falla.
            session.RewrapKeyring = (_, _, _) => throw LibsodiumFailure(failure);

            Assert.True(session.Unlock(Fake.MasterPassword).Succeeded);
            Assert.Equal(Fake.Secret, Assert.Single(session.Entries).Password.Reveal());
            Assert.Equal((byte)KdfAlgorithm.Pbkdf2Sha256, store.Envelope![5]);

            session.AddEntry(Fake.Entry("Nueva", "https://nueva.example", "u", Fake.Secret2)); // se sigue guardando
            Assert.Equal((byte)KdfAlgorithm.Pbkdf2Sha256, store.Envelope![5]);
        }

        // Con el comportamiento normal, el siguiente desbloqueo la convierte.
        using var restarted = new VaultSession(store, new InMemorySecurityStateStore(), clock);
        Assert.True(restarted.Unlock(Fake.MasterPassword).Succeeded);
        Assert.Equal((byte)KdfAlgorithm.Argon2id64MiB, store.Envelope![5]);
        Assert.Equal(2, restarted.Entries.Count);
    }

    [Fact]
    public void Fatal_error_during_the_upgrade_leaves_nothing_decrypted_and_the_vault_still_opens()
    {
        var clock = new FakeClock();
        var store = Fake.NewVault(clock);
        using var session = new VaultSession(store, new InMemorySecurityStateStore(), clock);
        var normal = session.RewrapKeyring;
        VaultKeyring? previous = null;
        session.RewrapKeyring = (current, _, _) =>
        {
            previous = current;
            throw new OutOfMemoryException();
        };

        Assert.Throws<OutOfMemoryException>(() => session.Unlock(Fake.MasterPassword));
        Assert.Equal(VaultState.Locked, session.State);
        Assert.True(Fake.DataKey(previous!).IsDisposed);

        session.RewrapKeyring = normal;
        Assert.True(session.Unlock(Fake.MasterPassword).Succeeded);
        Assert.Equal((byte)KdfAlgorithm.Argon2id64MiB, store.Envelope![5]);
    }

    [Fact]
    public void Weak_master_password_from_an_older_version_is_flagged()
    {
        const string weak = "caballoverde";
        var store = new InMemoryVaultStore();
        using (var keyring = VaultKeyring.Create(weak, Fake.FastIterations))
        using (var empty = new VaultDocument())
        using (var json = VaultDocumentSerializer.Serialize(empty))
        {
            store.Envelope = keyring.Seal(json.Span);
        }

        using var session = new VaultSession(store, new InMemorySecurityStateStore(), new FakeClock(), Fake.FastIterations);
        Assert.Null(session.MasterPasswordMeetsPolicy);
        Assert.True(session.Unlock(weak).Succeeded); // se sigue pudiendo abrir
        Assert.False(session.MasterPasswordMeetsPolicy);

        Assert.True(session.ChangeMasterPassword(weak, Fake.MasterPassword).Succeeded);
        Assert.True(session.MasterPasswordMeetsPolicy);
    }

    private static UnlockResult LockAndUnlock(VaultSession session, string password)
    {
        session.Lock(LockReason.Manual);
        return session.Unlock(password);
    }

    /// <summary>Excepciones con las que NSec informa de que no puede cargar libsodium (y la que las envuelve).</summary>
    private static Exception LibsodiumFailure(string kind) => kind switch
    {
        "PlatformNotSupported" => new PlatformNotSupportedException("No se pudo cargar libsodium (simulado).", new DllNotFoundException()),
        "TypeInitialization" => new TypeInitializationException("Interop.Libsodium", new DllNotFoundException()),
        "DllNotFound" => new DllNotFoundException("libsodium (simulado)"),
        _ => new CryptoUnavailableException(new DllNotFoundException()),
    };
}

/// <summary>Importación y restauración que no pierden datos, y borrado verificado del archivo de origen.</summary>
public class ImportSafetyTests
{
    private static readonly DateTimeOffset Earlier = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Recommended_option_never_overwrites_changes_made_in_passdesk()
    {
        var untouched = Imported("Intacta", "Vieja-1", modified: Earlier);    // importada y sin tocar
        var edited = Imported("Editada", "Mia-2", modified: Later);           // importada y editada después en PassDesk
        var manual = Fake.Entry("Manual", "https://manual.example", "u", "Mia-3"); // creada a mano
        var entries = new List<CredentialEntry> { untouched, edited, manual };

        const string csv =
            "Title,URL,Username,Password\r\n" +
            "Intacta,https://intacta.example,u,Nueva-1\r\n" +
            "Editada,https://editada.example,u,Nueva-2\r\n" +
            "Manual,https://manual.example,u,Nueva-3\r\n";
        using var parsed = ExportFileReader.ParseText(csv, "Prueba");
        var preview = ImportAnalyzer.Analyze(entries, parsed);
        Assert.Equal(3, preview.ConflictCount);

        var planned = ImportApplier.Simulate(entries, preview, ConflictResolution.UpdateUnlessChangedLocally);
        var outcome = ImportApplier.Apply(entries, preview, ConflictResolution.UpdateUnlessChangedLocally, Now, EntrySources.AppleExport);

        Assert.Equal(planned, outcome);
        Assert.Equal(1, outcome.Updated);
        Assert.Equal(2, outcome.Added);
        Assert.True(outcome.AllDataImported);
        Assert.Equal("Nueva-1", untouched.Password.Reveal());
        Assert.Equal("Mia-2", edited.Password.Reveal());   // el cambio local se conserva…
        Assert.Equal("Mia-3", manual.Password.Reveal());
        Assert.Contains(entries, e => e.Title == "Editada" && e.Password.Reveal() == "Nueva-2"); // …y la versión de Apple también
        DisposeAll(entries);
    }

    [Fact]
    public void Restoring_a_backup_only_updates_with_newer_versions()
    {
        var current = Imported("Banco Ficticio", "Clave-Reciente", modified: Later);
        var other = Imported("Correo Ficticio", "Clave-Correo-Vieja", modified: Earlier);
        var entries = new List<CredentialEntry> { current, other };

        var olderCopy = Imported("Banco Ficticio", "Clave-Antigua", modified: Earlier);
        var newerCopy = Imported("Correo Ficticio", "Clave-Correo-Nueva", modified: Later);
        using var parsed = ParseResult.FromEntries([olderCopy, newerCopy], "copia");
        var outcome = ImportApplier.Apply(entries, ImportAnalyzer.Analyze(entries, parsed), ConflictResolution.UpdateUnlessChangedLocally, Now, EntrySources.Backup);

        Assert.Equal("Clave-Reciente", current.Password.Reveal());     // la copia era más antigua: no se toca
        Assert.Equal("Clave-Correo-Nueva", other.Password.Reveal());   // la copia era más reciente: se actualiza
        Assert.Equal(1, outcome.Updated);
        Assert.Equal(0, outcome.Added);
        DisposeAll([.. entries, olderCopy, newerCopy]);
    }

    [Fact]
    public void Skipped_conflicts_or_invalid_rows_block_automatic_deletion()
    {
        var existing = Imported("Intacta", "Vieja-1", modified: Earlier);
        var entries = new List<CredentialEntry> { existing };
        const string csv = "Title,URL,Username,Password\r\nIntacta,https://intacta.example,u,Nueva-1\r\n";
        using var parsed = ExportFileReader.ParseText(csv, "Prueba");
        var preview = ImportAnalyzer.Analyze(entries, parsed);

        Assert.False(ImportApplier.Simulate(entries, preview, ConflictResolution.Skip).AllDataImported);
        Assert.True(ImportApplier.Simulate(entries, preview, ConflictResolution.UpdateUnlessChangedLocally).AllDataImported);

        using var withInvalid = ExportFileReader.ParseText("Title,URL,Username,Password\r\nSin clave,https://a.example,u,\r\n", "Prueba");
        Assert.False(ImportApplier.Simulate(entries, ImportAnalyzer.Analyze(entries, withInvalid), ConflictResolution.UpdateUnlessChangedLocally).AllDataImported);
        DisposeAll(entries);
    }

    [Fact]
    public void Deletion_verifies_that_the_file_is_the_one_that_was_read()
    {
        using var temp = new TempDirectory();
        string path = temp.File("Passwords.csv");
        File.WriteAllText(path, "Title,URL,Username,Password\r\nA,https://a.example,u,Ficticia-v1\r\n");
        FileFingerprint fingerprint;
        using (var parsed = ExportFileReader.ReadFile(path))
        {
            fingerprint = parsed.SourceFingerprint!;
        }

        Assert.Equal(new FileInfo(path).Length, fingerprint.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), fingerprint.Sha256);

        var result = SecureFileDeleter.Delete(path, fingerprint);
        Assert.True(result.Deleted);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("Title,URL,Username,Password\r\nA,https://a.example,u,Ficticia-v2\r\n")]                                        // mismo tamaño
    [InlineData("Title,URL,Username,Password\r\nA,https://a.example,u,Ficticia-v2\r\nB,https://b.example,u,Ficticia-v2\r\n")] // más filas
    public void Deletion_refuses_a_file_replaced_after_reading(string replacement)
    {
        using var temp = new TempDirectory();
        string path = temp.File("Passwords.csv");
        File.WriteAllText(path, "Title,URL,Username,Password\r\nA,https://a.example,u,Ficticia-v1\r\n");
        FileFingerprint fingerprint;
        using (var parsed = ExportFileReader.ReadFile(path))
        {
            fingerprint = parsed.SourceFingerprint!;
        }

        File.WriteAllText(path, replacement); // llega otra exportación con el mismo nombre
        var result = SecureFileDeleter.Delete(path, fingerprint);

        Assert.False(result.Deleted);
        Assert.Equal(SecureFileDeleter.ChangedFileError, result.Error);
        Assert.Equal(replacement, File.ReadAllText(path)); // intacto
    }

    [Fact]
    public void Recovery_points_are_encrypted_copies_that_can_be_restored_and_are_limited()
    {
        using var temp = new TempDirectory();
        var store = new DpapiVaultStore(temp.File("vault.pdv"));
        using var session = new VaultSession(store, new InMemorySecurityStateStore(), new FakeClock(), Fake.FastIterations);
        session.CreateVault(Fake.MasterPassword);
        session.AddEntry(Fake.Entry("Antes", "https://antes.example", "u", Fake.Secret));

        session.CreateRecoveryPoint("importacion");
        session.AddEntry(Fake.Entry("Despues", "https://despues.example", "u", "Otra-Ficticia"));

        string point = Assert.Single(Directory.GetFiles(store.RecoveryDirectory, "vault-*.pdv"));
        Assert.Contains("importacion", Path.GetFileName(point));
        Assert.False(Fake.ContainsText(File.ReadAllBytes(point), Fake.Secret));
        using (var restored = VaultSession.OpenBackupEnvelope(new DpapiVaultStore(point).ReadEnvelope(), Fake.MasterPassword))
        {
            Assert.Equal("Antes", Assert.Single(restored.Entries).Title); // estado anterior al cambio
        }

        // Se marca el primero como claramente más antiguo y se crean más de los que se conservan.
        var oldest = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetCreationTimeUtc(point, oldest);
        for (int i = 0; i < DpapiVaultStore.MaxRecoveryPoints + 2; i++)
        {
            session.CreateRecoveryPoint("importacion");
        }

        string[] kept = Directory.GetFiles(store.RecoveryDirectory, "vault-*.pdv");
        Assert.Equal(DpapiVaultStore.MaxRecoveryPoints, kept.Length);
        Assert.All(kept, f => Assert.True(File.GetCreationTimeUtc(f) > oldest)); // se elimina el más antiguo
    }

    private static CredentialEntry Imported(string title, string password, DateTimeOffset modified)
    {
        var entry = Fake.Entry(title, $"https://{title.ToLowerInvariant().Replace(' ', '-')}.example", "u", password);
        entry.CreatedUtc = Earlier;
        entry.ImportedUtc = Earlier;
        entry.ModifiedUtc = modified;
        return entry;
    }

    private static void DisposeAll(IEnumerable<CredentialEntry> entries)
    {
        foreach (var entry in entries)
        {
            entry.Dispose();
        }
    }
}

/// <summary>Si un guardado falla, la bóveda en memoria vuelve al estado guardado y se puede seguir guardando.</summary>
public class RollbackTests
{
    [Fact]
    public void Failed_save_rolls_back_the_new_entry_and_later_saves_work()
    {
        var store = new FlakyVaultStore();
        using var session = new VaultSession(store, new InMemorySecurityStateStore(), new FakeClock(), Fake.FastIterations);
        session.CreateVault(Fake.MasterPassword);
        session.AddEntry(Fake.Entry("Primera", "https://primera.example", "u", Fake.Secret));
        int dataChanged = 0;
        session.DataChanged += (_, _) => dataChanged++;

        store.FailNextWrites = 1;
        Assert.Throws<IOException>(() => session.AddEntry(Fake.Entry("Fallida", "https://fallida.example", "u", "x")));

        var only = Assert.Single(session.Entries);
        Assert.Equal("Primera", only.Title);
        Assert.Equal(Fake.Secret, only.Password.Reveal());
        Assert.Equal(1, dataChanged); // la interfaz se recarga

        session.AddEntry(Fake.Entry("Tercera", "https://tercera.example", "u", Fake.Secret2));
        session.Lock(LockReason.Manual);
        Assert.True(session.Unlock(Fake.MasterPassword).Succeeded);
        Assert.Equal(new[] { "Primera", "Tercera" }, session.Entries.Select(e => e.Title).ToArray());
    }

    [Fact]
    public void Failed_delete_keeps_the_entry_readable()
    {
        var store = new FlakyVaultStore();
        using var session = new VaultSession(store, new InMemorySecurityStateStore(), new FakeClock(), Fake.FastIterations);
        session.CreateVault(Fake.MasterPassword);
        session.AddEntry(Fake.Entry("Conservar", "https://conservar.example", "u", Fake.Secret));
        var id = session.Entries[0].Id;

        store.FailNextWrites = 1;
        Assert.Throws<IOException>(() => session.DeleteEntry(id));

        Assert.Equal(Fake.Secret, Assert.Single(session.Entries).Password.Reveal());
    }

    [Fact]
    public void Failed_document_add_does_not_block_later_saves()
    {
        using var temp = new TempDirectory();
        var store = new FlakyVaultStore();
        var clock = new FakeClock();
        using var session = new VaultSession(store, new InMemorySecurityStateStore(), clock, Fake.FastIterations);
        session.CreateVault(Fake.MasterPassword);
        var documentStore = new InMemoryDocumentStore();
        var documents = new SecureDocumentService(session, documentStore, clock);
        File.WriteAllText(temp.File("documento.txt"), "contenido ficticio");

        store.FailNextWrites = 1;
        var result = documents.AddFiles([temp.File("documento.txt")]);

        Assert.Equal(1, result.Failed);
        Assert.Empty(session.Documents);
        Assert.Empty(documentStore.Blobs);

        session.AddEntry(Fake.Entry("Despues", "https://despues.example", "u", Fake.Secret)); // antes fallaba siempre
        Assert.Equal(1, documents.AddFiles([temp.File("documento.txt")]).Added);
    }

    [Fact]
    public void Failed_import_imports_nothing()
    {
        var store = new FlakyVaultStore();
        using var session = new VaultSession(store, new InMemorySecurityStateStore(), new FakeClock(), Fake.FastIterations);
        session.CreateVault(Fake.MasterPassword);
        using var parsed = ExportFileReader.ParseText("Title,URL,Username,Password\r\nA,https://a.example,u,Ficticia-1\r\nB,https://b.example,u,Ficticia-2\r\n", "Prueba");

        store.FailNextWrites = 1;
        Assert.Throws<IOException>(() => session.Mutate(doc =>
            ImportApplier.Apply(doc.Entries, ImportAnalyzer.Analyze(doc.Entries, parsed), ConflictResolution.UpdateUnlessChangedLocally, DateTimeOffset.UtcNow, EntrySources.AppleExport)));

        Assert.Empty(session.Entries);
        Assert.Null(session.LastImport);
    }
}

/// <summary>Windows Hello: desactivado por defecto, caduca y no sobrevive a bloquear Windows ni a suspender.</summary>
public class QuickUnlockHardeningTests
{
    [Fact]
    public void Quick_unlock_is_off_by_default_in_settings()
    {
        Assert.False(new AppSettings().WindowsHelloQuickUnlock);
    }

    [Theory]
    [InlineData(LockReason.SessionLocked)]
    [InlineData(LockReason.Suspend)]
    public async Task Windows_lock_or_suspend_requires_the_master_password(LockReason reason)
    {
        using var session = Fake.NewSession(out _, out _);
        session.CreateVault(Fake.MasterPassword);
        session.Lock(reason);

        var verifier = new FakePresenceVerifier(PresenceResult.Verified);
        Assert.False(session.CanQuickUnlock);
        Assert.Equal(UnlockStatus.NotAvailable, (await session.QuickUnlockAsync(verifier, "Prueba")).Status);
        Assert.Equal(0, verifier.Calls);
    }

    [Theory]
    [InlineData(LockReason.SessionLocked)]
    [InlineData(LockReason.Suspend)]
    public void Windows_lock_after_an_inactivity_lock_also_forgets_the_key(LockReason reason)
    {
        using var session = Fake.NewSession(out _, out _);
        session.CreateVault(Fake.MasterPassword);
        session.Lock(LockReason.Inactivity);
        Assert.True(session.CanQuickUnlock);

        session.Lock(reason);
        Assert.False(session.CanQuickUnlock);
    }

    [Fact]
    public void Forgetting_the_key_on_windows_lock_works_even_without_locking_passdesk()
    {
        using var session = Fake.NewSession(out _, out _);
        session.CreateVault(Fake.MasterPassword);
        session.Lock(LockReason.Inactivity);

        session.ForgetQuickUnlockKey();
        Assert.False(session.CanQuickUnlock);
    }

    [Fact]
    public async Task Quick_unlock_expires_after_its_lifetime()
    {
        using var session = Fake.NewSession(out _, out var clock);
        session.CreateVault(Fake.MasterPassword);
        session.Lock(LockReason.Inactivity);

        clock.Advance(VaultSession.DefaultQuickUnlockLifetime - TimeSpan.FromMinutes(1));
        Assert.True(session.CanQuickUnlock);
        Assert.True((await session.QuickUnlockAsync(new FakePresenceVerifier(PresenceResult.Verified), "Prueba")).Succeeded);

        session.Lock(LockReason.Inactivity);
        clock.Advance(TimeSpan.FromMinutes(2));
        var verifier = new FakePresenceVerifier(PresenceResult.Verified);
        Assert.False(session.CanQuickUnlock);
        Assert.Equal(UnlockStatus.NotAvailable, (await session.QuickUnlockAsync(verifier, "Prueba")).Status);
        Assert.Equal(0, verifier.Calls);

        // La contraseña maestra renueva el plazo.
        Assert.True(session.Unlock(Fake.MasterPassword).Succeeded);
        session.Lock(LockReason.Inactivity);
        Assert.True(session.CanQuickUnlock);
    }

    [Fact]
    public void Expired_key_is_purged_by_the_periodic_check_even_if_the_clock_goes_back()
    {
        using var session = Fake.NewSession(out _, out var clock);
        var autoLock = new AutoLockController(session, new InactivityMonitor(clock, TimeSpan.FromMinutes(5)));
        session.CreateVault(Fake.MasterPassword);
        session.Lock(LockReason.Inactivity);

        clock.Advance(VaultSession.DefaultQuickUnlockLifetime + TimeSpan.FromMinutes(1));
        autoLock.Tick();
        clock.Advance(-VaultSession.DefaultQuickUnlockLifetime);

        Assert.False(session.CanQuickUnlock);
    }
}

/// <summary>
/// Restos en memoria: la detección de exportaciones solo decodifica la cabecera, y las contraseñas, notas y semillas
/// se guardan en Base64 (contenido versión 2) sin pasar por los búferes compartidos de .NET.
/// </summary>
public class MemoryResidueTests
{
    private const string RowMarker = "MARCADOR-FILA-FICTICIA-4471";

    // «ñ» al principio: como texto JSON, Utf8JsonWriter escaparía el valor entero (hasta 6 bytes por byte) en un búfer
    // de ArrayPool. Más de 192 bytes para que también la conversión Base64 superase el umbral de la pila.
    private static readonly string PoolMarker = "ñ+&MARCADOR-POOL-FICTICIO-" + string.Concat(Enumerable.Repeat("0123456789abcdef", 16)) + "-ñandú";
    private static readonly string ControlMarker = PoolMarker.Replace("MARCADOR-POOL", "CONTROL-POOL", StringComparison.Ordinal);

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-8-bom")]
    [InlineData("utf-16le")]
    [InlineData("utf-16be")]
    public void Export_probe_decodes_only_the_header_line(string kind)
    {
        // U+0D0A contiene los bytes 0x0D y 0x0A en UTF-16: el salto de línea debe buscarse por caracteres completos.
        const string header = "Title,URL,Username,Password,Notes,ഊ";
        string rows = string.Concat(Enumerable.Repeat($"Ejemplo,https://ejemplo.example,usuario,{RowMarker},,\r\n", 80));
        Encoding encoding = kind.StartsWith("utf-16", StringComparison.Ordinal)
            ? new UnicodeEncoding(bigEndian: kind.EndsWith("be", StringComparison.Ordinal), byteOrderMark: true)
            : new UTF8Encoding(encoderShouldEmitUTF8Identifier: kind.EndsWith("bom", StringComparison.Ordinal));
        byte[] file = [.. encoding.GetPreamble(), .. encoding.GetBytes(header + "\r\n" + rows)];
        byte[] probe = file[..4096]; // lo que lee la detección

        ExportFileProbe.DecodeHeaderLine(probe); // calentamiento (JIT)
        long before = GC.GetAllocatedBytesForCurrentThread();
        string decoded = ExportFileProbe.DecodeHeaderLine(probe);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(header, decoded);
        Assert.DoesNotContain(RowMarker, decoded);
        Assert.True(allocated < 512, $"Se reservaron {allocated} bytes: se decodificó más que la cabecera.");
    }

    [Fact]
    public void Saving_secrets_that_need_json_escaping_leaves_no_copy_in_the_array_pool()
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(PoolMarker);
        using (var document = new VaultDocument())
        {
            document.Entries.Add(Fake.Entry("Pool", "https://pool.example", "u", PoolMarker, notes: PoolMarker));
            using var json = VaultDocumentSerializer.Serialize(document);
            using var reread = VaultDocumentSerializer.Deserialize(json.Span);
            Assert.Equal(PoolMarker, Assert.Single(reread.Entries).Password.Reveal());
        }

        // Tamaños que pedirían el escape de Utf8JsonWriter y la conversión Base64 (en los dos sentidos).
        byte[] base64 = Encoding.ASCII.GetBytes(Convert.ToBase64String(utf8));
        foreach (int size in new[] { 6 * utf8.Length, base64.Length })
        {
            byte[] pooled = RentedCopy(size);
            Assert.False(Fake.ContainsBytes(pooled, "MARCADOR-POOL-FICTICIO"u8.ToArray()));
            Assert.False(Fake.ContainsBytes(pooled, base64[..64]));
        }
    }

    [Fact]
    public void Control_json_text_escaping_does_leave_a_copy_in_the_array_pool()
    {
        // Demuestra que la prueba anterior detecta el problema: es lo que hacía la versión 1 con cada secreto.
        byte[] utf8 = Encoding.UTF8.GetBytes(ControlMarker);
        using (var output = new SecureBufferWriter())
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteString("pw", utf8);
            writer.WriteEndObject();
        }

        Assert.True(Fake.ContainsBytes(RentedCopy(6 * utf8.Length), "CONTROL-POOL-FICTICIO"u8.ToArray()));
    }

    [Fact]
    public void Version_1_content_is_read_and_saved_again_as_version_2_with_base64_secrets()
    {
        // Contenido tal como lo guardaba la versión 1: secretos como texto JSON escapado y la clave del documento en Base64.
        string key = Convert.ToBase64String([.. Enumerable.Range(1, 32).Select(i => (byte)i)]);
        string v1 = $$"""
            {"v":1,"entries":[{"id":"0f8fad5b-d9cb-469f-a165-70867728950e","title":"Antigua","url":"https://antigua.example","user":"u",
            "pw":"ñandú+&-Ficticia","notes":"Nota\nficticia","otp":"otpauth://totp/Prueba?secret=JBSWY3DPEHPK3PXP",
            "cat":"Other","catManual":false,"created":"2026-01-15T10:00:00+00:00","modified":"2026-01-15T10:00:00+00:00","src":"manual"}],
            "docs":[{"id":"7c9e6679-7425-40de-944b-e07fc1f90ae7","title":"Doc","file":"doc.txt","size":3,"sha256":"AB","key":"{{key}}",
            "created":"2026-01-15T10:00:00+00:00","modified":"2026-01-15T10:00:00+00:00"}]}
            """;

        using var original = VaultDocumentSerializer.Deserialize(Fake.Utf8(v1));
        var entry = Assert.Single(original.Entries);
        Assert.Equal("ñandú+&-Ficticia", entry.Password.Reveal());
        Assert.Equal("Nota\nficticia", entry.Notes.Reveal());

        using var saved = VaultDocumentSerializer.Serialize(original);
        string json = Encoding.UTF8.GetString(saved.Span);
        Assert.StartsWith("{\"v\":2,", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Ficticia", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("otpauth", json, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(Fake.Utf8("ñandú+&-Ficticia")), json, StringComparison.Ordinal);

        using var reread = VaultDocumentSerializer.Deserialize(saved.Span);
        var copy = Assert.Single(reread.Entries);
        Assert.True(entry.Password.ContentEquals(copy.Password));
        Assert.True(entry.Notes.ContentEquals(copy.Notes));
        Assert.True(entry.OtpAuth.ContentEquals(copy.OtpAuth));
        Assert.True(Assert.Single(original.Documents).Key.ContentEquals(Assert.Single(reread.Documents).Key));
    }

    /// <summary>
    /// ArrayPool guarda en cada hilo el último array devuelto de cada tamaño: alquilar uno del mismo tamaño en el mismo
    /// hilo devuelve justo el que usó el código anterior. Se devuelve borrado.
    /// </summary>
    private static byte[] RentedCopy(int size)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(size);
        byte[] copy = (byte[])rented.Clone();
        ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        return copy;
    }
}

/// <summary>
/// Tras cambiar la contraseña maestra, la versión anterior (.bak) ya no se abre con la contraseña antigua; los puntos de
/// recuperación anteriores sí, y se pueden borrar.
/// </summary>
public class PasswordChangeTests
{
    [Fact]
    public void Previous_version_no_longer_opens_with_the_old_password()
    {
        using var temp = new TempDirectory();
        var store = new DpapiVaultStore(temp.File("vault.pdv"));
        using var session = new VaultSession(store, new InMemorySecurityStateStore(), new FakeClock(), Fake.FastIterations);
        session.CreateVault(Fake.MasterPassword);
        session.AddEntry(Fake.Entry("Cuenta", "https://cuenta.example", "u", Fake.Secret));
        string previous = store.FilePath + ".bak";
        OpenFile(previous, Fake.MasterPassword).Dispose(); // antes del cambio, el .bak existe y abre con la contraseña

        Assert.True(session.ChangeMasterPassword(Fake.MasterPassword, Fake.OtherMasterPassword).Succeeded);

        Assert.Throws<InvalidMasterPasswordException>(() => OpenFile(previous, Fake.MasterPassword));
        using var reopened = OpenFile(previous, Fake.OtherMasterPassword);
        Assert.Equal(Fake.Secret, Assert.Single(reopened.Entries).Password.Reveal());
    }

    [Fact]
    public void Failing_to_replace_the_previous_version_does_not_undo_the_change()
    {
        var store = new FlakyVaultStore();
        using var session = new VaultSession(store, new InMemorySecurityStateStore(), new FakeClock(), Fake.FastIterations);
        session.CreateVault(Fake.MasterPassword);
        store.WritesBeforeFailures = 1; // el primer guardado funciona y el segundo falla
        store.FailNextWrites = 1;

        Assert.True(session.ChangeMasterPassword(Fake.MasterPassword, Fake.OtherMasterPassword).Succeeded);

        session.Lock(LockReason.Manual);
        Assert.Equal(UnlockStatus.WrongPassword, session.Unlock(Fake.MasterPassword).Status);
        Assert.True(session.Unlock(Fake.OtherMasterPassword).Succeeded);
    }

    [Fact]
    public void Recovery_points_from_before_the_change_open_only_with_the_old_password_and_can_be_deleted()
    {
        using var temp = new TempDirectory();
        var store = new DpapiVaultStore(temp.File("vault.pdv"));
        using var session = new VaultSession(store, new InMemorySecurityStateStore(), new FakeClock(), Fake.FastIterations);
        session.CreateVault(Fake.MasterPassword);
        Assert.Empty(store.ListRecoveryPoints()); // todavía no existe la carpeta
        session.AddEntry(Fake.Entry("Cuenta", "https://cuenta.example", "u", Fake.Secret));
        session.CreateRecoveryPoint("importacion");
        session.CreateRecoveryPoint("restauracion");
        session.CreateRecoveryPoint("antes de Argon2id"); // la copia especial previa a Argon2id

        Assert.True(session.ChangeMasterPassword(Fake.MasterPassword, Fake.OtherMasterPassword).Succeeded);
        string[] before = store.ListRecoveryPoints();
        session.CreateRecoveryPoint("importacion"); // posterior al cambio
        string after = Assert.Single(store.ListRecoveryPoints().Except(before));

        Assert.Equal(3, before.Length);
        foreach (string point in before)
        {
            OpenFile(point, Fake.MasterPassword).Dispose();
            Assert.Throws<InvalidMasterPasswordException>(() => OpenFile(point, Fake.OtherMasterPassword));
        }

        OpenFile(after, Fake.OtherMasterPassword).Dispose();
        string special = Assert.Single(before, DpapiVaultStore.IsPreArgon2idPoint);

        Assert.Empty(store.DeleteRecoveryPoints(before.Where(p => p != special)));
        Assert.Equal(new[] { special, after }.Order(), store.ListRecoveryPoints().Order());
        Assert.Empty(store.DeleteRecoveryPoints([special]));
        Assert.Equal(after, Assert.Single(store.ListRecoveryPoints()));

        session.Lock(LockReason.Manual);
        Assert.True(session.Unlock(Fake.OtherMasterPassword).Succeeded); // la bóveda no se toca
    }

    [Theory]
    [InlineData("vault-20261016-185240-421-antesdeargon2id.pdv", true)]
    [InlineData("vault-20261016-185240-421-antesdeArgon2id-2.pdv", true)]
    [InlineData("vault-20261016-185240-421-importacion.pdv", false)]
    public void The_copy_from_before_argon2id_is_recognized(string name, bool expected) =>
        Assert.Equal(expected, DpapiVaultStore.IsPreArgon2idPoint(Path.Combine(@"C:\datos\recuperacion", name)));

    [Fact]
    public void Deleting_recovery_points_refuses_any_other_file_and_then_deletes_nothing()
    {
        using var temp = new TempDirectory();
        var store = new DpapiVaultStore(temp.File("vault.pdv"));
        using var session = new VaultSession(store, new InMemorySecurityStateStore(), new FakeClock(), Fake.FastIterations);
        session.CreateVault(Fake.MasterPassword);
        session.CreateRecoveryPoint("importacion");
        string point = Assert.Single(store.ListRecoveryPoints());
        string outside = temp.File(Path.GetFileName(point)); // mismo nombre, fuera de «recuperacion»
        File.Copy(point, outside);

        foreach (string other in new[] { store.FilePath, store.FilePath + ".bak", outside, Path.Combine(store.RecoveryDirectory, "..", "vault.pdv") })
        {
            Assert.Throws<ArgumentException>(() => store.DeleteRecoveryPoints([point, other]));
        }

        Assert.True(File.Exists(point));
        Assert.True(File.Exists(outside));
        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public void Recovery_points_that_cannot_be_deleted_are_reported_and_the_rest_are_deleted()
    {
        using var temp = new TempDirectory();
        var store = new DpapiVaultStore(temp.File("vault.pdv"));
        using var session = new VaultSession(store, new InMemorySecurityStateStore(), new FakeClock(), Fake.FastIterations);
        session.CreateVault(Fake.MasterPassword);
        session.CreateRecoveryPoint("importacion");
        session.CreateRecoveryPoint("restauracion");
        string[] points = store.ListRecoveryPoints();

        IReadOnlyList<string> failed;
        using (new FileStream(points[0], FileMode.Open, FileAccess.Read, FileShare.None)) // abierto por otro programa
        {
            failed = store.DeleteRecoveryPoints(points);
        }

        Assert.Equal(points[0], Assert.Single(failed));
        Assert.Equal(points[0], Assert.Single(store.ListRecoveryPoints()));
    }

    private static VaultDocument OpenFile(string path, string password) =>
        VaultSession.OpenBackupEnvelope(new DpapiVaultStore(path).ReadEnvelope(), password);
}

/// <summary>Contador de intentos fallidos: cifrado con DPAPI y, si está dañado, espera máxima en vez de volver a cero.</summary>
public class ThrottleStateTests
{
    [Fact]
    public void State_file_is_protected_with_dpapi_and_a_missing_file_is_the_initial_state()
    {
        using var temp = new TempDirectory();
        var store = new FileSecurityStateStore(temp.File("security.json"));
        Assert.Equal(UnlockThrottleState.None, store.Load());

        var state = new UnlockThrottleState(5, new DateTimeOffset(2026, 10, 15, 9, 0, 15, TimeSpan.Zero));
        store.Save(state);

        Assert.False(Fake.ContainsText(File.ReadAllBytes(store.FilePath), "FailedAttempts"));
        Assert.Equal(state, store.Load());
    }

    [Theory]
    [InlineData("json-roto")]
    [InlineData("vacio")]
    [InlineData("texto")]
    [InlineData("dpapi-truncado")]
    public void Damaged_state_file_applies_the_maximum_wait_instead_of_resetting(string damage)
    {
        using var temp = new TempDirectory();
        var security = new FileSecurityStateStore(temp.File("security.json"));
        var clock = new FakeClock();
        var store = Fake.NewVault(clock);
        security.Save(new UnlockThrottleState(2, null));
        byte[] valid = File.ReadAllBytes(security.FilePath);
        File.WriteAllBytes(security.FilePath, damage switch
        {
            "json-roto" => Fake.Utf8("{ roto"),
            "vacio" => [],
            "texto" => Fake.Utf8("contenido que no es de DPAPI"),
            _ => valid[..^10],
        });
        Assert.Throws<InvalidDataException>(() => security.Load());

        using (var session = new VaultSession(store, security, clock, Fake.FastIterations))
        {
            var result = session.Unlock(Fake.MasterPassword);
            Assert.Equal(UnlockStatus.Throttled, result.Status);
            Assert.Equal(UnlockThrottle.MaxDelay, result.RetryAfter);
        }

        // Reiniciar no acorta la espera; al cumplirse, la contraseña correcta abre y el archivo vuelve a ser válido.
        clock.Advance(UnlockThrottle.MaxDelay - TimeSpan.FromSeconds(1));
        using var restarted = new VaultSession(store, security, clock, Fake.FastIterations);
        Assert.Equal(UnlockStatus.Throttled, restarted.Unlock(Fake.MasterPassword).Status);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(restarted.Unlock(Fake.MasterPassword).Succeeded);
        Assert.Equal(UnlockThrottleState.None, security.Load());
    }

    [Fact]
    public void Impossible_saved_values_are_treated_as_damage()
    {
        var clock = new FakeClock();
        var store = Fake.NewVault(clock);
        foreach (var impossible in new[] { new UnlockThrottleState(-3, null), new UnlockThrottleState(0, clock.UtcNow.AddDays(30)) })
        {
            var security = new InMemorySecurityStateStore { State = impossible };
            using var session = new VaultSession(store, security, clock, Fake.FastIterations);

            Assert.Equal(UnlockThrottle.MaxDelay, session.Unlock(Fake.MasterPassword).RetryAfter);
            Assert.Equal((DateTimeOffset?)(clock.UtcNow + UnlockThrottle.MaxDelay), security.State.LockedUntilUtc);
        }
    }

    [Fact]
    public void Failure_counter_does_not_wrap_around()
    {
        var clock = new FakeClock();
        var security = new InMemorySecurityStateStore { State = new UnlockThrottleState(int.MaxValue, null) };
        using var session = new VaultSession(Fake.NewVault(clock), security, clock, Fake.FastIterations);

        Assert.Equal(UnlockThrottle.MaxDelay, session.Unlock(Fake.OtherMasterPassword).RetryAfter);
        Assert.Equal(int.MaxValue, security.State.FailedAttempts);
    }

    [Fact]
    public void Plain_json_from_older_versions_is_read_and_saved_again_protected()
    {
        using var temp = new TempDirectory();
        var security = new FileSecurityStateStore(temp.File("security.json"));
        File.WriteAllText(security.FilePath, "{\"FailedAttempts\":4,\"LockedUntilUtc\":\"2026-10-15T09:00:05+00:00\"}");
        Assert.Equal(new UnlockThrottleState(4, new DateTimeOffset(2026, 10, 15, 9, 0, 5, TimeSpan.Zero)), security.Load());

        var clock = new FakeClock();
        using var session = new VaultSession(Fake.NewVault(clock), security, clock, Fake.FastIterations);
        Assert.Equal(UnlockStatus.Throttled, session.Unlock(Fake.MasterPassword).Status); // se respeta la espera guardada
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(session.Unlock(Fake.MasterPassword).Succeeded);
        Assert.False(Fake.ContainsText(File.ReadAllBytes(security.FilePath), "FailedAttempts"));
    }

    [Fact]
    public void A_vault_unreadable_on_this_pc_is_reported_before_any_wait()
    {
        // Como al copiar la carpeta de datos a otro PC: ni la bóveda ni el contador se pueden descifrar con DPAPI.
        using var temp = new TempDirectory();
        byte[] foreign = RandomNumberGenerator.GetBytes(64);
        File.WriteAllBytes(temp.File("vault.pdv"), [.. "PDLV"u8, 1, 1, .. foreign]);
        File.WriteAllBytes(temp.File("security.json"), foreign);
        using var session = new VaultSession(
            new DpapiVaultStore(temp.File("vault.pdv")), new FileSecurityStateStore(temp.File("security.json")), new FakeClock(), Fake.FastIterations);

        Assert.Equal(UnlockStatus.VaultUnreadable, session.Unlock(Fake.MasterPassword).Status);
    }

    [Fact]
    public void Creating_a_new_vault_resets_the_counter()
    {
        // Por ejemplo, tras archivar una bóveda ilegible: el contador de la anterior ya no protege nada.
        var security = new InMemorySecurityStateStore { State = new UnlockThrottleState(-1, null) }; // imposible: espera máxima
        using var session = new VaultSession(new InMemoryVaultStore(), security, new FakeClock(), Fake.FastIterations);

        Assert.True(session.CreateVault(Fake.MasterPassword).Succeeded);
        Assert.Equal(UnlockThrottleState.None, security.State);

        session.Lock(LockReason.Manual);
        var typo = session.Unlock(Fake.OtherMasterPassword);
        Assert.Equal(UnlockStatus.WrongPassword, typo.Status);
        Assert.Equal(TimeSpan.Zero, typo.RetryAfter);
        Assert.True(session.Unlock(Fake.MasterPassword).Succeeded);
    }

    [Fact]
    public void Countdown_only_says_wrong_password_when_it_was_checked()
    {
        var wait = TimeSpan.FromSeconds(300);
        Assert.Equal("Contraseña incorrecta. Demasiados intentos: espera 300 s.", LockView.RetryCountdownText(UnlockStatus.WrongPassword, wait));

        // Con una espera ya guardada (al reiniciar, o con el contador dañado) la contraseña ni se comprueba.
        Assert.Equal("Demasiados intentos: espera 300 s.", LockView.RetryCountdownText(UnlockStatus.Throttled, wait));
    }
}
