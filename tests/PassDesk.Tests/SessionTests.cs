using PassDesk.Auth;
using PassDesk.Crypto;
using PassDesk.Storage;

namespace PassDesk.Tests;

public class SessionTests
{
    [Fact]
    public void New_vault_requires_a_strong_master_password()
    {
        using var session = Fake.NewSession(out var store, out _);
        Assert.Equal(VaultState.NotCreated, session.State);

        Assert.Equal(UnlockStatus.WeakPassword, session.CreateVault("corta").Status);
        Assert.Equal(UnlockStatus.WeakPassword, session.CreateVault("aaaaaaaaaaaaaaaa").Status);
        Assert.False(store.Exists);

        Assert.True(session.CreateVault(Fake.MasterPassword).Succeeded);
        Assert.Equal(VaultState.Unlocked, session.State);
        Assert.True(store.Exists);
        Assert.Equal(UnlockStatus.AlreadyExists, session.CreateVault(Fake.MasterPassword).Status);
    }

    [Fact]
    public void Lock_and_unlock_cycle_persists_entries()
    {
        using var session = Fake.NewSession(out var store, out _);
        session.CreateVault(Fake.MasterPassword);
        session.AddEntry(Fake.Entry("Correo Ficticio", "https://mail.example.com", "prueba@example.com", Fake.Secret));

        session.Lock(LockReason.Manual);
        Assert.Equal(VaultState.Locked, session.State);
        Assert.Throws<InvalidOperationException>(() => session.Entries);

        Assert.Equal(UnlockStatus.WrongPassword, session.Unlock(Fake.OtherMasterPassword).Status);
        Assert.Equal(VaultState.Locked, session.State);

        Assert.True(session.Unlock(Fake.MasterPassword).Succeeded);
        var entry = Assert.Single(session.Entries);
        Assert.Equal(Fake.Secret, entry.Password.Reveal());
        Assert.False(Fake.ContainsText(store.Envelope!, Fake.Secret));
    }

    [Fact]
    public void Locking_wipes_decrypted_secrets_from_memory()
    {
        using var session = Fake.NewSession(out _, out _);
        session.CreateVault(Fake.MasterPassword);
        session.AddEntry(Fake.Entry("Tienda", "https://tienda.example", "comprador", Fake.Secret));
        var entry = session.Entries[0];

        session.Lock(LockReason.Manual);

        Assert.True(entry.Password.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => entry.Password.Reveal());
    }

    [Fact]
    public void Repeated_failures_are_throttled_and_survive_restart()
    {
        var security = new InMemorySecurityStateStore();
        using var session = Fake.NewSession(out var store, out var clock, security);
        session.CreateVault(Fake.MasterPassword);
        session.Lock(LockReason.Manual);

        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(UnlockStatus.WrongPassword, session.Unlock(Fake.OtherMasterPassword).Status);
        }

        var fourth = session.Unlock(Fake.OtherMasterPassword);
        Assert.Equal(UnlockStatus.WrongPassword, fourth.Status);
        Assert.Equal(TimeSpan.FromSeconds(5), fourth.RetryAfter);

        // Incluso con la contraseña correcta hay que esperar.
        var throttled = session.Unlock(Fake.MasterPassword);
        Assert.Equal(UnlockStatus.Throttled, throttled.Status);
        Assert.True(throttled.RetryAfter > TimeSpan.Zero);

        // Un reinicio de la aplicación no elimina la espera.
        using var restarted = new VaultSession(store, security, clock, Fake.FastIterations);
        Assert.Equal(UnlockStatus.Throttled, restarted.Unlock(Fake.MasterPassword).Status);

        clock.Advance(TimeSpan.FromSeconds(6));
        Assert.True(restarted.Unlock(Fake.MasterPassword).Succeeded);
        Assert.Equal(0, security.State.FailedAttempts);
    }

    [Fact]
    public async Task Windows_Hello_quick_unlock_only_after_password_unlock()
    {
        using var session = Fake.NewSession(out var store, out var clock);
        session.CreateVault(Fake.MasterPassword);
        session.AddEntry(Fake.Entry("Juego", "https://store.steampowered.com", "jugador", Fake.Secret));
        session.Lock(LockReason.Inactivity);

        Assert.True(session.CanQuickUnlock);
        var canceled = await session.QuickUnlockAsync(new FakePresenceVerifier(PresenceResult.Canceled), "Prueba");
        Assert.Equal(UnlockStatus.Canceled, canceled.Status);
        Assert.Equal(VaultState.Locked, session.State);

        var ok = await session.QuickUnlockAsync(new FakePresenceVerifier(PresenceResult.Verified), "Prueba");
        Assert.True(ok.Succeeded);
        Assert.Equal(Fake.Secret, Assert.Single(session.Entries).Password.Reveal());

        // Al reiniciar la aplicación siempre se exige la contraseña maestra.
        using var restarted = new VaultSession(store, new InMemorySecurityStateStore(), clock, Fake.FastIterations);
        var verifier = new FakePresenceVerifier(PresenceResult.Verified);
        Assert.False(restarted.CanQuickUnlock);
        Assert.Equal(UnlockStatus.NotAvailable, (await restarted.QuickUnlockAsync(verifier, "Prueba")).Status);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    public async Task Quick_unlock_can_be_disabled()
    {
        using var session = Fake.NewSession(out _, out _);
        session.CreateVault(Fake.MasterPassword);
        session.QuickUnlockEnabled = false;
        session.Lock(LockReason.Manual);

        Assert.False(session.CanQuickUnlock);
        var result = await session.QuickUnlockAsync(new FakePresenceVerifier(PresenceResult.Verified), "Prueba");
        Assert.Equal(UnlockStatus.NotAvailable, result.Status);
    }

    [Fact]
    public void Auto_lock_triggers_only_after_inactivity_timeout()
    {
        using var session = Fake.NewSession(out _, out var clock);
        var monitor = new InactivityMonitor(clock, TimeSpan.FromMinutes(5));
        var autoLock = new AutoLockController(session, monitor);
        session.CreateVault(Fake.MasterPassword);

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(autoLock.Tick());
        Assert.Equal(VaultState.Unlocked, session.State);

        monitor.RecordActivity();
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.False(autoLock.Tick());

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(autoLock.Tick());
        Assert.Equal(VaultState.Locked, session.State);
    }

    [Fact]
    public void Changing_master_password_rotates_keys()
    {
        using var session = Fake.NewSession(out var store, out _);
        session.CreateVault(Fake.MasterPassword);
        session.AddEntry(Fake.Entry("Red social", "https://facebook.com", "perfil", Fake.Secret));
        byte[] before = store.Envelope!;

        Assert.Equal(UnlockStatus.WrongPassword, session.ChangeMasterPassword(Fake.OtherMasterPassword, Fake.OtherMasterPassword).Status);
        Assert.Equal(UnlockStatus.WeakPassword, session.ChangeMasterPassword(Fake.MasterPassword, "123").Status);
        Assert.True(session.ChangeMasterPassword(Fake.MasterPassword, Fake.OtherMasterPassword).Succeeded);
        Assert.NotEqual(before.AsSpan(0, 86).ToArray(), store.Envelope!.AsSpan(0, 86).ToArray());

        session.Lock(LockReason.Manual);
        Assert.Equal(UnlockStatus.WrongPassword, session.Unlock(Fake.MasterPassword).Status);
        Assert.True(session.Unlock(Fake.OtherMasterPassword).Succeeded);
        Assert.Equal(Fake.Secret, session.Entries[0].Password.Reveal());
    }

    [Fact]
    public void Corrupted_or_foreign_vault_reports_clear_status()
    {
        using var session = Fake.NewSession(out var store, out _);
        session.CreateVault(Fake.MasterPassword);
        session.Lock(LockReason.Manual);

        store.Envelope![^2] ^= 0x10;
        Assert.Equal(UnlockStatus.VaultCorrupted, session.Unlock(Fake.MasterPassword).Status);

        var unreadable = new VaultSession(new ThrowingStore(), new InMemorySecurityStateStore(), new FakeClock(), Fake.FastIterations);
        Assert.Equal(UnlockStatus.VaultUnreadable, unreadable.Unlock(Fake.MasterPassword).Status);
    }

    [Fact]
    public void Backups_are_encrypted_and_restorable_with_password()
    {
        using var session = Fake.NewSession(out _, out _);
        session.CreateVault(Fake.MasterPassword);
        session.AddEntry(Fake.Entry("Copia", "https://copia.example", "usuario", Fake.Secret));

        byte[] backup = BackupFile.Wrap(session.CreateBackupEnvelope());
        Assert.False(Fake.ContainsText(backup, Fake.Secret));

        byte[] envelope = BackupFile.Unwrap(backup);
        Assert.Throws<InvalidMasterPasswordException>(() => VaultSession.OpenBackupEnvelope(envelope, Fake.OtherMasterPassword));
        using var restored = VaultSession.OpenBackupEnvelope(envelope, Fake.MasterPassword);
        Assert.Equal(Fake.Secret, Assert.Single(restored.Entries).Password.Reveal());
    }

    [Fact]
    public void State_changes_are_notified()
    {
        using var session = Fake.NewSession(out _, out _);
        var states = new List<(VaultState State, LockReason? Reason)>();
        session.StateChanged += (_, e) => states.Add((e.NewState, e.Reason));

        session.CreateVault(Fake.MasterPassword);
        session.Lock(LockReason.SessionLocked);

        var expected = new List<(VaultState State, LockReason? Reason)>
        {
            (VaultState.Unlocked, null),
            (VaultState.Locked, LockReason.SessionLocked),
        };
        Assert.Equal(expected, states);
    }

    [Theory]
    [InlineData("corta", PasswordStrength.TooShort)]
    [InlineData("aaaaaaaaaaaa", PasswordStrength.Weak)]
    [InlineData("caballoverde", PasswordStrength.Fair)]
    [InlineData("Caballo-Verde-Bateria-7", PasswordStrength.VeryStrong)]
    public void Master_password_strength_is_estimated(string candidate, PasswordStrength expected)
    {
        Assert.Equal(expected, MasterPasswordPolicy.Evaluate(candidate));
    }

    private sealed class ThrowingStore : IVaultStore
    {
        public bool Exists => true;

        public byte[] ReadEnvelope() => throw new VaultAccessDeniedException(unchecked((int)0x8009000B));

        public void WriteEnvelope(byte[] envelope) => throw new IOException();

        public void CreateRecoveryPoint(string reason) => throw new IOException();
    }
}
