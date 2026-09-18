using System.Runtime.InteropServices;
using Windows.UI.Notifications;

namespace DiskTempMonitor.Services;

/// <summary>
/// Notifiche native di Windows (i riquadri che compaiono in basso a destra e restano
/// nel Centro notifiche).
/// <para>
/// Un'applicazione desktop non pacchettizzata può mostrarle solo se Windows sa a chi
/// attribuirle: serve un identificativo (AppUserModelID) e un collegamento nel menu
/// Start che lo dichiari. Senza, la notifica viene semplicemente scartata. Qui il
/// collegamento viene creato al primo avvio.
/// </para>
/// </summary>
internal static class WindowsToast
{
    private const string AppId = "DiskTempMonitor.Desktop";
    private const string ShortcutName = "Disk Temp Monitor.lnk";

    private static bool _initialised;
    private static bool _available;

    /// <summary>True se Windows accetta le nostre notifiche.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (!_initialised) Initialise();
            return _available;
        }
    }

    public static string? LastError { get; private set; }

    private static void Initialise()
    {
        _initialised = true;
        try
        {
            EnsureStartMenuShortcut();
            // Se l'identificativo non è registrato, questa chiamata solleva un'eccezione.
            _ = ToastNotificationManager.CreateToastNotifier(AppId);
            _available = true;
        }
        catch (Exception ex)
        {
            _available = false;
            LastError = ex.Message;
        }
    }

    /// <summary>Mostra una notifica. Restituisce false se non è stato possibile.</summary>
    public static bool Show(string title, string body, bool important)
    {
        if (!IsAvailable) return false;

        try
        {
            string xml =
                $"""
                <toast scenario="{(important ? "reminder" : "default")}" launch="show">
                  <visual>
                    <binding template="ToastGeneric">
                      <text>{Escape(title)}</text>
                      <text>{Escape(body)}</text>
                    </binding>
                  </visual>
                </toast>
                """;

            var doc = new Windows.Data.Xml.Dom.XmlDocument();
            doc.LoadXml(xml);

            var toast = new ToastNotification(doc)
            {
                // Senza scadenza resterebbero nel Centro notifiche per giorni.
                ExpirationTime = DateTimeOffset.Now.AddHours(important ? 4 : 1),
            };

            ToastNotificationManager.CreateToastNotifier(AppId).Show(toast);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ------------------------------------------------- collegamento nel menu Start

    private static void EnsureStartMenuShortcut()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");
        Directory.CreateDirectory(folder);

        string path = Path.Combine(folder, ShortcutName);
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DiskTempMonitor.exe");

        // Se esiste già e punta all'eseguibile giusto non si tocca: riscriverlo a ogni
        // avvio farebbe perdere a Windows le impostazioni di notifica dell'utente.
        if (File.Exists(path) && ShortcutTargetsExe(path, exe)) return;

        CreateShortcut(path, exe);
    }

    private static bool ShortcutTargetsExe(string shortcutPath, string exe)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(shortcutPath, 0);

            var sb = new System.Text.StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            Marshal.FinalReleaseComObject(link);

            return string.Equals(sb.ToString(), exe, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void CreateShortcut(string path, string exe)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exe);
            link.SetArguments("--tray");
            link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
            link.SetDescription("Temperature e S.M.A.R.T. dei dischi");

            var store = (IPropertyStore)link;
            var key = PropertyKeys.AppUserModelId;

            // PROPVARIANT costruito a mano: InitPropVariantFromString è inline
            // nell'header di Windows, non una funzione esportata da propsys.dll.
            var value = new PropVariant
            {
                VarType = VT_LPWSTR,
                Value = Marshal.StringToCoTaskMemUni(AppId),
            };
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally { PropVariantClear(ref value); }

            ((IPersistFile)link).Save(path, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    // --------------------------------------------------------------- interop COM

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
                     int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath,
                             int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
                  [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant propvar);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort VarType;
        private readonly ushort _reserved1, _reserved2, _reserved3;
        public IntPtr Value;
        private readonly IntPtr _padding;
    }

    private static class PropertyKeys
    {
        // PKEY_AppUserModel_ID
        public static PropertyKey AppUserModelId => new()
        {
            FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            PropertyId = 5,
        };
    }

    private const ushort VT_LPWSTR = 31;

    [DllImport("ole32.dll", PreserveSig = false)]
    private static extern void PropVariantClear(ref PropVariant pvar);
}
