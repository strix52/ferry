using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Ferry;

// Desktop Window Manager chrome. WPF cannot express any of this: the caption
// colour, the 1px window border, the rounded corners and the Mica backdrop all
// belong to DWM, not to the WPF visual tree. Without it a dark Ferry window
// still gets a light grey system title bar and a square white border.
//
// Every attribute here is best-effort. DwmSetWindowAttribute returns
// E_INVALIDARG for attributes the running build does not know, so on an older
// Windows the calls simply no-op and the window falls back to opaque chrome.
internal static class WindowEffects
{
    private const int UseImmersiveDarkMode = 20;   // Windows 10 20H1+
    private const int WindowCornerPreference = 33; // Windows 11
    private const int BorderColor = 34;            // Windows 11
    private const int SystemBackdropType = 38;     // Windows 11 22H2+

    private const int CornerRound = 2;
    private const int BackdropMica = 2;
    private const uint ColorDefault = 0xFFFFFFFF;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Applies DWM chrome to an already-created window (call from
    /// SourceInitialized or later — before that there is no HWND).
    /// Returns true when the Mica backdrop was accepted, which is the caller's
    /// cue to leave its background translucent.
    /// </summary>
    public static bool Apply(Window window, bool dark, bool useMica = true)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        // Dark mode first: it decides the colour of the DWM-drawn border and
        // of the caption buttons we did not replace.
        var darkFlag = dark ? 1 : 0;
        Set(hwnd, UseImmersiveDarkMode, ref darkFlag);

        var corner = CornerRound;
        Set(hwnd, WindowCornerPreference, ref corner);

        // Let DWM pick the border colour for the active/inactive states rather
        // than freezing one that will be wrong after a theme change.
        var border = unchecked((int)ColorDefault);
        Set(hwnd, BorderColor, ref border);

        // The main transfer window deliberately uses an opaque Passage ground:
        // full-client Mica can reveal different wallpaper tones through its
        // custom caption and make that single bar look like two windows. Small
        // secondary surfaces may continue to opt into Mica.
        var backdrop = useMica ? BackdropMica : 1; // DWMSBT_NONE
        var backdropApplied = Set(hwnd, SystemBackdropType, ref backdrop);
        var mica = useMica && backdropApplied;
        // Name the window: every dialog runs through here too, and three
        // identical lines in the crash log look like a loop.
        App.Log(useMica
            ? $"dwm mica {(mica ? "on" : "unavailable")} ({window.GetType().Name})"
            : $"dwm backdrop solid ({window.GetType().Name})");
        return mica;
    }

    private static bool Set(IntPtr hwnd, int attribute, ref int value)
    {
        try
        {
            return DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}
