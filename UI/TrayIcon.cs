using System.Runtime.InteropServices;

namespace DiskTempMonitor.UI;

/// <summary>
/// Icona in area di notifica gestita direttamente con Shell_NotifyIcon.
/// <para>
/// Serve per poter fissare noi l'identificativo (uID). Windows 11 memorizza la scelta
/// "mostra sempre nella barra" in <c>HKCU\Control Panel\NotifyIconSettings</c> usando
/// come chiave il percorso dell'eseguibile più l'uID: la NotifyIcon di WinForms assegna
/// l'uID da un contatore interno che cambia ogni volta che il componente viene ricreato,
/// perciò l'icona veniva vista come nuova e tornava nel menu a scomparsa.
/// </para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // ------------------------------------------------------------------ interop

    private const int WM_APP = 0x8000;
    private const int WM_TRAYCALLBACK = WM_APP + 1;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;                  // WM_USER + 0
    private const int NIN_KEYSELECT = 0x0401;               // WM_USER + 1
    private const int NIN_BALLOONUSERCLICK = 0x0405;        // WM_USER + 5

    private const uint NIM_ADD = 0x00;
    private const uint NIM_MODIFY = 0x01;
    private const uint NIM_DELETE = 0x02;
    private const uint NIM_SETVERSION = 0x04;

    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIF_INFO = 0x10;
    private const uint NIF_SHOWTIP = 0x80;

    private const uint NOTIFYICON_VERSION_4 = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static readonly uint WM_TASKBARCREATED = RegisterWindowMessageW("TaskbarCreated");

    // ------------------------------------------------------------------- stato

    private readonly uint _uid;
    private readonly MessageWindow _window;
    private Icon? _icon;
    private string _tip = "";
    private bool _added;
    private bool _disposed;

    public ContextMenuStrip? ContextMenu { get; set; }

    public event EventHandler? DoubleClick;
    public event EventHandler? BalloonClicked;

    /// <param name="uid">
    /// Identificativo stabile dell'icona: deve restare lo stesso a ogni avvio
    /// perché Windows ricordi se l'utente l'ha fissata nella barra.
    /// </param>
    public TrayIcon(uint uid)
    {
        _uid = uid;
        _window = new MessageWindow(this);
    }

    private NOTIFYICONDATA BuildData(uint flags)
    {
        return new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _window.Handle,
            uID = _uid,
            uFlags = flags,
            uCallbackMessage = WM_TRAYCALLBACK,
            hIcon = _icon?.Handle ?? IntPtr.Zero,
            szTip = _tip,
            szInfo = "",
            szInfoTitle = "",
            uVersion = NOTIFYICON_VERSION_4,
        };
    }

    /// <summary>Crea o aggiorna l'icona con la nuova immagine e il nuovo suggerimento.</summary>
    public void Update(Icon icon, string tip)
    {
        if (_disposed) return;

        var previous = _icon;
        _icon = icon;
        _tip = Truncate(tip, 127);

        var data = BuildData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);

        if (!_added)
        {
            if (Shell_NotifyIconW(NIM_ADD, ref data))
            {
                _added = true;
                var version = BuildData(0);
                Shell_NotifyIconW(NIM_SETVERSION, ref version);
            }
        }
        else if (!Shell_NotifyIconW(NIM_MODIFY, ref data))
        {
            // La shell può aver perso l'icona (riavvio di Explorer): si riparte da capo.
            _added = false;
            if (Shell_NotifyIconW(NIM_ADD, ref data))
            {
                _added = true;
                var version = BuildData(0);
                Shell_NotifyIconW(NIM_SETVERSION, ref version);
            }
        }

        // L'handle serve alla shell finché l'icona è quella corrente.
        if (!ReferenceEquals(previous, icon)) previous?.Dispose();
    }

    public void ShowBalloon(string title, string text, int timeoutMs)
    {
        if (_disposed || !_added) return;

        var data = BuildData(NIF_INFO);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = 0x02;                 // NIIF_WARNING
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private void Remove()
    {
        if (!_added) return;
        var data = BuildData(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        _added = false;
    }

    private bool HandleMessage(ref Message m)
    {
        if (m.Msg == WM_TASKBARCREATED)
        {
            // Explorer è ripartito: le icone vanno riaggiunte.
            _added = false;
            if (_icon is not null) Update(_icon, _tip);
            return false;
        }

        if (m.Msg != WM_TRAYCALLBACK) return false;

        int notification = (int)(m.LParam.ToInt64() & 0xFFFF);
        int x = (short)(m.WParam.ToInt64() & 0xFFFF);
        int y = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);

        switch (notification)
        {
            case WM_LBUTTONDBLCLK:
            case NIN_KEYSELECT:
                DoubleClick?.Invoke(this, EventArgs.Empty);
                break;

            case NIN_BALLOONUSERCLICK:
                BalloonClicked?.Invoke(this, EventArgs.Empty);
                break;

            case WM_CONTEXTMENU:
                ShowContextMenu(new Point(x, y));
                break;
        }

        return true;
    }

    private void ShowContextMenu(Point screenPoint)
    {
        if (ContextMenu is null) return;

        // Senza questo il menu resta aperto quando si clicca altrove.
        SetForegroundWindow(_window.Handle);
        ContextMenu.Show(screenPoint);
    }

    private static string Truncate(string value, int max)
    {
        value = value.Replace("\r", "");
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Remove();
        _window.DestroyHandle();
        _icon?.Dispose();
        _icon = null;
    }

    /// <summary>Finestra nascosta che riceve i messaggi di callback della shell.</summary>
    private sealed class MessageWindow : NativeWindow
    {
        private readonly TrayIcon _owner;

        public MessageWindow(TrayIcon owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams
            {
                Caption = "DiskTempMonitor.TrayIcon",
                Style = unchecked((int)0x80000000),     // WS_POPUP
                ExStyle = 0x00000080,                   // WS_EX_TOOLWINDOW
            });
        }

        protected override void WndProc(ref Message m)
        {
            if (_owner.HandleMessage(ref m)) return;
            base.WndProc(ref m);
        }
    }
}
