using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ImprimePlus;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        InitializeComponent();

        // Red de seguridad global: sin esto, CUALQUIER excepcion no atrapada en el
        // hilo de UI (p.ej. la que tiraba un FileOpenPicker en ciertas maquinas)
        // termina el proceso en seco. Marcamos Handled para mantener la app viva y
        // dejamos un log en %TEMP%\ImprimePlus para poder diagnosticar el incidente.
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            TryLogCrash(args.ExceptionObject as Exception, fatal: args.IsTerminating);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            TryLogCrash(args.Exception, fatal: false);
            args.SetObserved();
        };
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        TryLogCrash(e.Exception, fatal: false);
        // Mantener la app abierta: el usuario no pierde su trabajo por un fallo aislado.
        e.Handled = true;
    }

    private static void TryLogCrash(Exception? ex, bool fatal)
    {
        if (ex is null) return;
        try
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ImprimePlus");
            System.IO.Directory.CreateDirectory(dir);
            var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] fatal={fatal}{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}";
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"), line);
        }
        catch { /* el logging jamas debe propagar */ }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        Window.Activate();
    }
}
