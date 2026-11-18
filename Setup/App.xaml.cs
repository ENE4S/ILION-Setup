using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace Ilion.Setup.App;

/// <summary>Red de seguridad: un error inesperado no cierra ILION Setup; se anota con todo su detalle en ilion-setup.log y se avisa.</summary>
public partial class App : Application
{
    public static string LogFile { get; set; } = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "ILION.Setup", "ilion-setup.log");

    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Write("ERROR no controlado", e.Exception);
            e.Handled = true; // Setup sigue abierto
            MessageBox.Show("Ha ocurrido un error inesperado, pero ILION Setup sigue abierto y no se ha dejado nada a medias en el juego.\n\n"
                + e.Exception.Message + "\n\nEl detalle queda en " + LogFile + " (adjúntalo si lo reportas).", "ILION Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { Write("ERROR en segundo plano", e.Exception); e.SetObserved(); };
        System.AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("ERROR fatal", e.ExceptionObject as System.Exception);
    }

    private static void Write(string what, System.Exception? error)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!); File.AppendAllText(LogFile, $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss} {what}: {error}{System.Environment.NewLine}"); }
        catch (IOException) { }
    }
}
