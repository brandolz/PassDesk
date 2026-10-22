using PassDesk.Browser.Host;
using PassDesk.Browser.Ipc;

namespace PassDesk.NativeHost;

/// <summary>
/// Punto de entrada del conector. El navegador lo lanza con el origen de la extensión como primer argumento, le envía un
/// mensaje por la entrada estándar y espera la respuesta por la salida estándar. No escribe nada más (ni en consola ni en
/// archivos): cualquier texto extra rompería el protocolo y podría filtrar datos.
/// </summary>
internal static class Program
{
    private const string AllowedOriginsFile = "allowed-origins.txt";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            // Sin lista de extensiones autorizadas (o ilegible), no se atiende a nadie.
            string listPath = Path.Combine(AppContext.BaseDirectory, AllowedOriginsFile);
            IReadOnlyList<string> allowed = File.Exists(listPath)
                ? NativeHostRunner.ParseAllowedCallers(File.ReadAllLines(listPath))
                : [];
            var options = new NativeHostOptions
            {
                AllowedCallers = allowed,
                PipeName = PipeNames.ForCurrentUser(),
            };

            await using var input = Console.OpenStandardInput();
            await using var output = Console.OpenStandardOutput();
            return await NativeHostRunner.RunAsync(args, input, output, options, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nunca se imprime la excepción: podría contener datos de la solicitud.
            return 1;
        }
    }
}
