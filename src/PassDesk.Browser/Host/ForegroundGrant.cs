using System.IO.Pipes;
using System.Runtime.InteropServices;
using PassDesk.Browser.Ipc;
using PassDesk.Browser.Protocol;

namespace PassDesk.Browser.Host;

/// <summary>
/// Cesión del primer plano de Windows a PassDesk. El navegador lanza el conector justo después de un clic del usuario, así
/// que el conector puede pasar a primer plano; PassDesk, que está en segundo plano, no. Antes de reenviar una solicitud que
/// abre una ventana en PassDesk (la confirmación o la pantalla de desbloqueo), el conector le cede ese permiso con
/// <c>AllowSetForegroundWindow</c> para que la ventana salga delante del navegador y reciba el teclado. Solo permite a
/// PassDesk ponerse delante una vez; no autoriza nada. Fuera de Windows no hace nada.
/// </summary>
internal static class ForegroundGrant
{
    /// <summary>Solo las solicitudes que abren una ventana en PassDesk necesitan el primer plano.</summary>
    public static bool IsNeededFor(RequestType type) => type is RequestType.Fill or RequestType.Unlock;

    /// <summary>
    /// Cede el primer plano al proceso de la tubería. Solo se llama con una tubería ya verificada al conectar (es PassDesk).
    /// Si Windows no lo permite, no pasa nada: PassDesk avisa en la barra de tareas.
    /// </summary>
    public static void ToPipeServer(NamedPipeClientStream pipe)
    {
        if (PipePeer.ServerProcessId(pipe) is uint processId)
        {
            AllowSetForegroundWindow(processId);
        }
    }

    // DllImport y no LibraryImport: esta biblioteca no habilita código unsafe, que es lo que necesita el generador.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
