using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;
using PassDesk.Core.Model;
using PassDesk.Crypto;

namespace PassDesk.Storage;

/// <summary>Resumen de la última importación (no contiene datos de las credenciales).</summary>
public sealed record ImportRecord(DateTimeOffset TimestampUtc, string SourceId, int Added, int Updated, int Skipped);

/// <summary>Contenido descifrado de la bóveda. Solo existe en memoria mientras está desbloqueada.</summary>
public sealed class VaultDocument : IDisposable
{
    public List<CredentialEntry> Entries { get; } = [];

    /// <summary>Metadatos y claves de los documentos cifrados (el contenido está en archivos aparte).</summary>
    public List<SecureDocument> Documents { get; } = [];

    public ImportRecord? LastImport { get; set; }

    public void Dispose()
    {
        foreach (var entry in Entries)
        {
            entry.Dispose();
        }

        foreach (var document in Documents)
        {
            document.Dispose();
        }

        Entries.Clear();
        Documents.Clear();
    }
}

/// <summary>
/// Serializa el documento a JSON UTF-8 directamente sobre memoria fijada que se pone a cero.
/// Las contraseñas, notas, semillas TOTP y claves pasan de su forma protegida al búfer en Base64, sin crear
/// cadenas gestionadas ni copias intermedias. Este JSON solo existe en memoria y siempre se cifra antes de
/// tocar el disco.
/// </summary>
public static class VaultDocumentSerializer
{
    // Versión 2: contraseñas, notas y semillas TOTP en Base64. En la versión 1 iban como texto JSON, y Utf8JsonWriter
    // escapa el texto (ñ, +, &…) en un búfer compartido de ArrayPool que no se pone a cero. La versión 1 se sigue leyendo.
    private const int SchemaVersion = 2;

    public static SensitiveBuffer Serialize(VaultDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var output = new SecureBufferWriter(Math.Max(16 * 1024, document.Entries.Count * 512));
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", SchemaVersion);
            if (document.LastImport is { } last)
            {
                writer.WriteStartObject("lastImport");
                writer.WriteString("utc", last.TimestampUtc);
                writer.WriteString("source", last.SourceId);
                writer.WriteNumber("added", last.Added);
                writer.WriteNumber("updated", last.Updated);
                writer.WriteNumber("skipped", last.Skipped);
                writer.WriteEndObject();
            }

            writer.WriteStartArray("entries");
            foreach (var entry in document.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("id", entry.Id);
                writer.WriteString("title", entry.Title);
                writer.WriteString("url", entry.Url);
                writer.WriteString("user", entry.Username);
                WriteSecret(writer, "pw", entry.Password);
                WriteSecret(writer, "notes", entry.Notes);
                WriteSecret(writer, "otp", entry.OtpAuth);
                writer.WriteString("cat", entry.Category.ToString());
                writer.WriteBoolean("catManual", entry.IsCategoryManual);
                writer.WriteString("created", entry.CreatedUtc);
                writer.WriteString("modified", entry.ModifiedUtc);
                if (entry.ImportedUtc is { } imported)
                {
                    writer.WriteString("imported", imported);
                }

                writer.WriteString("src", entry.Source);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("docs");
            foreach (var doc in document.Documents)
            {
                writer.WriteStartObject();
                writer.WriteString("id", doc.Id);
                writer.WriteString("title", doc.Title);
                writer.WriteString("file", doc.FileName);
                writer.WriteNumber("size", doc.Size);
                writer.WriteString("sha256", doc.Sha256);
                WriteSecret(writer, "key", doc.Key);
                writer.WriteString("created", doc.CreatedUtc);
                writer.WriteString("modified", doc.ModifiedUtc);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var result = new SensitiveBuffer(output.WrittenCount);
        output.WrittenSpan.CopyTo(result.Span);
        return result;
    }

    public static VaultDocument Deserialize(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 16 });
        var document = new VaultDocument();
        int version = 1; // "v" se escribe siempre antes que las entradas
        try
        {
            Next(ref reader);
            Expect(ref reader, JsonTokenType.StartObject);
            while (Next(ref reader) != JsonTokenType.EndObject)
            {
                Expect(ref reader, JsonTokenType.PropertyName);
                if (reader.ValueTextEquals("v"u8))
                {
                    Next(ref reader);
                    version = reader.GetInt32();
                    if (version > SchemaVersion)
                    {
                        throw new VaultFormatException("La bóveda se creó con una versión más reciente de PassDesk.");
                    }
                }
                else if (reader.ValueTextEquals("lastImport"u8))
                {
                    Next(ref reader);
                    document.LastImport = reader.TokenType == JsonTokenType.Null ? null : ReadImport(ref reader);
                }
                else if (reader.ValueTextEquals("entries"u8))
                {
                    Next(ref reader);
                    Expect(ref reader, JsonTokenType.StartArray);
                    while (Next(ref reader) != JsonTokenType.EndArray)
                    {
                        document.Entries.Add(ReadEntry(ref reader, base64Secrets: version >= 2));
                    }
                }
                else if (reader.ValueTextEquals("docs"u8))
                {
                    Next(ref reader);
                    Expect(ref reader, JsonTokenType.StartArray);
                    while (Next(ref reader) != JsonTokenType.EndArray)
                    {
                        document.Documents.Add(ReadDocument(ref reader));
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            return document;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            document.Dispose();
            throw new VaultFormatException("El contenido de la bóveda no es válido.");
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>Los secretos se escriben en Base64, que va directo al búfer fijado (nunca se escapa).</summary>
    private static void WriteSecret(Utf8JsonWriter writer, string name, ProtectedSecret secret)
    {
        if (secret.IsEmpty)
        {
            return;
        }

        using var plain = secret.Unprotect();
        writer.WriteBase64String(name, plain.Span);
    }

    private static ImportRecord ReadImport(ref Utf8JsonReader reader)
    {
        Expect(ref reader, JsonTokenType.StartObject);
        DateTimeOffset utc = default;
        string source = string.Empty;
        int added = 0, updated = 0, skipped = 0;
        while (Next(ref reader) != JsonTokenType.EndObject)
        {
            Expect(ref reader, JsonTokenType.PropertyName);
            if (reader.ValueTextEquals("utc"u8)) { Next(ref reader); utc = reader.GetDateTimeOffset(); }
            else if (reader.ValueTextEquals("source"u8)) { Next(ref reader); source = reader.GetString() ?? string.Empty; }
            else if (reader.ValueTextEquals("added"u8)) { Next(ref reader); added = reader.GetInt32(); }
            else if (reader.ValueTextEquals("updated"u8)) { Next(ref reader); updated = reader.GetInt32(); }
            else if (reader.ValueTextEquals("skipped"u8)) { Next(ref reader); skipped = reader.GetInt32(); }
            else { reader.Skip(); }
        }

        return new ImportRecord(utc, source, added, updated, skipped);
    }

    private static CredentialEntry ReadEntry(ref Utf8JsonReader reader, bool base64Secrets)
    {
        Expect(ref reader, JsonTokenType.StartObject);
        Guid id = Guid.Empty;
        string title = string.Empty, url = string.Empty, user = string.Empty, source = EntrySources.Manual;
        var password = ProtectedSecret.Empty;
        var notes = ProtectedSecret.Empty;
        var otp = ProtectedSecret.Empty;
        var category = Category.Other;
        bool manual = false;
        DateTimeOffset created = default, modified = default;
        DateTimeOffset? imported = null;

        try
        {
            while (Next(ref reader) != JsonTokenType.EndObject)
            {
                Expect(ref reader, JsonTokenType.PropertyName);
                if (reader.ValueTextEquals("id"u8)) { Next(ref reader); id = reader.GetGuid(); }
                else if (reader.ValueTextEquals("title"u8)) { Next(ref reader); title = reader.GetString() ?? string.Empty; }
                else if (reader.ValueTextEquals("url"u8)) { Next(ref reader); url = reader.GetString() ?? string.Empty; }
                else if (reader.ValueTextEquals("user"u8)) { Next(ref reader); user = reader.GetString() ?? string.Empty; }
                else if (reader.ValueTextEquals("pw"u8)) { Next(ref reader); password.Dispose(); password = ReadSecret(ref reader, base64Secrets); }
                else if (reader.ValueTextEquals("notes"u8)) { Next(ref reader); notes.Dispose(); notes = ReadSecret(ref reader, base64Secrets); }
                else if (reader.ValueTextEquals("otp"u8)) { Next(ref reader); otp.Dispose(); otp = ReadSecret(ref reader, base64Secrets); }
                else if (reader.ValueTextEquals("cat"u8))
                {
                    Next(ref reader);
                    category = Enum.TryParse<Category>(reader.GetString(), ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
                        ? parsed
                        : Category.Other;
                }
                else if (reader.ValueTextEquals("catManual"u8)) { Next(ref reader); manual = reader.GetBoolean(); }
                else if (reader.ValueTextEquals("created"u8)) { Next(ref reader); created = reader.GetDateTimeOffset(); }
                else if (reader.ValueTextEquals("modified"u8)) { Next(ref reader); modified = reader.GetDateTimeOffset(); }
                else if (reader.ValueTextEquals("imported"u8))
                {
                    Next(ref reader);
                    imported = reader.TokenType == JsonTokenType.Null ? null : reader.GetDateTimeOffset();
                }
                else if (reader.ValueTextEquals("src"u8)) { Next(ref reader); source = reader.GetString() ?? EntrySources.Manual; }
                else { reader.Skip(); }
            }
        }
        catch
        {
            password.Dispose();
            notes.Dispose();
            otp.Dispose();
            throw;
        }

        var entry = new CredentialEntry
        {
            Id = id == Guid.Empty ? Guid.NewGuid() : id,
            Title = title,
            Url = url,
            Username = user,
            Category = category,
            IsCategoryManual = manual,
            CreatedUtc = created,
            ModifiedUtc = modified == default ? created : modified,
            ImportedUtc = imported,
            Source = source,
        };
        entry.SetPassword(password);
        entry.SetNotes(notes);
        entry.SetOtpAuth(otp);
        return entry;
    }

    private static SecureDocument ReadDocument(ref Utf8JsonReader reader)
    {
        Expect(ref reader, JsonTokenType.StartObject);
        Guid id = Guid.Empty;
        string title = string.Empty, file = string.Empty, sha256 = string.Empty;
        long size = 0;
        var key = ProtectedSecret.Empty;
        DateTimeOffset created = default, modified = default;
        try
        {
            while (Next(ref reader) != JsonTokenType.EndObject)
            {
                Expect(ref reader, JsonTokenType.PropertyName);
                if (reader.ValueTextEquals("id"u8)) { Next(ref reader); id = reader.GetGuid(); }
                else if (reader.ValueTextEquals("title"u8)) { Next(ref reader); title = reader.GetString() ?? string.Empty; }
                else if (reader.ValueTextEquals("file"u8)) { Next(ref reader); file = reader.GetString() ?? string.Empty; }
                else if (reader.ValueTextEquals("size"u8)) { Next(ref reader); size = reader.GetInt64(); }
                else if (reader.ValueTextEquals("sha256"u8)) { Next(ref reader); sha256 = reader.GetString() ?? string.Empty; }
                else if (reader.ValueTextEquals("key"u8)) { Next(ref reader); key.Dispose(); key = ReadSecret(ref reader, base64: true); }
                else if (reader.ValueTextEquals("created"u8)) { Next(ref reader); created = reader.GetDateTimeOffset(); }
                else if (reader.ValueTextEquals("modified"u8)) { Next(ref reader); modified = reader.GetDateTimeOffset(); }
                else { reader.Skip(); }
            }

            if (id == Guid.Empty || key.IsEmpty)
            {
                throw new JsonException();
            }
        }
        catch
        {
            key.Dispose();
            throw;
        }

        var doc = new SecureDocument
        {
            Id = id,
            Title = title,
            FileName = file,
            Size = size,
            Sha256 = sha256,
            CreatedUtc = created,
            ModifiedUtc = modified == default ? created : modified,
        };
        doc.SetKey(key);
        return doc;
    }

    /// <summary>
    /// Copia el valor (sin escapes) a memoria fijada y, si está en Base64, lo decodifica en ese mismo búfer: no pasa
    /// por cadenas, ArrayPool ni arrays sin fijar.
    /// </summary>
    private static ProtectedSecret ReadSecret(ref Utf8JsonReader reader, bool base64)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return ProtectedSecret.Empty;
        }

        Expect(ref reader, JsonTokenType.String);
        int max = reader.HasValueSequence ? checked((int)reader.ValueSequence.Length) : reader.ValueSpan.Length;
        using var buffer = new SensitiveBuffer(max);
        int length = reader.CopyString(buffer.Span);
        if (base64 && Base64.DecodeFromUtf8InPlace(buffer.Span[..length], out length) != OperationStatus.Done)
        {
            throw new JsonException();
        }

        return ProtectedSecret.FromBytes(buffer.Span[..length]);
    }

    private static JsonTokenType Next(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            throw new JsonException();
        }

        return reader.TokenType;
    }

    private static void Expect(ref Utf8JsonReader reader, JsonTokenType type)
    {
        if (reader.TokenType != type)
        {
            throw new JsonException();
        }
    }
}
