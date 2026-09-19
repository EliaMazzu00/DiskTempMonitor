; Installer di Disk Temp Monitor — Inno Setup 6.
;
; Non si compila a mano: build.ps1 pubblica l'applicazione in stage\ e passa qui
; versione e cartella sorgente. Vedi installer\LEGGIMI-installer.md.
;
; Il pacchetto è self-contained: porta con sé il runtime .NET, quindi sul computer
; di destinazione non serve installare nulla prima.

#define AppName "Disk Temp Monitor"
#define AppExeName "DiskTempMonitor.exe"
#define AppPublisher "Disk Temp Monitor"
#define AppUrl "https://github.com/"
; Identificativo dichiarato dall'applicazione per le notifiche di Windows: deve
; restare uguale a WindowsToast.AppId, altrimenti i riquadri di notifica spariscono.
#define AppUserModelId "DiskTempMonitor.Desktop"
; Nome dell'attività pianificata e del servizio del driver, entrambi creati dall'app.
#define TaskName "DiskTempMonitor"

#ifndef AppVersion
  #define AppVersion "1.7.1"
#endif
#ifndef SourceDir
  #define SourceDir "stage"
#endif

[Setup]
; Cambiare questo GUID significa perdere il legame con le versioni già installate:
; l'aggiornamento non le sostituirebbe più e resterebbero due voci in "App installate".
AppId={{7B4C2F93-5A61-4E0D-9C8B-2F1D6A3E7C54}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

; L'applicazione legge i comandi S.M.A.R.T. e registra un driver: gira sempre elevata,
; quindi si installa per tutti gli utenti sotto Programmi, non nel profilo di uno solo.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041

; Se l'app è in esecuzione la si chiude prima di sovrascrivere i file, senza chiedere
; un riavvio: il mutex è quello dell'istanza singola dichiarato in Program.cs.
AppMutex=DiskTempMonitor.SingleInstance
CloseApplications=yes
RestartApplications=no

; La voce HKCU qui sotto serve solo a cancellare un avvio automatico configurato
; dalle versioni precedenti: l'avviso del compilatore sulle aree per-utente è atteso.
UsedUserAreasWarning=no

OutputDir=Output
OutputBaseFilename=DiskTempMonitor-{#AppVersion}-setup
SetupIconFile=..\app.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
SetupLogging=yes

[Languages]
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
it.StartWithWindows=Avvia automaticamente all'accesso a Windows (in area di notifica)
en.StartWithWindows=Start automatically when signing in to Windows (in the notification area)
it.StartupGroup=Avvio automatico:
en.StartupGroup=Automatic startup:
it.CreatingTask=Configurazione dell'avvio automatico...
en.CreatingTask=Setting up automatic startup...
it.RemoveSettings=Eliminare anche impostazioni, cronologia delle autodiagnosi e registro errori?%n%n%1%n%nRispondi No se intendi reinstallare il programma.
en.RemoveSettings=Also delete settings, self-test history and error log?%n%n%1%n%nAnswer No if you plan to reinstall the program.
it.ElevationNote=Disk Temp Monitor richiede i privilegi di amministratore per leggere i dati S.M.A.R.T. dei dischi: all'avvio Windows mostrerà la richiesta di conferma. Attivando l'avvio automatico la conferma non viene chiesta al login, perché il programma parte da un'attività pianificata.
en.ElevationNote=Disk Temp Monitor needs administrator rights to read S.M.A.R.T. data: Windows will ask for confirmation each time it starts. With automatic startup enabled no prompt appears at sign-in, because the program is launched by a scheduled task.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "{cm:StartWithWindows}"; GroupDescription: "{cm:StartupGroup}"

[Files]
; L'intera cartella pubblicata: eseguibile, runtime .NET e librerie native.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LEGGIMI.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Il collegamento nel menu Start porta l'AppUserModelID: senza, Windows scarta le
; notifiche dei programmi desktop non pacchettizzati. È lo stesso collegamento che
; l'applicazione creerebbe da sé al primo avvio (vedi Services/WindowsToast.cs).
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; \
    Comment: "Temperature e S.M.A.R.T. dei dischi"; \
    AppUserModelID: "{#AppUserModelId}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; \
    Comment: "Temperature e S.M.A.R.T. dei dischi"; \
    AppUserModelID: "{#AppUserModelId}"; Tasks: desktopicon

[Registry]
; Ripulisce l'avvio automatico vecchia maniera: dalla 1.5 l'app usa l'Utilità di
; pianificazione, perché Windows ignora al login i programmi elevati elencati qui.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueName: "DiskTempMonitor"; Flags: dontcreatekey deletevalue uninsdeletevalue

[Run]
; Avvio automatico come attività pianificata con privilegi massimi: è l'unico modo
; perché un programma elevato parta al login senza richiesta di conferma.
Filename: "{sys}\schtasks.exe"; \
    Parameters: "/Create /F /SC ONLOGON /RL HIGHEST /TN ""{#TaskName}"" /TR ""\""{app}\{#AppExeName}\"" --tray"""; \
    StatusMsg: "{cm:CreatingTask}"; Flags: runhidden waituntilterminated; Tasks: startup
; shellexec, non CreateProcess: le voci postinstall partono con l'utente originale non
; elevato, e CreateProcess non sa elevare da sé un programma che dichiara
; requireAdministrator — fallirebbe con l'errore 740. ShellExecute mostra l'UAC.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
    Flags: shellexec nowait postinstall skipifsilent

[UninstallRun]
; Prima si chiude l'applicazione, poi si smonta quello che ha creato: l'attività
; pianificata e il servizio del driver dei sensori, se è rimasto registrato.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExeName}"; \
    Flags: runhidden; RunOnceId: "ChiudiApp"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /F /TN ""{#TaskName}"""; \
    Flags: runhidden; RunOnceId: "RimuoviAttivita"
Filename: "{sys}\sc.exe"; Parameters: "stop {#TaskName}"; \
    Flags: runhidden; RunOnceId: "FermaDriver"
Filename: "{sys}\sc.exe"; Parameters: "delete {#TaskName}"; \
    Flags: runhidden; RunOnceId: "RimuoviDriver"

[UninstallDelete]
; Il driver dei sensori viene estratto accanto all'eseguibile a ogni avvio: non è
; fra i file installati, quindi va rimosso a parte.
Type: files; Name: "{app}\*.sys"
Type: dirifempty; Name: "{app}"

[Code]
procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectTasks then
    WizardForm.TasksList.Hint := CustomMessage('ElevationNote');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dati: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Dati := ExpandConstant('{userappdata}\DiskTempMonitor');
    if DirExists(Dati) then
      if SuppressibleMsgBox(FmtMessage(CustomMessage('RemoveSettings'), [Dati]),
                            mbConfirmation, MB_YESNO, IDNO) = IDYES then
        DelTree(Dati, True, True, True);
  end;
end;
