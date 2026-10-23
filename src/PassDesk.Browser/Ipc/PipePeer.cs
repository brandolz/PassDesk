using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PassDesk.Browser.Ipc;

/// <summary>
/// Identifica el programa al otro lado de la tubería por la ruta de su ejecutable. Lo usan los dos extremos: PassDesk para
/// admitir solo al conector, y el conector para hablar solo con PassDesk. Ante cualquier duda, no es el esperado.
/// </summary>
public static class PipePeer
{
    /// <summary>Proceso que atiende la tubería, o null si no se puede saber o no es Windows.</summary>
    public static uint? ServerProcessId(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint processId) ? processId : null;
    }

    /// <summary>Si el proceso <paramref name="processId"/> ejecuta exactamente <paramref name="expectedPath"/>. Si no se puede comprobar, no.</summary>
    public static bool RunsExecutable(uint processId, string expectedPath)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return IsExpectedExecutable(process.MainModule?.FileName, expectedPath);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException or OverflowException)
        {
            return false;
        }
    }

    public static bool IsExpectedExecutable(string? candidatePath, string expectedPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(expectedPath))
        {
            return false;
        }

        return string.Equals(Path.GetFullPath(candidatePath), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase);
    }

    // DllImport y no LibraryImport: esta biblioteca no habilita código unsafe, que es lo que necesita el generador.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}
