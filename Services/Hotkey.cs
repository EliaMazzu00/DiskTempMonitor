namespace DiskTempMonitor.Services;

/// <summary>
/// Combinazioni da tastiera globali scritte come le legge una persona — "Ctrl+Alt+O" —
/// e tradotte in quello che vuole <c>RegisterHotKey</c>.
/// </summary>
public static class Hotkey
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    /// <summary>
    /// Senza questo la combinazione, oltre a fare il suo mestiere, arriverebbe anche
    /// alla finestra in primo piano: il gioco vedrebbe una O premuta dal nulla.
    /// </summary>
    private const uint ModNoRepeat = 0x4000;

    public static bool TryParse(string? testo, out uint modifiers, out uint key)
    {
        modifiers = ModNoRepeat;
        key = 0;

        if (string.IsNullOrWhiteSpace(testo)) return false;

        foreach (var pezzo in testo.Split('+', StringSplitOptions.RemoveEmptyEntries |
                                               StringSplitOptions.TrimEntries))
        {
            switch (pezzo.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModControl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift" or "maiusc": modifiers |= ModShift; break;
                case "win" or "windows": modifiers |= ModWin; break;
                default:
                    if (!Enum.TryParse<Keys>(pezzo, ignoreCase: true, out var k)) return false;
                    key = (uint)k;
                    break;
            }
        }

        // Una combinazione senza tasti di servizio ruberebbe quel tasto a tutto il
        // sistema: non si registra.
        return key != 0 && (modifiers & ~ModNoRepeat) != 0;
    }

    /// <summary>
    /// Prova a prendersi la combinazione e la rilascia subito: serve a dire all'utente,
    /// mentre la sceglie, se è libera o se qualcun altro se l'è già presa. È l'unico
    /// modo di saperlo: Windows non dice chi la tiene, solo che è occupata.
    /// </summary>
    public static bool IsAvailable(IntPtr owner, string? testo)
    {
        if (!TryParse(testo, out uint mods, out uint key)) return false;

        const int idProva = 0xD71;
        if (!RegisterHotKey(owner, idProva, mods, key)) return false;

        UnregisterHotKey(owner, idProva);
        return true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
