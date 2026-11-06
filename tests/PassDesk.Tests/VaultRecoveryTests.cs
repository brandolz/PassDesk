using PassDesk.Auth;
using PassDesk.Core.Model;
using PassDesk.Storage;

namespace PassDesk.Tests;

/// <summary>
/// Integridad de la bóveda en disco: copias que deja un guardado (<c>vault.pdv.tmp</c>, <c>vault.pdv.bak</c>), guardados que
/// fallan a mitad y recuperación. Bóvedas ficticias reales (DPAPI + PBKDF2 rápido) en directorios temporales; los fallos de
/// <c>ReplaceFile</c> se simulan solo en el flujo de cada prueba.
/// </summary>
public class VaultRecoveryTests
{
    /// <summary>ERROR_UNABLE_TO_MOVE_REPLACEMENT_2 (1177) como HRESULT.</summary>
    private const int ErrorUnableToMoveReplacement2 = unchecked((int)0x80070481);

    // ---- Estado normal ----

    [Fact]
    public void A_valid_main_vault_unlocks_as_always_and_nothing_is_pending()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno", "Dos");
        Assert.True(File.Exists(store.BackupPath)); // tras varios guardados siempre hay .bak

        using var session = NewSession(store);
        Assert.Equal(VaultState.Locked, session.State);
        Assert.False(session.RecoveryPending);
        Assert.True(session.Unlock(Fake.MasterPassword).Succeeded);
        Assert.Equal(["Uno", "Dos"], Titles(session));
        Assert.Empty(Preserved(store));
    }

    // ---- Falta la bóveda principal ----

    [Fact]
    public void Missing_main_vault_with_a_backup_offers_recovery_and_never_a_new_vault()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno", "Dos");
        File.Delete(store.FilePath);
        byte[] bak = Read(store.BackupPath);

        using var session = NewSession(store);
        Assert.Equal(VaultState.Locked, session.State);
        Assert.True(session.RecoveryPending);
        Assert.Equal(UnlockStatus.AlreadyExists, session.CreateVault(Fake.OtherMasterPassword).Status);
        Assert.Equal(UnlockStatus.RecoveryRequired, session.Unlock(Fake.MasterPassword).Status);

        Assert.False(store.Exists);
        Assert.Equal(bak, Read(store.BackupPath));
        Assert.Empty(Preserved(store));
    }

    [Fact]
    public void Recovery_with_the_right_password_restores_the_backup_and_keeps_a_verified_copy_of_it()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno", "Dos"); // .bak = versión anterior al último guardado (solo «Uno»)
        File.Delete(store.FilePath);
        byte[] bak = Read(store.BackupPath);

        string kept;
        using (var session = NewSession(store))
        {
            Assert.True(session.Recover(Fake.MasterPassword).Succeeded);
            Assert.Equal(VaultState.Unlocked, session.State);
            Assert.Equal(["Uno"], Titles(session));
            Assert.True(store.Exists);
            kept = Assert.Single(Preserved(store));
            Assert.EndsWith("-bak.pdv", kept);
            Assert.Equal(bak, Read(kept));
            Assert.Equal(bak, Read(store.BackupPath)); // recuperar no toca el .bak

            session.AddEntry(Entry("Tres"));
        }

        using var restarted = NewSession(store);
        Assert.False(restarted.RecoveryPending);
        Assert.True(restarted.Unlock(Fake.MasterPassword).Succeeded);
        Assert.Equal(["Uno", "Tres"], Titles(restarted));
        Assert.Equal(bak, Read(kept)); // la copia conservada no cambia con los guardados posteriores
    }

    [Fact]
    public void Missing_main_vault_with_only_the_temp_file_is_recovered_from_it()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno", "Dos");
        File.Move(store.FilePath, store.TempPath); // el contenido nuevo quedó en .tmp
        File.Delete(store.BackupPath);
        byte[] tmp = Read(store.TempPath);

        using var session = NewSession(store);
        Assert.True(session.RecoveryPending);
        Assert.Equal(UnlockStatus.AlreadyExists, session.CreateVault(Fake.OtherMasterPassword).Status);
        Assert.Equal(tmp, Read(store.TempPath));

        Assert.True(session.Recover(Fake.MasterPassword).Succeeded);
        Assert.Equal(["Uno", "Dos"], Titles(session));
        Assert.Equal(tmp, Read(Assert.Single(Preserved(store), p => p.EndsWith("-tmp.pdv", StringComparison.Ordinal))));
        Assert.False(File.Exists(store.TempPath)); // pasó a ser la bóveda principal
    }

    [Fact]
    public void With_both_copies_the_newest_wins_and_both_are_kept()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno", "Dos", "Tres"); // .bak = Uno y Dos
        File.Move(store.FilePath, store.TempPath);
        byte[] tmp = Read(store.TempPath);
        byte[] bak = Read(store.BackupPath);

        using var session = NewSession(store);
        Assert.True(session.Recover(Fake.MasterPassword).Succeeded);
        Assert.Equal(["Uno", "Dos", "Tres"], Titles(session));

        string[] kept = Preserved(store);
        Assert.Equal(tmp, Read(Assert.Single(kept, p => p.EndsWith("-tmp.pdv", StringComparison.Ordinal))));
        Assert.Equal(bak, Read(Assert.Single(kept, p => p.EndsWith("-bak.pdv", StringComparison.Ordinal))));
        Assert.Equal(bak, Read(store.BackupPath));
    }

    [Fact]
    public void A_half_written_temp_file_falls_back_to_the_backup()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno", "Dos", "Tres");
        File.Move(store.FilePath, store.TempPath);
        byte[] whole = Read(store.TempPath);
        File.WriteAllBytes(store.TempPath, whole[..(whole.Length / 2)]); // corte mientras se escribía el .tmp
        byte[] halfTmp = Read(store.TempPath);

        using var session = NewSession(store);
        Assert.True(session.Recover(Fake.MasterPassword).Succeeded);
        Assert.Equal(["Uno", "Dos"], Titles(session));
        Assert.Equal(halfTmp, Read(Assert.Single(Preserved(store), p => p.EndsWith("-tmp.pdv", StringComparison.Ordinal))));
    }

    [Fact]
    public void Empty_leftover_files_are_not_mistaken_for_a_vault()
    {
        using var temp = new TempDirectory();
        var store = new DpapiVaultStore(temp.File("vault.pdv"));
        File.WriteAllBytes(store.TempPath, []); // corte justo al crear el .tmp: no hay datos que conservar

        using var session = NewSession(store);
        Assert.Equal(VaultState.NotCreated, session.State);
        Assert.True(session.CreateVault(Fake.MasterPassword).Succeeded);
    }

    // ---- Bóveda principal dañada ----

    [Fact]
    public void Corrupted_main_vault_with_a_valid_backup_is_recovered_and_the_damaged_file_is_kept()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno", "Dos");
        Corrupt(store); // .bak = Uno y Dos (válida)
        byte[] damaged = Read(store.FilePath);

        using var session = NewSession(store);
        Assert.False(session.RecoveryPending);
        Assert.Equal(UnlockStatus.VaultCorrupted, session.Unlock(Fake.MasterPassword).Status);
        Assert.True(session.CanRecover);

        Assert.True(session.Recover(Fake.MasterPassword).Succeeded);
        Assert.Equal(["Uno", "Dos"], Titles(session));
        Assert.Equal(damaged, Read(Assert.Single(Preserved(store), p => p.EndsWith("-principal.pdv", StringComparison.Ordinal))));
    }

    // ---- La principal manda ----

    [Fact]
    public void Recovering_while_the_main_vault_is_fine_just_unlocks_and_replaces_nothing()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno", "Dos");
        byte[] main = Read(store.FilePath);
        byte[] bak = Read(store.BackupPath);

        using var session = NewSession(store);
        Assert.True(session.CanRecover); // hay .bak, como siempre tras guardar
        Assert.True(session.Recover(Fake.MasterPassword).Succeeded);
        Assert.Equal(["Uno", "Dos"], Titles(session)); // no la versión anterior del .bak
        Assert.Equal(main, Read(store.FilePath));
        Assert.Equal(bak, Read(store.BackupPath));
        Assert.Empty(Preserved(store));
    }

    [Fact]
    public void A_main_vault_that_cannot_be_read_right_now_is_never_replaced()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno", "Dos");
        byte[] main = Read(store.FilePath);

        using var session = NewSession(store);
        using (new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.None)) // otro programa la tiene abierta
        {
            Assert.Equal(UnlockStatus.VaultUnreadable, session.Unlock(Fake.MasterPassword).Status);
            Assert.Equal(UnlockStatus.VaultUnreadable, session.Recover(Fake.MasterPassword).Status);
        }

        Assert.Equal(main, Read(store.FilePath));
        Assert.Empty(Preserved(store));
        Assert.True(session.Recover(Fake.MasterPassword).Succeeded); // ya libre: se abre la principal tal cual
        Assert.Equal(["Uno", "Dos"], Titles(session));
        Assert.Equal(main, Read(store.FilePath));
    }

    [Fact]
    public void A_wrong_password_for_a_readable_main_vault_never_falls_back_to_the_copies()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno", "Dos");
        using (var otherTemp = new TempDirectory())
        {
            // Un .bak que se abre con otra contraseña (por ejemplo, de antes de cambiarla).
            var other = new DpapiVaultStore(otherTemp.File("vault.pdv"));
            using (var session = NewSession(other))
            {
                Assert.True(session.CreateVault(Fake.OtherMasterPassword).Succeeded);
            }

            File.Copy(other.FilePath, store.BackupPath, overwrite: true);
        }

        byte[] main = Read(store.FilePath);
        byte[] bak = Read(store.BackupPath);
        var security = new InMemorySecurityStateStore();
        using var recovering = NewSession(store, security);
        Assert.Equal(UnlockStatus.WrongPassword, recovering.Recover(Fake.OtherMasterPassword).Status);
        Assert.Equal(1, security.State.FailedAttempts);
        Assert.Equal(main, Read(store.FilePath));
        Assert.Equal(bak, Read(store.BackupPath));
        Assert.Empty(Preserved(store));
    }

    // ---- Guardados que fallan a mitad ----

    [Fact]
    public void Replace_failing_with_error_1177_after_renaming_the_original_still_completes_the_save()
    {
        using var temp = new TempDirectory();
        string path = temp.File("datos.bin");
        File.WriteAllBytes(path, "v1"u8.ToArray());
        File.WriteAllBytes(path + ".bak", "v0"u8.ToArray());

        using (FaultyReplace(ReplaceFailsAfterRenaming))
        {
            AtomicFile.Write(path, "v2"u8, keepPrevious: true);
        }

        Assert.Equal("v2"u8.ToArray(), Read(path));
        Assert.Equal("v1"u8.ToArray(), Read(path + ".bak")); // la versión anterior, intacta
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Replace_that_changes_nothing_still_uses_the_equivalent_fallback()
    {
        using var temp = new TempDirectory();
        string path = temp.File("datos.bin");
        File.WriteAllBytes(path, "v1"u8.ToArray());

        using (FaultyReplace((_, _, _) => throw new IOException("Simulación: ReplaceFile no disponible.")))
        {
            AtomicFile.Write(path, "v2"u8, keepPrevious: true);
        }

        Assert.Equal("v2"u8.ToArray(), Read(path));
        Assert.Equal("v1"u8.ToArray(), Read(path + ".bak"));
    }

    [Fact]
    public void A_save_hit_by_error_1177_is_kept_and_the_vault_opens_after_restarting()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno");
        using (var session = NewSession(store))
        {
            Assert.True(session.Unlock(Fake.MasterPassword).Succeeded);
            using (FaultyReplace(ReplaceFailsAfterRenaming))
            {
                session.AddEntry(Entry("Dos"));
            }
        }

        using var restarted = NewSession(store);
        Assert.False(restarted.RecoveryPending);
        Assert.True(restarted.Unlock(Fake.MasterPassword).Succeeded);
        Assert.Equal(["Uno", "Dos"], Titles(restarted));
    }

    [Fact]
    public void A_crash_after_renaming_the_original_leaves_a_recoverable_vault_and_blocks_further_saves()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno");
        using (var session = NewSession(store))
        {
            Assert.True(session.Unlock(Fake.MasterPassword).Succeeded);
            using (FaultyReplace((_, full, backup) =>
                   {
                       File.Move(full, backup!, overwrite: true);
                       throw new SimulatedCrash();
                   }))
            {
                Assert.Throws<SimulatedCrash>(() => session.AddEntry(Entry("Dos")));
            }

            // En disco: sin principal, .bak = «Uno» y .tmp = «Uno» y «Dos».
            Assert.False(store.Exists);
            byte[] tmp = Read(store.TempPath);
            byte[] bak = Read(store.BackupPath);

            // Los guardados siguientes se rechazan sin tocar las copias.
            Assert.Throws<VaultRecoveryPendingException>(() => session.AddEntry(Entry("Tres")));
            Assert.Equal(tmp, Read(store.TempPath));
            Assert.Equal(bak, Read(store.BackupPath));
        }

        using var restarted = NewSession(store);
        Assert.True(restarted.RecoveryPending);
        Assert.True(restarted.Recover(Fake.MasterPassword).Succeeded);
        Assert.Equal(["Uno", "Dos"], Titles(restarted)); // la copia más reciente
    }

    // ---- Contraseña incorrecta ----

    [Fact]
    public void Recovery_with_a_wrong_password_changes_nothing_and_counts_as_a_failed_attempt()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno", "Dos");
        File.Delete(store.FilePath);
        byte[] bak = Read(store.BackupPath);
        var security = new InMemorySecurityStateStore();

        using var session = NewSession(store, security);
        Assert.Equal(UnlockStatus.WrongPassword, session.Recover(Fake.OtherMasterPassword).Status);
        Assert.Equal(1, security.State.FailedAttempts);
        Assert.Equal(VaultState.Locked, session.State);
        Assert.True(session.RecoveryPending);
        Assert.False(store.Exists);
        Assert.Equal(bak, Read(store.BackupPath));
        Assert.Empty(Preserved(store));

        Assert.True(session.Recover(Fake.MasterPassword).Succeeded);
        Assert.Equal(0, security.State.FailedAttempts);
    }

    // ---- Nunca se sobrescribe la única copia ----

    [Fact]
    public void The_only_recoverable_copy_is_never_overwritten()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno");
        File.Move(store.FilePath, store.TempPath);
        byte[] tmp = Read(store.TempPath);
        byte[] bak = Read(store.BackupPath);

        // Ni el almacén guarda (el primer paso vaciaría el .tmp)…
        Assert.Throws<VaultRecoveryPendingException>(() => store.WriteEnvelope([1, 2, 3]));

        // …ni la sesión crea una bóveda nueva, tampoco tras volver a mirar el disco (lo que hace «Archivar»).
        using var session = NewSession(store);
        Assert.Equal(UnlockStatus.AlreadyExists, session.CreateVault(Fake.OtherMasterPassword).Status);
        session.RefreshStoreState();
        Assert.Equal(VaultState.Locked, session.State);

        Assert.Equal(tmp, Read(store.TempPath));
        Assert.Equal(bak, Read(store.BackupPath));
        Assert.False(store.Exists);
    }

    [Fact]
    public void Setting_the_copies_aside_moves_them_without_deleting_and_only_then_allows_a_new_vault()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno", "Dos");
        File.Move(store.FilePath, store.TempPath);
        byte[] tmp = Read(store.TempPath);
        byte[] bak = Read(store.BackupPath);

        using var session = NewSession(store);
        session.SetAsideRecoveryCopies();
        Assert.Equal(VaultState.NotCreated, session.State);
        Assert.False(File.Exists(store.TempPath));
        Assert.False(File.Exists(store.BackupPath));
        string[] kept = Preserved(store);
        string keptTmp = Assert.Single(kept, p => p.EndsWith("-tmp.pdv", StringComparison.Ordinal));
        string keptBak = Assert.Single(kept, p => p.EndsWith("-bak.pdv", StringComparison.Ordinal));
        Assert.Equal(tmp, Read(keptTmp));
        Assert.Equal(bak, Read(keptBak));

        // Bóveda nueva, varios guardados y más puntos de recuperación de los que se conservan: las copias apartadas no cambian.
        Assert.True(session.CreateVault(Fake.OtherMasterPassword).Succeeded);
        session.AddEntry(Entry("Nueva"));
        session.AddEntry(Entry("Otra"));
        for (int i = 0; i < DpapiVaultStore.MaxRecoveryPoints + 2; i++)
        {
            session.CreateRecoveryPoint("importacion");
        }

        Assert.Equal(tmp, Read(keptTmp));
        Assert.Equal(bak, Read(keptBak));

        // Y se siguen abriendo con su contraseña (se pueden restaurar desde Ajustes).
        using var document = VaultSession.OpenBackupEnvelope(new DpapiVaultStore(keptTmp).ReadEnvelope(), Fake.MasterPassword);
        Assert.Equal(2, document.Entries.Count);
    }

    [Fact]
    public void Only_the_vault_own_copies_can_be_read_or_restored()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno");
        string other = temp.File("otra.pdv");
        File.Copy(store.FilePath, other);

        Assert.Throws<ArgumentException>(() => store.ReadRecoveryCopy(other));
        Assert.Throws<ArgumentException>(() => store.RestoreRecoveryCopy(other));
        Assert.Throws<ArgumentException>(() => store.RestoreRecoveryCopy(store.FilePath));
    }

    // ---- Recuperación interrumpida ----

    [Fact]
    public void An_interrupted_recovery_loses_nothing_and_can_be_retried()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno", "Dos");
        Corrupt(store);
        byte[] damaged = Read(store.FilePath);
        byte[] bak = Read(store.BackupPath);

        using var session = NewSession(store);
        using (FaultyReplace((_, _, _) => throw new SimulatedCrash())) // corte justo al colocar la copia
        {
            Assert.Throws<SimulatedCrash>(() => session.Recover(Fake.MasterPassword));
        }

        Assert.Equal(VaultState.Locked, session.State);
        Assert.Equal(damaged, Read(store.FilePath));
        Assert.Equal(bak, Read(store.BackupPath));
        string[] kept = Preserved(store);
        Assert.Equal(damaged, Read(Assert.Single(kept, p => p.EndsWith("-principal.pdv", StringComparison.Ordinal))));
        Assert.Equal(bak, Read(Assert.Single(kept, p => p.EndsWith("-bak.pdv", StringComparison.Ordinal))));

        Assert.True(session.Recover(Fake.MasterPassword).Succeeded);
        Assert.Equal(["Uno", "Dos"], Titles(session));
        Assert.Equal(bak, Read(store.BackupPath));
    }

    [Fact]
    public void A_half_written_main_file_left_by_an_interruption_is_recovered_from_the_backup()
    {
        using var temp = new TempDirectory();
        var store = NewVault(temp, "Uno", "Dos");
        File.Delete(store.FilePath);
        byte[] bak = Read(store.BackupPath);
        File.WriteAllBytes(store.FilePath, bak[..(bak.Length / 2)]); // la principal quedó a medias

        using var session = NewSession(store);
        Assert.Equal(UnlockStatus.VaultUnreadable, session.Unlock(Fake.MasterPassword).Status); // la capa DPAPI quedó cortada
        Assert.True(session.CanRecover);
        Assert.True(session.Recover(Fake.MasterPassword).Succeeded);
        Assert.Equal(["Uno"], Titles(session));
        Assert.Single(Preserved(store), p => p.EndsWith("-principal.pdv", StringComparison.Ordinal));
    }

    // ---- Ayudas ----

    /// <summary>Bóveda en disco con una entrada por título. Cada alta es un guardado, así que el .bak tiene la versión anterior.</summary>
    private static DpapiVaultStore NewVault(TempDirectory temp, params string[] titles)
    {
        var store = new DpapiVaultStore(temp.File("vault.pdv"));
        using var session = NewSession(store);
        Assert.True(session.CreateVault(Fake.MasterPassword).Succeeded);
        foreach (string title in titles)
        {
            session.AddEntry(Entry(title));
        }

        return store;
    }

    private static VaultSession NewSession(DpapiVaultStore store, InMemorySecurityStateStore? security = null) =>
        new(store, security ?? new InMemorySecurityStateStore(), new FakeClock(), Fake.FastIterations);

    private static CredentialEntry Entry(string title) =>
        Fake.Entry(title, $"https://{title.ToLowerInvariant()}.example", "usuario.prueba", Fake.Secret);

    private static string[] Titles(VaultSession session) => [.. session.Entries.Select(e => e.Title)];

    private static byte[] Read(string path) => File.ReadAllBytes(path);

    private static string[] Preserved(DpapiVaultStore store) =>
        Directory.Exists(store.RecoveryDirectory) ? Directory.GetFiles(store.RecoveryDirectory, DpapiVaultStore.PreservedCopyPrefix + "*") : [];

    /// <summary>Daña un byte del contenido cifrado (la capa DPAPI sigue siendo válida). El .bak queda con la versión buena.</summary>
    private static void Corrupt(DpapiVaultStore store)
    {
        byte[] envelope = store.ReadEnvelope();
        envelope[^2] ^= 0x10;
        store.WriteEnvelope(envelope);
    }

    /// <summary>Lo que deja ReplaceFile al fallar con 1177: el original ya renombrado como copia y el contenido nuevo aún en .tmp.</summary>
    private static void ReplaceFailsAfterRenaming(string temp, string full, string? backup)
    {
        File.Move(full, backup!, overwrite: true);
        throw new IOException("Simulación del error 1177.", ErrorUnableToMoveReplacement2);
    }

    private static Scope FaultyReplace(Action<string, string, string?> replace)
    {
        AtomicFile.ReplaceOverride.Value = replace;
        return new Scope(() => AtomicFile.ReplaceOverride.Value = null);
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    /// <summary>Un corte (proceso terminado, apagón): nada lo captura a mitad.</summary>
    private sealed class SimulatedCrash : Exception;
}
