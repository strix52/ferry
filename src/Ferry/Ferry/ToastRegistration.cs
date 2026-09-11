using System.IO;
using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace Ferry;

// Makes Windows willing to show Ferry's toast notifications.
//
// ToastNotificationManager.CreateToastNotifier(aumid) works for a packaged app
// out of the box. For an unpackaged Win32 exe like this one it only works if a
// Start-Menu shortcut exists whose System.AppUserModel.ID property carries the
// same aumid, and the failure is silent: Show() neither throws nor displays, so
// there is nothing to catch and fall back from. Hence Ready — callers ask
// before choosing between a toast and the tray balloon.
//
// The shortcut is written under the *user's* Start Menu, needs no elevation,
// and is named apart from the shortcut the old clipboardSync install owns.
internal static class ToastRegistration
{
    // Stable for the life of the app: Windows keys the notification settings
    // the user sees in Settings > Notifications off this string, so changing it
    // silently resets their per-app choices.
    public const string Aumid = "Ferry.Desktop";

    // Uses the unified Ferry.lnk shortcut created by the installer.
    private const string ShortcutName = "Ferry.lnk";

    // False when the shortcut had to be created this run. Windows caches the
    // shell's view of the Start Menu, and a toast fired seconds after the
    // shortcut appears is usually dropped; from the next launch it works.
    public static bool Ready { get; private set; }

    public static void Ensure()
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(Aumid);
        }
        catch (Exception ex)
        {
            App.Log($"aumid: could not tag process: {ex.Message}");
            return;
        }

        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            App.Log("aumid: no process path, toasts stay off");
            return;
        }

        var shortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            ShortcutName);

        try
        {
            if (PointsAt(shortcut, exe))
            {
                Ready = true;
                return;
            }

            Write(shortcut, exe);
            App.Log($"aumid: wrote {ShortcutName}; toasts available from the next launch");
        }
        catch (Exception ex)
        {
            // Nothing here is worth failing a launch over — the caller falls
            // back to the tray balloon, which needs no registration at all.
            App.Log($"aumid: shortcut failed: {ex.Message}");
        }
    }

    private static bool PointsAt(string shortcut, string exe)
    {
        if (!File.Exists(shortcut)) return false;
        var link = (IShellLinkW)new ShellLink();
        ((ComTypes.IPersistFile)link).Load(shortcut, 0);
        var buffer = Marshal.AllocHGlobal(MaxPath * sizeof(char));
        try
        {
            link.GetPath(buffer, MaxPath, IntPtr.Zero, 0);
            var target = Marshal.PtrToStringUni(buffer) ?? "";
            return string.Equals(target, exe, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.ReleaseComObject(link);
        }
    }

    private static void Write(string shortcut, string exe)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);

        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
            link.SetDescription("Send text and files between this PC and phone.");

            var store = (IPropertyStore)link;
            var key = new PropertyKey(
                new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5); // System.AppUserModel.ID
            var value = PropVariant.FromString(Aumid);
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally
            {
                PropVariantClear(ref value);
            }

            ((ComTypes.IPersistFile)link).Save(shortcut, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }

        // Without this the shell can keep serving a stale Start-Menu listing,
        // which delays the shortcut being honoured by more than a relaunch.
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
    }

    private const int MaxPath = 260;
    private const uint SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_IDLIST = 0x0000;

    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variant);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    // Only ever filled and cleared by COM/OLE, never read field by field; the
    // two pointers give the union its native width on both 32- and 64-bit.
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
#pragma warning disable CS0169, CS0649 // layout-only fields
        private ushort _type;
        private ushort _reserved1;
        private ushort _reserved2;
        private ushort _reserved3;
        private IntPtr _value;
        private IntPtr _valueHigh;
#pragma warning restore CS0169, CS0649

        public static PropVariant FromString(string s) => new()
        {
            _type = 31, // VT_LPWSTR
            _value = Marshal.StringToCoTaskMemUni(s)
        };
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    // Every method has to be declared even though Ferry calls four of them:
    // the vtable is positional, so a missing entry would silently shift the
    // rest. The unused ones take raw pointers to keep the marshalling trivial.
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(IntPtr file, int chars, IntPtr findData, uint flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription(IntPtr name, int chars);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(IntPtr dir, int chars);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments(IntPtr args, int chars);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out ushort hotkey);
        void SetHotkey(ushort hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation(IntPtr iconPath, int chars, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint props);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
