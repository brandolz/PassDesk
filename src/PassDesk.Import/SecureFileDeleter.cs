using System.Security.Cryptography;
using PassDesk.Core.Diagnostics;

namespace PassDesk.Import;

public sealed record SecureDeleteResult(bool Deleted, string? Error, IReadOnlyList<string> Warnings);

/// <summary>
/// Elimina el archivo de exportación sin cifrar: lo sobrescribe con datos aleatorios, lo trunca,
/// lo renombra y lo borra (sin pasar por la Papelera de reciclaje).
/// <para>
/// Limitación honesta: en SSD/NVMe (nivelación de desgaste), con instantáneas de volumen, copias de
/// seguridad o carpetas sincronizadas en la nube, la sobrescritura no garantiza que no queden copias.
/// </para>
/// </summary>
public static class SecureFileDeleter
{
    public const string GeneralWarning =
        "En unidades SSD/NVMe, y si hay copias de seguridad o instantáneas del sistema (Historial de archivos, " +
        "Restaurar sistema), la sobrescritura no garantiza que no queden restos físicos del archivo.";

    /// <summary>Avisos específicos de la ubicación del archivo (OneDrive, iCloud Drive, unidades de red).</summary>
    public static IReadOnlyList<string> GetLocationWarnings(string path)
    {
        var warnings = new List<string>();
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return warnings;
        }

        foreach (string variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            if (IsUnder(fullPath, Environment.GetEnvironmentVariable(variable)))
            {
                warnings.Add("El archivo está en una carpeta de OneDrive: puede existir una copia en la nube y en la Papelera de reciclaje de OneDrive. Elimínala también allí.");
                break;
            }
        }

        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (IsUnder(fullPath, Path.Combine(profile, "iCloudDrive")))
        {
            warnings.Add("El archivo está en iCloud Drive: elimínalo también de iCloud Drive y de «Eliminados recientemente».");
        }

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(fullPath) ?? fullPath);
            if (drive.DriveType is DriveType.Network or DriveType.Removable)
            {
                warnings.Add("El archivo está en una unidad de red o extraíble; pueden existir copias fuera de este equipo.");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
        }

        return warnings;
    }

    public const string ChangedFileError =
        "El archivo ha cambiado desde que se leyó (quizá es una exportación más reciente con el mismo nombre). " +
        "No se ha borrado: revísalo y, si hace falta, impórtalo de nuevo.";

    /// <summary>
    /// Sobrescribe y elimina el archivo. Con <paramref name="expected"/>, antes de tocarlo comprueba (sobre el mismo
    /// archivo abierto en exclusiva) que su tamaño y su SHA-256 coinciden con lo que se importó; si no, no lo borra.
    /// </summary>
    public static SecureDeleteResult Delete(string path, FileFingerprint? expected = null)
    {
        var warnings = new List<string>(GetLocationWarnings(path)) { GeneralWarning };
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new SecureDeleteResult(false, "El archivo ya no existe.", warnings);
            }

            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return new SecureDeleteResult(false, "Por seguridad no se eliminan accesos directos ni enlaces simbólicos. Borra el archivo manualmente.", warnings);
            }

            if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                info.Attributes &= ~FileAttributes.ReadOnly;
            }

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, bufferSize: 0, FileOptions.WriteThrough))
            {
                if (expected is not null && !Matches(stream, expected))
                {
                    AppLog.Warn(LogEvent.SecureDeleteRefused);
                    return new SecureDeleteResult(false, ChangedFileError, warnings);
                }

                stream.Position = 0;
                long length = stream.Length;
                var block = new byte[64 * 1024];
                long remaining = length;
                while (remaining > 0)
                {
                    int count = (int)Math.Min(block.Length, remaining);
                    RandomNumberGenerator.Fill(block.AsSpan(0, count));
                    stream.Write(block, 0, count);
                    remaining -= count;
                }

                stream.Flush(flushToDisk: true);
                stream.SetLength(0);
                stream.Flush(flushToDisk: true);
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            string renamed = Path.Combine(directory, Path.GetRandomFileName());
            File.Move(path, renamed);
            File.Delete(renamed);
            AppLog.Info(LogEvent.SecureDeleteCompleted);
            return new SecureDeleteResult(true, null, warnings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error(LogEvent.SecureDeleteFailed, ex);
            return new SecureDeleteResult(false, "No se pudo eliminar el archivo. Puede estar abierto en otra aplicación (por ejemplo, Excel). Ciérrala y bórralo manualmente.", warnings);
        }
    }

    /// <summary>Compara tamaño y SHA-256 leyendo el archivo ya abierto (sin dejar copias del contenido).</summary>
    private static bool Matches(FileStream stream, FileFingerprint expected)
    {
        if (stream.Length != expected.Length)
        {
            return false;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = PassDesk.Crypto.SecureMemory.AllocatePinned(64 * 1024);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            PassDesk.Crypto.SecureMemory.Zero(buffer);
        }

        byte[] actual = hash.GetHashAndReset();
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected.Sha256));
    }

    private static bool IsUnder(string fullPath, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
    }
}
