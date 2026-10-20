using System.Security.Cryptography;
using System.Text;
using PassDesk.Auth;
using PassDesk.Core.Diagnostics;
using PassDesk.Core.Model;
using PassDesk.Core.Search;
using PassDesk.Crypto;
using PassDesk.Storage;

namespace PassDesk.Tests;

public class DocumentTests
{
    // Contenido ficticio que simula un documento sensible.
    private static readonly byte[] FakeScan = Encoding.UTF8.GetBytes("DOCUMENTO FICTICIO · DNI 00000000T · Nombre: Persona de Prueba");

    [Fact]
    public void Document_cipher_round_trips_and_binds_to_document_id()
    {
        using var key = DocumentCipher.NewKey();
        var id = Guid.NewGuid();
        byte[] blob = DocumentCipher.Seal(key, id, FakeScan);

        Assert.False(Fake.ContainsBytes(blob, FakeScan));
        using (var plain = DocumentCipher.Open(key, id, blob))
        {
            Assert.Equal(FakeScan, plain.Span.ToArray());
        }

        Assert.Throws<VaultFormatException>(() => DocumentCipher.Open(key, Guid.NewGuid(), blob));
        using var otherKey = DocumentCipher.NewKey();
        Assert.Throws<VaultFormatException>(() => DocumentCipher.Open(otherKey, id, blob));
        blob[^1] ^= 0x01;
        Assert.Throws<VaultFormatException>(() => DocumentCipher.Open(key, id, blob));
    }

    [Fact]
    public void Vault_serialization_keeps_documents_and_their_keys()
    {
        using var document = new VaultDocument();
        var doc = new SecureDocument { Title = "DNI", FileName = "dni.jpg", Size = 42, Sha256 = "ABC", CreatedUtc = DateTimeOffset.UnixEpoch, ModifiedUtc = DateTimeOffset.UnixEpoch };
        doc.SetKey(DocumentCipher.NewKey());
        document.Documents.Add(doc);
        byte[] blob = DocumentCipher.Seal(doc.Key, doc.Id, FakeScan);

        using var json = VaultDocumentSerializer.Serialize(document);
        using var copy = VaultDocumentSerializer.Deserialize(json.Span);

        var restored = Assert.Single(copy.Documents);
        Assert.Equal(doc.Id, restored.Id);
        Assert.Equal("dni.jpg", restored.FileName);
        Assert.Equal(DocumentKind.Image, restored.Kind);
        using var plain = DocumentCipher.Open(restored.Key, restored.Id, blob);
        Assert.Equal(FakeScan, plain.Span.ToArray());
    }

    [Fact]
    public void Dpapi_document_store_keeps_documents_encrypted_on_disk()
    {
        using var temp = new TempDirectory();
        var store = new DpapiDocumentStore(temp.File("docs"));
        using var key = DocumentCipher.NewKey();
        var id = Guid.NewGuid();
        byte[] blob = DocumentCipher.Seal(key, id, FakeScan);

        store.Write(id, blob);

        byte[] onDisk = File.ReadAllBytes(store.PathFor(id));
        Assert.Equal("PDDL"u8.ToArray(), onDisk[..4]);
        Assert.False(Fake.ContainsBytes(onDisk, FakeScan));
        Assert.Equal(blob, store.Read(id));

        store.Delete(id);
        Assert.False(store.Exists(id));
    }

    [Fact]
    public void Service_adds_opens_renames_exports_and_deletes_documents()
    {
        using var temp = new TempDirectory();
        using var session = Fake.NewSession(out _, out var clock);
        session.CreateVault(Fake.MasterPassword);
        var store = new InMemoryDocumentStore();
        var service = new SecureDocumentService(session, store, clock);
        File.WriteAllBytes(temp.File("DNI frontal.jpg"), FakeScan);
        File.WriteAllBytes(temp.File("copia.jpg"), FakeScan);

        var result = service.AddFiles([temp.File("DNI frontal.jpg"), temp.File("copia.jpg"), temp.File("no-existe.pdf")]);

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Duplicates);
        Assert.Equal(1, result.Failed);
        Assert.True(File.Exists(temp.File("DNI frontal.jpg"))); // el original no se toca
        var doc = Assert.Single(service.Documents);
        Assert.Equal("DNI frontal", doc.Title);
        Assert.Equal(FakeScan.Length, doc.Size);
        Assert.False(Fake.ContainsBytes(store.Blobs[doc.Id], FakeScan));

        using (var plain = service.Open(doc.Id))
        {
            Assert.Equal(FakeScan, plain.Span.ToArray());
        }

        service.Rename(doc.Id, "DNI (anverso)");
        Assert.Equal("DNI (anverso)", Assert.Single(service.Documents).Title);

        service.Export(doc.Id, temp.File("exportado.jpg"));
        Assert.Equal(FakeScan, File.ReadAllBytes(temp.File("exportado.jpg")));

        Assert.True(service.Delete(doc.Id));
        Assert.Empty(service.Documents);
        Assert.Empty(store.Blobs);
    }

    [Fact]
    public void Oversized_files_are_rejected()
    {
        using var temp = new TempDirectory();
        using var session = Fake.NewSession(out _, out var clock);
        session.CreateVault(Fake.MasterPassword);
        var service = new SecureDocumentService(session, new InMemoryDocumentStore(), clock);
        using (var stream = File.Create(temp.File("enorme.pdf")))
        {
            stream.SetLength(DocumentCipher.MaxDocumentBytes + 1);
        }

        var result = service.AddFiles([temp.File("enorme.pdf")]);

        Assert.Equal(1, result.TooLarge);
        Assert.Empty(service.Documents);
    }

    [Fact]
    public void Locking_makes_documents_unavailable()
    {
        using var temp = new TempDirectory();
        using var session = Fake.NewSession(out _, out var clock);
        session.CreateVault(Fake.MasterPassword);
        var service = new SecureDocumentService(session, new InMemoryDocumentStore(), clock);
        File.WriteAllBytes(temp.File("pasaporte.pdf"), FakeScan);
        service.AddFiles([temp.File("pasaporte.pdf")]);
        var doc = Assert.Single(service.Documents);

        session.Lock(LockReason.Manual);

        Assert.True(doc.Key.IsDisposed);
        Assert.Throws<InvalidOperationException>(() => service.Open(doc.Id));
    }

    [Fact]
    public void Backups_include_documents_and_restore_them_on_another_vault()
    {
        using var temp = new TempDirectory();
        using var source = Fake.NewSession(out _, out var clock);
        source.CreateVault(Fake.MasterPassword);
        var sourceDocs = new SecureDocumentService(source, new InMemoryDocumentStore(), clock);
        File.WriteAllBytes(temp.File("dni.jpg"), FakeScan);
        sourceDocs.AddFiles([temp.File("dni.jpg")]);

        byte[] backup = BackupFile.Wrap(source.CreateBackupEnvelope(), sourceDocs.CollectForBackup());
        Assert.False(Fake.ContainsBytes(backup, FakeScan));

        var contents = BackupFile.UnwrapContents(backup);
        using var opened = VaultSession.OpenBackupEnvelope(contents.Envelope, Fake.MasterPassword);

        using var target = Fake.NewSession(out _, out var clock2);
        target.CreateVault(Fake.OtherMasterPassword);
        var targetStore = new InMemoryDocumentStore();
        var targetDocs = new SecureDocumentService(target, targetStore, clock2);

        Assert.Equal(1, targetDocs.RestoreFromBackup(opened, contents.Documents));
        Assert.Equal(0, targetDocs.RestoreFromBackup(opened, contents.Documents)); // ya estaba
        var restored = Assert.Single(targetDocs.Documents);
        using var plain = targetDocs.Open(restored.Id);
        Assert.Equal(FakeScan, plain.Span.ToArray());
    }

    [Fact]
    public void Version_1_backups_are_still_readable()
    {
        byte[] envelope = Fake.Utf8("sobre de prueba");
        byte[] legacy = [.. "PDBK"u8.ToArray(), 1, .. envelope];

        var contents = BackupFile.UnwrapContents(legacy);

        Assert.Equal(envelope, contents.Envelope);
        Assert.Empty(contents.Documents);
        Assert.Throws<VaultFormatException>(() => BackupFile.UnwrapContents([.. "PDBK"u8.ToArray(), 2, 9, 9]));
    }

    [Fact]
    public void Document_operations_never_log_file_names_or_content()
    {
        using var temp = new TempDirectory();
        var memory = new MemoryLogSink();
        AppLog.AddSink(memory);
        try
        {
            using var session = Fake.NewSession(out _, out var clock);
            session.CreateVault(Fake.MasterPassword);
            var service = new SecureDocumentService(session, new InMemoryDocumentStore(), clock);
            File.WriteAllBytes(temp.File("Pasaporte-Secreto-Ficticio.pdf"), FakeScan);
            service.AddFiles([temp.File("Pasaporte-Secreto-Ficticio.pdf"), temp.File("Otro-Inexistente-Ficticio.pdf")]);
            var doc = service.Documents[0];
            service.Export(doc.Id, temp.File("salida.pdf"));
            service.Delete(doc.Id);
        }
        finally
        {
            AppLog.RemoveSink(memory);
        }

        string text = memory.AllText;
        Assert.Contains("DocumentsAdded", text);
        Assert.DoesNotContain("Pasaporte-Secreto-Ficticio", text);
        Assert.DoesNotContain("Otro-Inexistente-Ficticio", text);
        Assert.DoesNotContain("00000000T", text);
    }

    [Fact]
    public void Documents_are_searchable_by_title_and_file_name()
    {
        var docs = new List<SecureDocument>
        {
            new() { Title = "Pasaporte", FileName = "passport_2026.pdf", ModifiedUtc = DateTimeOffset.UnixEpoch },
            new() { Title = "Carné de conducir", FileName = "licencia.jpg", ModifiedUtc = DateTimeOffset.UnixEpoch.AddDays(1) },
        };

        Assert.Equal("Carné de conducir", Assert.Single(DocumentSearch.Apply(docs, "carne", SortMode.NameAscending)).Title);
        Assert.Equal("Pasaporte", Assert.Single(DocumentSearch.Apply(docs, "passport", SortMode.NameAscending)).Title);
        Assert.Equal("Carné de conducir", DocumentSearch.Apply(docs, null, SortMode.ModifiedNewest)[0].Title);
        Assert.Equal(DocumentKind.Pdf, docs[0].Kind);
        Assert.Equal(DocumentKind.Image, docs[1].Kind);
    }
}
