# Installer di Disk Temp Monitor

Procedura guidata che installa il programma come una qualunque applicazione di Windows:
voce nel menu Start (ricercabile), collegamento sul desktop, avvio automatico opzionale e
disinstallazione da *Impostazioni → App → App installate*.

## Produrre il pacchetto

```powershell
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

Risultato: `installer\Output\DiskTempMonitor-<versione>-setup.exe` (circa 40 MB).

Lo script:

1. legge la versione da `DiskTempMonitor.csproj` (`-Version` la sovrascrive);
2. pubblica l'applicazione **self-contained** in `installer\stage` — il runtime .NET viaggia
   dentro il pacchetto, quindi su chi installa non serve nessun prerequisito;
3. compila `DiskTempMonitor.iss` con Inno Setup.

Con `-SkipPublish` riusa quanto c'è già in `stage` e ricompila solo l'installer: comodo
mentre si mette mano allo script `.iss`.

### Cosa serve sul computer che compila

- .NET SDK (qualunque versione capace di compilare `net9.0-windows`)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php):

  ```powershell
  winget install --id JRSoftware.InnoSetup -e
  ```

  `build.ps1` lo cerca nel PATH e nelle cartelle di installazione abituali, compresa
  `%LOCALAPPDATA%\Programs\Inno Setup 6` dove winget lo mette per impostazione predefinita.

## Cosa fa la procedura guidata

| Passo | Dettaglio |
|---|---|
| Lingua | Italiano o inglese, proposta secondo quella di Windows |
| Cartella | `C:\Program Files\Disk Temp Monitor`, modificabile |
| Menu Start | `Disk Temp Monitor`, per tutti gli utenti |
| Desktop | collegamento opzionale, preselezionato |
| Avvio automatico | attività pianificata `DiskTempMonitor` con privilegi massimi, opzionale |
| Fine | avvio immediato del programma, opzionale |

L'installazione richiede i privilegi di amministratore: l'applicazione li richiede comunque
per parlare con il driver di archiviazione, e installandola in *Programmi* è disponibile a
tutti gli utenti del computer.

### Perché l'avvio automatico è un'attività pianificata

Il manifesto dichiara `requireAdministrator`. Windows ignora al login i programmi elevati
elencati nella chiave `Run` del registro, quindi l'unica strada che non chiede conferma a
ogni accesso è l'Utilità di pianificazione con `/RL HIGHEST`. È lo stesso meccanismo che
l'applicazione usa dalle impostazioni (`Services/Startup.cs`), con lo stesso nome di
attività: attivarlo qui o là è equivalente, e l'installer ripulisce la vecchia voce nella
chiave `Run` lasciata dalle versioni precedenti alla 1.5.

### Perché l'avvio a fine installazione usa `shellexec`

Le voci `[Run]` con il flag `postinstall` Inno le esegue con l'utente originale, quello non
elevato che ha lanciato il setup, e con `CreateProcess`. Ma `CreateProcess` non sa elevare
da sé un programma che dichiara `requireAdministrator`: fallisce con l'errore 740,
*"è necessaria l'esecuzione con privilegi elevati"*. Il flag `shellexec` passa da
`ShellExecute`, che invece mostra la normale richiesta UAC.

### Il collegamento nel menu Start e le notifiche

Le notifiche native di Windows arrivano solo se esiste un collegamento nel menu Start che
dichiari l'`AppUserModelID` del programma (`DiskTempMonitor.Desktop`). L'installer lo scrive
nel collegamento che crea; l'applicazione, che al primo avvio se lo creerebbe da sola nel
menu Start personale, riconosce quello dell'installer e non ne aggiunge un secondo — due voci
identiche nella ricerca di Windows sarebbero solo confusione.

Se si cambia `AppUserModelId` nello script `.iss` va cambiato anche `WindowsToast.AppId`,
altrimenti le notifiche smettono di comparire.

## Disinstallazione

Rimuove i file, i collegamenti, l'attività pianificata, il driver dei sensori estratto
accanto all'eseguibile e l'eventuale servizio rimasto registrato dopo un arresto anomalo.
Impostazioni, cronologia delle autodiagnosi e registro errori in
`%AppData%\DiskTempMonitor` vengono cancellati solo se lo si conferma: rispondendo *No* una
reinstallazione ritrova tutto com'era.

## Aggiornare una versione già installata

Si lancia il nuovo `setup.exe` sopra la vecchia installazione: l'`AppId` è lo stesso, quindi
Windows la sostituisce e in "App installate" resta una voce sola. Se il programma è in
esecuzione viene chiuso prima di sovrascrivere i file, riconosciuto dal mutex di istanza
singola.

**L'`AppId` in `DiskTempMonitor.iss` non va cambiato**: cambiarlo significherebbe installare
in parallelo una seconda copia invece di aggiornare la prima.

## Firma digitale

Il pacchetto non è firmato: al primo avvio Windows SmartScreen mostra "Windows ha protetto il
PC", da cui si prosegue con *Ulteriori informazioni → Esegui comunque*. Con un certificato di
firma del codice l'avviso sparisce; in quel caso si aggiunge a `[Setup]`:

```
SignTool=nome_dello_strumento
SignedUninstaller=yes
```

e si configura lo strumento di firma nelle impostazioni di Inno Setup.
