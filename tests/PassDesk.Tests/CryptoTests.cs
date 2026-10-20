using System.Text;
using PassDesk.Crypto;

namespace PassDesk.Tests;

public class CryptoTests
{
    [Fact]
    public void Seal_then_open_with_password_round_trips()
    {
        byte[] payload = Fake.Utf8("{\"pw\":\"" + Fake.Secret + "\"}");
        using var keyring = VaultKeyring.Create(Fake.MasterPassword, Fake.FastIterations);
        byte[] envelope = keyring.Seal(payload);

        using var reopened = VaultKeyring.Unlock(VaultKeyring.ReadHeader(envelope), Fake.MasterPassword);
        using var plain = reopened.Open(envelope);

        Assert.Equal(payload, plain.Span.ToArray());
    }

    [Fact]
    public void Envelope_does_not_contain_plaintext()
    {
        using var keyring = VaultKeyring.Create(Fake.MasterPassword, Fake.FastIterations);
        byte[] envelope = keyring.Seal(Fake.Utf8("usuario@example.com " + Fake.Secret));

        Assert.False(Fake.ContainsText(envelope, Fake.Secret));
        Assert.False(Fake.ContainsText(envelope, "usuario@example.com"));
        Assert.False(Fake.ContainsText(envelope, Fake.MasterPassword));
    }

    [Fact]
    public void Wrong_password_is_rejected()
    {
        using var keyring = VaultKeyring.Create(Fake.MasterPassword, Fake.FastIterations);
        byte[] envelope = keyring.Seal(Fake.Utf8("datos"));

        Assert.Throws<InvalidMasterPasswordException>(() => VaultKeyring.Unlock(VaultKeyring.ReadHeader(envelope), Fake.OtherMasterPassword));
        Assert.Throws<InvalidMasterPasswordException>(() => VaultKeyring.Unlock(VaultKeyring.ReadHeader(envelope), string.Empty));
    }

    [Fact]
    public void Tampered_ciphertext_fails_integrity_check()
    {
        using var keyring = VaultKeyring.Create(Fake.MasterPassword, Fake.FastIterations);
        byte[] envelope = keyring.Seal(Fake.Utf8("contenido de prueba suficientemente largo"));
        envelope[^3] ^= 0x01;

        Assert.Throws<VaultFormatException>(() => keyring.Open(envelope));
    }

    [Fact]
    public void Tampered_key_header_is_rejected()
    {
        using var keyring = VaultKeyring.Create(Fake.MasterPassword, Fake.FastIterations);
        byte[] envelope = keyring.Seal(Fake.Utf8("datos"));
        envelope[12] ^= 0xFF; // byte de la sal

        Assert.Throws<InvalidMasterPasswordException>(() => VaultKeyring.Unlock(VaultKeyring.ReadHeader(envelope), Fake.MasterPassword));
    }

    [Fact]
    public void Every_seal_uses_a_fresh_nonce()
    {
        using var keyring = VaultKeyring.Create(Fake.MasterPassword, Fake.FastIterations);
        byte[] a = keyring.Seal(Fake.Utf8("igual"));
        byte[] b = keyring.Seal(Fake.Utf8("igual"));

        Assert.NotEqual(a, b);
        Assert.NotEqual(a.AsSpan(86, 12).ToArray(), b.AsSpan(86, 12).ToArray());
    }

    [Fact]
    public void Envelope_from_another_key_is_rejected()
    {
        using var first = VaultKeyring.Create(Fake.MasterPassword, Fake.FastIterations);
        using var second = VaultKeyring.Create(Fake.MasterPassword, Fake.FastIterations);
        byte[] envelope = first.Seal(Fake.Utf8("datos"));

        Assert.Throws<VaultFormatException>(() => second.Open(envelope));
    }

    [Fact]
    public void Garbage_and_truncated_envelopes_are_rejected()
    {
        Assert.Throws<VaultFormatException>(() => VaultKeyring.ReadHeader(Fake.Utf8("esto no es una bóveda")));

        using var keyring = VaultKeyring.Create(Fake.MasterPassword, Fake.FastIterations);
        byte[] envelope = keyring.Seal(Fake.Utf8("datos de prueba"));
        Assert.Throws<VaultFormatException>(() => keyring.Open(envelope.AsSpan(0, envelope.Length - 4)));
    }

    [Fact]
    public void Default_kdf_follows_owasp_recommendation()
    {
        Assert.True(VaultKeyring.DefaultIterations >= 600_000);
    }

    [Fact]
    public void Header_rejects_out_of_range_iterations()
    {
        using var keyring = VaultKeyring.Create(Fake.MasterPassword, Fake.FastIterations);
        byte[] envelope = keyring.Seal(Fake.Utf8("x"));
        BitConverter.GetBytes(int.MaxValue).CopyTo(envelope, 6);

        Assert.Throws<VaultFormatException>(() => VaultKeyring.ReadHeader(envelope));
    }

    [Fact]
    public void Protected_secret_round_trips_and_never_prints_its_value()
    {
        using var secret = ProtectedSecret.FromString(Fake.Secret2);

        Assert.Equal(Fake.Secret2, secret.Reveal());
        Assert.Equal("[redacted]", secret.ToString());
        Assert.DoesNotContain(Fake.Secret2, $"Valor: {secret}");
    }

    [Fact]
    public void Protected_secret_compares_content_and_clones_independently()
    {
        using var a = ProtectedSecret.FromString(Fake.Secret);
        using var b = ProtectedSecret.FromString(Fake.Secret);
        using var c = ProtectedSecret.FromString(Fake.Secret2);
        var clone = a.Clone();

        Assert.True(a.ContentEquals(b));
        Assert.False(a.ContentEquals(c));
        Assert.True(ProtectedSecret.Empty.ContentEquals(ProtectedSecret.FromString("")));

        clone.Dispose();
        Assert.Equal(Fake.Secret, a.Reveal());
    }

    [Fact]
    public void Disposed_secret_cannot_be_read()
    {
        var secret = ProtectedSecret.FromString(Fake.Secret);
        secret.Dispose();

        Assert.True(secret.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => secret.Reveal());
    }

    [Fact]
    public void Sensitive_buffer_is_zeroed_on_dispose()
    {
        var buffer = new SensitiveBuffer(32);
        buffer.Span.Fill(0xAB);
        buffer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => buffer.Span.Length);
    }

    [Fact]
    public void Secure_buffer_writer_keeps_content_when_growing()
    {
        using var writer = new SecureBufferWriter(256);
        byte[] data = Encoding.ASCII.GetBytes(new string('x', 10_000));
        var span = writer.GetSpan(data.Length);
        data.CopyTo(span);
        writer.Advance(data.Length);

        Assert.Equal(data, writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void Dpapi_round_trips_for_current_user_and_rejects_tampering()
    {
        byte[] data = Fake.Utf8("capa local de prueba");
        byte[] blob = WindowsDataProtection.Protect(data);

        Assert.Equal(data, WindowsDataProtection.Unprotect(blob));
        Assert.False(Fake.ContainsBytes(blob, data));

        blob[^5] ^= 0x55;
        Assert.Throws<VaultAccessDeniedException>(() => WindowsDataProtection.Unprotect(blob));
    }
}
