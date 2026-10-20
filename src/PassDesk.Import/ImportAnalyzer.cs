using PassDesk.Core.Diagnostics;
using PassDesk.Core.Model;

namespace PassDesk.Import;

public enum ImportItemStatus
{
    /// <summary>No existe en PassDesk: se añadirá.</summary>
    New,

    /// <summary>Ya existe una credencial idéntica: se omitirá.</summary>
    Duplicate,

    /// <summary>Existe el mismo sitio y usuario con otra contraseña, nota o código.</summary>
    Conflict,

    /// <summary>Repetida dentro del propio archivo: se omitirá.</summary>
    DuplicateInFile,
}

public enum ConflictResolution
{
    /// <summary>Actualiza siempre la credencial existente con los datos importados.</summary>
    UpdateExisting,

    /// <summary>Conserva la existente y añade la importada como nueva.</summary>
    KeepBoth,

    /// <summary>No cambia nada.</summary>
    Skip,

    /// <summary>
    /// Opción recomendada, que nunca pierde un cambio hecho en PassDesk:
    /// <list type="bullet">
    /// <item>Copia de seguridad (los datos traen fecha): actualiza solo si la versión importada es más reciente;
    /// si es más antigua, deja la actual.</item>
    /// <item>Exportación de Apple (sin fechas): actualiza si la cuenta no se ha modificado en PassDesk desde su
    /// última importación; si se modificó o se creó a mano, conserva ambas versiones.</item>
    /// </list>
    /// </summary>
    UpdateUnlessChangedLocally,
}

/// <summary>Qué se hace con un conflicto concreto según la opción elegida.</summary>
public enum ConflictAction
{
    Update,
    AddAsNew,
    KeepCurrent,
}

public sealed class ImportPreviewItem
{
    public required ParsedCredential Item { get; init; }

    public required ImportItemStatus Status { get; init; }

    public Guid? ExistingId { get; init; }
}

/// <summary>Vista previa de una importación. No muestra ni contiene contraseñas en claro.</summary>
public sealed class ImportPreview
{
    public required ParseResult Source { get; init; }

    public required IReadOnlyList<ImportPreviewItem> Items { get; init; }

    public int NewCount => Items.Count(i => i.Status == ImportItemStatus.New);

    public int DuplicateCount => Items.Count(i => i.Status is ImportItemStatus.Duplicate or ImportItemStatus.DuplicateInFile);

    public int ConflictCount => Items.Count(i => i.Status == ImportItemStatus.Conflict);

    public int InvalidCount => Source.Issues.Count;
}

/// <param name="ConflictsNotImported">Conflictos cuyos datos del archivo no se han guardado (opción «No cambiar»).</param>
public sealed record ImportOutcome(int Added, int Updated, int Skipped, int Invalid, int ConflictsNotImported = 0)
{
    /// <summary>
    /// Todo lo que traía el archivo está ya en la bóveda (sin filas no válidas ni conflictos descartados). Solo
    /// entonces se puede borrar automáticamente el archivo de origen.
    /// </summary>
    public bool AllDataImported => Invalid == 0 && ConflictsNotImported == 0;
}

/// <summary>Detecta credenciales nuevas, duplicadas y en conflicto frente a la bóveda actual.</summary>
public static class ImportAnalyzer
{
    public static ImportPreview Analyze(IEnumerable<CredentialEntry> existing, ParseResult parsed)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(parsed);

        var index = new Dictionary<string, List<CredentialEntry>>(StringComparer.Ordinal);
        foreach (var entry in existing)
        {
            if (!index.TryGetValue(entry.DuplicateKey, out var list))
            {
                index[entry.DuplicateKey] = list = [];
            }

            list.Add(entry);
        }

        var seenInFile = new Dictionary<string, List<ParsedCredential>>(StringComparer.Ordinal);
        var items = new List<ImportPreviewItem>(parsed.Items.Count);
        foreach (var item in parsed.Items)
        {
            string key = item.DuplicateKey;
            ImportItemStatus status;
            Guid? existingId = null;

            if (seenInFile.TryGetValue(key, out var earlier) && earlier.Any(other => SameSecrets(other, item)))
            {
                status = ImportItemStatus.DuplicateInFile;
            }
            else if (index.TryGetValue(key, out var matches))
            {
                var identical = matches.FirstOrDefault(m => SameSecrets(m, item));
                if (identical is not null)
                {
                    status = ImportItemStatus.Duplicate;
                    existingId = identical.Id;
                }
                else
                {
                    status = ImportItemStatus.Conflict;
                    existingId = matches[0].Id;
                }
            }
            else
            {
                status = ImportItemStatus.New;
            }

            if (!seenInFile.TryGetValue(key, out var bucket))
            {
                seenInFile[key] = bucket = [];
            }

            bucket.Add(item);
            items.Add(new ImportPreviewItem { Item = item, Status = status, ExistingId = existingId });
        }

        return new ImportPreview { Source = parsed, Items = items };
    }

    private static bool SameSecrets(CredentialEntry entry, ParsedCredential item) =>
        entry.Password.ContentEquals(item.Password)
        && entry.Notes.ContentEquals(item.Notes)
        && entry.OtpAuth.ContentEquals(item.OtpAuth);

    private static bool SameSecrets(ParsedCredential a, ParsedCredential b) =>
        a.Password.ContentEquals(b.Password)
        && a.Notes.ContentEquals(b.Notes)
        && a.OtpAuth.ContentEquals(b.OtpAuth);
}

/// <summary>Aplica una vista previa confirmada sobre la lista de credenciales de la bóveda.</summary>
public static class ImportApplier
{
    public static ImportOutcome Apply(
        IList<CredentialEntry> entries,
        ImportPreview preview,
        ConflictResolution resolution,
        DateTimeOffset now,
        string sourceId)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(preview);

        var outcome = Run(entries, preview, resolution, now, sourceId, apply: true);
        AppLog.Info(
            LogEvent.ImportApplied,
            new LogMetric("added", outcome.Added),
            new LogMetric("updated", outcome.Updated),
            new LogMetric("skipped", outcome.Skipped),
            new LogMetric("invalid", outcome.Invalid),
            new LogMetric("conflictsNotImported", outcome.ConflictsNotImported));
        return outcome;
    }

    /// <summary>Calcula qué haría <see cref="Apply"/> con esta opción, sin cambiar nada (para la vista previa).</summary>
    public static ImportOutcome Simulate(IReadOnlyList<CredentialEntry> entries, ImportPreview preview, ConflictResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(preview);
        return Run([.. entries], preview, resolution, DateTimeOffset.UtcNow, string.Empty, apply: false);
    }

    /// <summary>Decide qué hacer con un conflicto concreto.</summary>
    public static ConflictAction Decide(ConflictResolution resolution, CredentialEntry? existing, ParsedCredential item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (existing is null)
        {
            // La cuenta desapareció entre la vista previa y la importación: se añade para no perder nada.
            return resolution == ConflictResolution.Skip ? ConflictAction.KeepCurrent : ConflictAction.AddAsNew;
        }

        return resolution switch
        {
            ConflictResolution.UpdateExisting => ConflictAction.Update,
            ConflictResolution.KeepBoth => ConflictAction.AddAsNew,
            ConflictResolution.Skip => ConflictAction.KeepCurrent,
            _ when item.ModifiedUtc is { } importedModified =>
                importedModified > existing.ModifiedUtc ? ConflictAction.Update : ConflictAction.KeepCurrent,
            _ => existing.ImportedUtc is { } imported && existing.ModifiedUtc <= imported
                ? ConflictAction.Update
                : ConflictAction.AddAsNew,
        };
    }

    private static ImportOutcome Run(IList<CredentialEntry> entries, ImportPreview preview, ConflictResolution resolution, DateTimeOffset now, string sourceId, bool apply)
    {
        int added = 0, updated = 0, skipped = 0, conflictsNotImported = 0;
        foreach (var previewItem in preview.Items)
        {
            if (previewItem.Status == ImportItemStatus.New)
            {
                if (apply)
                {
                    entries.Add(CreateEntry(previewItem.Item, now, sourceId));
                }

                added++;
                continue;
            }

            if (previewItem.Status != ImportItemStatus.Conflict)
            {
                skipped++; // idénticas: ya están en la bóveda
                continue;
            }

            var existing = entries.FirstOrDefault(e => e.Id == previewItem.ExistingId);
            switch (Decide(resolution, existing, previewItem.Item))
            {
                case ConflictAction.Update:
                    if (apply)
                    {
                        Update(existing!, previewItem.Item, now);
                    }

                    updated++;
                    break;

                case ConflictAction.AddAsNew:
                    if (apply)
                    {
                        entries.Add(CreateEntry(previewItem.Item, now, sourceId));
                    }

                    added++;
                    break;

                default:
                    skipped++;
                    if (resolution == ConflictResolution.Skip)
                    {
                        conflictsNotImported++;
                    }

                    break;
            }
        }

        return new ImportOutcome(added, updated, skipped, preview.InvalidCount, conflictsNotImported);
    }

    private static CredentialEntry CreateEntry(ParsedCredential item, DateTimeOffset now, string sourceId)
    {
        var entry = new CredentialEntry
        {
            Title = item.Title,
            Url = item.Url,
            Username = item.Username,
            Category = item.Category ?? CategoryClassifier.Classify(item.Title, item.Url),
            IsCategoryManual = item.IsCategoryManual,
            CreatedUtc = item.CreatedUtc ?? now,
            ModifiedUtc = item.ModifiedUtc ?? now,
            ImportedUtc = now,
            Source = sourceId,
        };
        entry.SetPassword(item.Password.Clone());
        entry.SetNotes(item.Notes.Clone());
        entry.SetOtpAuth(item.OtpAuth.Clone());
        return entry;
    }

    private static void Update(CredentialEntry entry, ParsedCredential item, DateTimeOffset now)
    {
        if (item.Title.Length > 0)
        {
            entry.Title = item.Title;
        }

        if (item.Url.Length > 0)
        {
            entry.Url = item.Url;
        }

        entry.SetPassword(item.Password.Clone());

        // No se borran notas ni códigos existentes si el origen no los trae.
        if (!item.Notes.IsEmpty)
        {
            entry.SetNotes(item.Notes.Clone());
        }

        if (!item.OtpAuth.IsEmpty)
        {
            entry.SetOtpAuth(item.OtpAuth.Clone());
        }

        if (!entry.IsCategoryManual)
        {
            entry.Category = item.Category ?? CategoryClassifier.Classify(entry.Title, entry.Url);
        }

        entry.ModifiedUtc = now;
        entry.ImportedUtc = now;
    }
}
