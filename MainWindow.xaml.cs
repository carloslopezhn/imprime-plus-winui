using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace ImprimePlus;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Botones de la barra de título (min/max/cerrar): fijar colores explícitos
        // para que SIEMPRE se vean (antes quedaban casi invisibles sobre el fondo
        // claro y solo aparecían al pasar el mouse).
        var tb = AppWindow.TitleBar;
        Color ink = Color.FromArgb(255, 0x1E, 0x29, 0x3B);   // texto/glifo oscuro
        Color inkDim = Color.FromArgb(255, 0x64, 0x74, 0x8B); // inactivo, aún legible
        Color hoverBg = Color.FromArgb(255, 0xD7, 0xDF, 0xEA);
        Color pressBg = Color.FromArgb(255, 0xBF, 0xCB, 0xDA);
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = ink;
        tb.ButtonInactiveForegroundColor = inkDim;
        tb.ButtonHoverForegroundColor = ink;
        tb.ButtonHoverBackgroundColor = hoverBg;
        tb.ButtonPressedForegroundColor = ink;
        tb.ButtonPressedBackgroundColor = pressBg;

        // Arrancar SIEMPRE maximizada (default permanente).
        if (AppWindow.Presenter is OverlappedPresenter p)
            p.Maximize();

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }
}
