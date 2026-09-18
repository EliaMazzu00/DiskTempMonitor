# Disk Temp Monitor

Applicazione Windows Forms (.NET 9) divisa in tre schede:

- **Dischi** — seriale, temperatura e dati S.M.A.R.T. di tutti i dischi collegati (NVMe,
  SSD SATA, HDD 2.5"/3.5", chiavette e box USB), con **icone numeriche in area di
  notifica** in stile Core Temp.
- **Sistema** — temperature e carico per core del processore, scheda video, identità
  dell'hardware (modello, microarchitettura, cache, istruzioni), memoria installata banco
  per banco, scheda madre e BIOS. In stile CPU-Z.
- **Autotest** — stato dell'autodiagnosi di ogni disco, comandi per avviarla e
  interromperla, e cronologia delle esecuzioni.

Non serve installare né tenere aperto nient'altro: l'applicazione parla da sola con i
dischi e si porta dietro il driver che serve a leggere i sensori del processore.

## Avvio

Eseguibile singolo (richiede il .NET 9 Desktop Runtime, già presente su questo PC).
All'avvio Windows chiede la conferma UAC: l'applicazione ha bisogno dei privilegi di
amministratore per interrogare i dischi (vedi *Privilegi* più sotto).

```bash
C:\Users\elia.mazzuchelli\Downloads\DiskTempMonitor\publish\DiskTempMonitor.exe
```

Parametri: `--tray` parte direttamente in area di notifica, `--show` forza l'apertura
della finestra anche se le impostazioni dicono di partire ridotto.

```bash
C:\Users\elia.mazzuchelli\Downloads\DiskTempMonitor\publish\DiskTempMonitor.exe --show
```

## Come legge i dati

Nessuna libreria esterna: l'app parla direttamente con il driver di storage, la stessa
strada usata da CrystalDiskInfo. Le fonti vengono provate in cascata:

| Tipo di disco | Fonte dei dati |
|---|---|
| NVMe | `IOCTL_STORAGE_QUERY_PROPERTY` → Identify Controller (seriale/modello/firmware) e **SMART/Health log page 02h** (temperatura composita, 8 sensori, usura, ore, cicli, errori) |
| SATA / ATA | Tre canali provati in ordine: `IOCTL_ATA_PASS_THROUGH`, il vecchio `SMART_RCV_DRIVE_DATA`, e l'incapsulamento SCSI. Da ciascuno: **IDENTIFY DEVICE** (seriale, firmware, RPM, velocità SATA) e **READ ATTRIBUTES / THRESHOLDS** (30 attributi con valore, peggiore, soglia e raw) |
| USB con dentro un SATA | `IOCTL_SCSI_PASS_THROUGH_DIRECT` → **ATA PASS-THROUGH** 16 byte, lo stesso con `ck_cond`, poi 12 byte (standard SAT) |
| USB con dentro un NVMe | Comandi NVMe nel dialetto del ponte (oggi i **Realtek RTL9210/9220**): Identify Controller e log page SMART/Health |
| Qualsiasi SCSI/USB | **LOG SENSE** pagine `0Dh` (Temperature) e `2Fh` (Informational Exceptions) |
| Qualsiasi | `StorageDeviceTemperatureProperty` — sensori esposti dal driver, usato come ultima risorsa |

Capacità, settori logico/fisico, tipo di supporto e lettere di unità arrivano da
`IOCTL_DISK_GET_LENGTH_INFO` / `IOCTL_DISK_GET_DRIVE_GEOMETRY_EX`,
`StorageAccessAlignmentProperty`, `StorageDeviceSeekPenaltyProperty` e
`IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`.

Il canale che ha funzionato viene ricordato disco per disco: gli aggiornamenti periodici
lo riusano invece di riprovarli tutti, e ogni tentativo a vuoto costerebbe un timeout.
*Rileva dischi* azzera la memoria e riprova tutto da capo.

### Enclosure USB esterne

Un box esterno non espone il canale S.M.A.R.T. classico: mostra a Windows un disco SCSI,
e cosa c'è dietro dipende dal ponte.

- **Ponte USB→SATA.** Dentro c'è un normale disco SATA. I comandi ATA vengono incapsulati
  in un CDB **ATA PASS-THROUGH** (standard SAT), che il ponte traduce. Si provano nell'ordine
  la variante a 16 byte, la stessa con `ck_cond` attivo e infine quella a 12 byte, perché i
  ponti non concordano. Lo stato SCSI da solo non basta a decidere se ha funzionato: parecchi
  ponti rispondono CHECK CONDITION pur avendo consegnato i dati giusti, quindi si guarda se
  il settore restituito ha davvero la forma di una tabella di attributi.
- **Ponte USB→NVMe.** Dentro c'è un SSD NVMe, e il ponte non sa nulla di ATA: il
  pass-through SAT non può funzionare per definizione. Serve il protocollo del ponte. È
  implementato quello dei **Realtek RTL9210/9210B/9210C**, nella forma documentata da
  smartmontools (`-d sntrealtek`), e viene provato solo sui dispositivi che si dichiarano
  Realtek — lo stesso codice operativo su un ponte di un'altra marca vorrebbe dire altro.
  Passano di lì due soli comandi, entrambi di sola lettura: Identify Controller e la log
  page SMART/Health.
- **Ponte che non inoltra niente.** Restano le pagine di log SCSI standard, che danno
  almeno la temperatura, e in ultima istanza modello e capacità senza sensori.

Quando i dati arrivano per una di queste strade, il campo *Origine dati* lo indica.

### Il comando di autodiagnosi NVMe, e perché veniva respinto

`IOCTL_STORAGE_PROTOCOL_COMMAND` è pignolo su due punti, e sbagliarne uno solo fa fallire
tutto. Sono stati verificati inviando il codice `0Fh` (*interrompi*), che su un disco senza
diagnosi in corso non fa niente ed è quindi innocuo:

| Forma del comando | Esito reale |
|---|---|
| `Length` = 80, nessuna area per le informazioni di errore | **respinto**, errore di sistema 87 (parametro non valido) |
| `Length` = 84, con `ErrorInfoLength` = 64 e `ErrorInfoOffset` valorizzati | **accettato** |

`Length` non è la dimensione del buffer né quella dell'intestazione: è quanto misura la
struttura per il compilatore C, cioè 80 byte di campi più il primo byte del comando,
arrotondato a 84. E l'area per le informazioni di errore va dichiarata anche quando non
interessa leggerla.

Il secondo punto è la lettura dell'esito: in `STORAGE_PROTOCOL_STATUS` lo **zero significa
"in attesa"** e **l'uno "riuscito"**. Trattare il diverso-da-zero come errore, che è
l'istinto, faceva dichiarare fallito ogni comando andato a buon fine.

### Privilegi

**L'applicazione si avvia sempre come amministratore** (`requireAdministrator` nel
manifest): senza, i dischi SATA e quelli nei box USB non mostrerebbero né temperatura né
attributi, perché i comandi S.M.A.R.T. verso il driver richiedono privilegi elevati.

Conseguenza sull'avvio automatico: Windows **non** avvia al login i programmi elevati
elencati nella chiave `Run` del registro, quindi si usa l'Utilità di pianificazione con
privilegi massimi. Una configurazione preesistente nella chiave `Run` viene spostata
automaticamente al primo avvio.

## Disposizione

```
+---------------------------+-------------------------------+
| Attributi S.M.A.R.T.      | Dettagli del disco            |
| (barra laterale,          +-------------------------------+
|  riducibile a linguetta)  | Andamento temperatura         |
+---------------------------+-------------------------------+
```

La barra laterale si riduce o si espande con un clic sulla sua intestazione, con il
pulsante *Attributi* o con **F9**; il bordo fra le due zone si trascina per cambiarne la
larghezza. Da ridotta resta una linguetta con il titolo in verticale.

### Scorciatoie

| Tasto | Azione |
|---|---|
| **F5** | Rilegge temperatura e attributi dei dischi già noti |
| **F6** | Rienumera i dischi fisici collegati al PC |
| **F9** | Mostra o riduce la barra laterale degli attributi |
| **F4** | Apre le impostazioni |
| **F3** | Apre il menu di esportazione |
| **F8** | Riquadro compatto sempre in primo piano |
| **F2** | Passa fra tema chiaro, scuro e automatico |
| **Esc** | Nasconde la finestra in area di notifica |

Le prime due corrispondono ai pulsanti *Aggiorna* e *Rileva dischi* della barra strumenti.
La rienumerazione avviene comunque da sola quando si collega o si scollega un disco.

## Finestra principale

- **Tema chiaro/scuro**, automatico in base a Windows oppure forzato dalle impostazioni
  (anche col pulsante *Tema* nella barra strumenti). Barra del titolo e barre di
  scorrimento seguono il tema.

  Le barre di scorrimento **non sono quelle di sistema**: sono disegnate dall'applicazione.
  Quella nativa non si lascia colorare, e in tema chiaro Windows la disegna `#F0F0F0`,
  quasi bianca: dentro una scheda attenuata come questa appariva come una striscia
  luminosa incollata sopra, e faceva sembrare la barra laterale un corpo estraneo. Quella
  disegnata prende come binario il colore della scheda che la ospita, e sparisce.

  Il tema chiaro non usa **né il bianco pieno né il nero quasi pieno**: le superfici sono
  un avorio appena spento e il testo un grigio ardesia. Un monitor luminoso rende quella
  combinazione faticosa già dopo pochi minuti. Il contrasto del testo si ferma a circa
  **6:1**: basso abbastanza da non stancare, ben oltre il 4,5:1 che si considera il minimo
  leggibile. Anche verde, rosso, blu e
  ambra sono nelle versioni smorzate, tutte verificate sopra il minimo.

  I colori delle soglie di temperatura li sceglie l'utente, e servono accesi per le icone
  in area di notifica, che devono reggere su una barra delle applicazioni di tinta ignota.
  Dentro la finestra vengono perciò attenuati verso il colore del testo (`Theme.OnSurface`)
  quando il tema è chiaro, e lasciati intatti quando è scuro: stessa informazione, senza
  che il verde acceso strida sul fondo chiaro.
- **Scheda per ogni disco**: indicatore circolare della temperatura, pillola dello stato
  di salute, vita residua, modello, interfaccia e barra di occupazione dei volumi
  (arancione oltre l'80%, rossa oltre il 92%).
- **Dispositivi USB muti esclusi**: chiavette e lettori di schede non hanno né sensore di
  temperatura né S.M.A.R.T., e nell'elenco sarebbero una riga di trattini. Un dispositivo
  USB da cui non arriva **nessun** dato viene perciò tolto dall'elenco, e la barra di stato
  dice quanti ne sono stati nascosti, perché un disco che sparisce senza spiegazione
  sembra un difetto. Si riattivano da `Impostazioni → Aggiornamento e aspetto →
  Dispositivi USB`. Attenzione: la regola vale anche per un SSD esterno il cui ponte non
  risponde — se quello che cerchi non c'è, togli la spunta e ricompare.
- **Ordine delle schede**: prima i dischi da cui arriva davvero una misura, poi gli altri;
  dentro ciascun gruppo si va per lettera di unità, e i dischi senza lettera chiudono la
  fila nell'ordine in cui li numera Windows. Lo stesso ordine vale nel riquadro compatto e
  nelle icone in area di notifica.
- **Dettagli su due colonne**: modello, **numero di serie** (clic per copiarlo), firmware,
  interfaccia, capacità, spazio occupato, settori, temperatura con le soglie del firmware,
  min/max di sessione, ore di accensione, accensioni, totale letto/scritto. I valori
  troncati restano leggibili per intero nel tooltip.
- **Grafico dell'andamento** della temperatura nella sessione, con area sfumata, linee
  tratteggiate delle soglie e minimo/massimo. Il pulsante **Svuota** nella sua
  intestazione azzera lo storico del disco selezionato e fa ripartire minimo e massimo
  dalla lettura corrente.
- **Tabella S.M.A.R.T.** con pallino di stato per riga e righe evidenziate quando un
  attributo si avvicina o supera la soglia. Per gli NVMe le colonne dei valori
  normalizzati, che non esistono, vengono nascoste.
- **Pulsante di copia** in alto a destra di ogni scheda: mette negli appunti il riepilogo
  di quel disco, lo stesso testo dell'esportazione. L'icona diventa una spunta per
  qualche secondo a conferma.
- *Esporta* apre un menu:
  - **Riepilogo** ▸ una voce per ogni disco collegato, più *Tutti i dischi*. Contiene solo
    i dati principali (modello, seriale, firmware, interfaccia, capacità e occupazione,
    temperatura con min/max di sessione, salute, vita residua, ore, accensioni, totale
    letto e scritto). Nessuna informazione sul computer, nessuna tabella di attributi:
    comodo da incollare in un messaggio.
  - **Rapporto completo** — in più il contesto del computer (nome, sistema, privilegi) e
    la tabella integrale degli attributi S.M.A.R.T.

  Entrambi producono un file di testo. Se ti servisse un formato per foglio di calcolo
  (CSV), non c'è: si può aggiungere.

- **Autodiagnosi**: ha una scheda tutta sua, *Autotest* (vedi sotto).
- **Riquadro compatto** (pulsante *Riquadro* o **F8**): vedi sotto.

L'aggiornamento periodico riusa i controlli già creati invece di ricostruirli, così i
valori cambiano senza sfarfallio.

### Indicatore di lettura

Mentre i dischi vengono letti, la scheda *Dischi* lascia il posto a un anello che gira,
col motivo dell'attesa: finché la lettura non è finita non si vedono valori vecchi
spacciati per attuali. Le due situazioni sono trattate diversamente:

| Operazione | Indicatore |
|---|---|
| Rienumerazione (*Rileva dischi*, **F6**, avvio, disco collegato) | compare **subito**, e resta almeno mezzo secondo |
| Aggiornamento periodico | compare solo se la lettura supera 200 ms |

La distinzione serve perché un aggiornamento periodico dura una manciata di millisecondi:
mostrarlo comunque farebbe lampeggiare la pagina a ogni giro. Se invece un disco si
impianta, l'indicatore compare e si capisce perché l'applicazione sta aspettando.

L'indicatore copre solo la scheda *Dischi*: passando a *Sistema* durante una lettura si
continua a consultare processore, memoria e scheda video senza interruzioni.

> **Attenzione alla riga delle schede quando è nascosta.** La striscia dei riquadri dei
> dischi vive in una riga del contenitore principale, la cui altezza va portata a zero
> quando quella scheda non è in primo piano. Il calcolo dell'altezza viene però invocato
> anche dal ridimensionamento della finestra e dalla ricostruzione dei riquadri: senza un
> controllo su quale scheda sia attiva, la riga si riapriva a ogni fine lettura e
> spingeva in basso tutti i riquadri della scheda *Sistema*.

### La barra di stato

A sinistra il riepilogo, a destra versione e privilegi. Mentre l'applicazione legge, il
testo **non cambia**: si accende un piccolo anello che gira accanto al riepilogo, e basta.
Sostituire la riga a ogni giro — come si faceva — la rendeva illeggibile proprio quando
serviva, e per giunta non diceva niente di utile: che stia leggendo si capisce dal
segnalino, e il riepilogo di prima resta valido fino al giro dopo.

Per lo stesso motivo il riepilogo periodico **non riporta l'ora dell'ultimo
aggiornamento**: cambiando ogni secondo faceva ridisegnare la riga di continuo, e quel
tremolio bastava a renderla fastidiosa. Senza, il testo che si assegna è identico a quello
già presente, e WinForms non ridisegna proprio niente. L'ora la porta invece la
rienumerazione, che capita di rado ed è un fatto che vale la pena datare.

Le due etichette non sono `Label` di WinForms ma controlli disegnati in doppio buffer
(`StatusLabel`): la Label di sistema ridipinge direttamente sullo schermo a ogni cambio di
testo, e su una riga viva si vede sfarfallare.

Misurato con intervallo di aggiornamento a un secondo, su 18 secondi: il testo cambia
**due volte** — il messaggio di avvio e poi il riepilogo — invece di una volta al secondo.

I messaggi delle azioni dell'utente — *rapporto salvato*, *riepilogo copiato*, *storico
azzerato* — restano visibili **otto secondi** prima che il riepilogo si riprenda la riga.
Senza quell'attesa, con l'intervallo di aggiornamento a un secondo, sparivano prima di
poterli leggere.

### Quale copia sto guardando

In fondo a destra, nella barra di stato, compare la **versione**: `v1.1.0 · Amministratore`.
L'ora di compilazione lì non si vede — ingombrava — ma resta nel **suggerimento del mouse**
e nell'intestazione dei rapporti esportati.

L'ora la incide il progetto nella versione
informativa dell'assembly a ogni compilazione (destinazione `MarcaOraDiCompilazione` nel
`.csproj`), quindi cambia davvero a ogni build. Il suggerimento del mouse mostra anche il
percorso dell'eseguibile in esecuzione: serve perché dello stesso programma esistono
facilmente due copie — quella in `bin\Release\` e quella in `publish\` — che possono
essere di due momenti diversi.

### Perché la scheda Dischi restava in rilevamento perpetuo

Tenendo aperto il riquadro compatto, la scheda *Dischi* finiva per non mostrarsi quasi
più: appena una lettura finiva ne partiva un'altra, e l'indicatore non se ne andava mai.
Le cause erano due, sommate.

**La coda.** Ogni richiesta arrivata durante una lettura veniva messa in coda. È giusto
per un clic dell'utente — un comando non va perso — ma applicarlo anche al giro automatico
dell'orologio produce una valanga: se il ciclo dura più dell'intervallo, la coda non si
svuota più. Ora una richiesta si accoda, un giro automatico che trova la casa occupata
**si lascia cadere**.

**L'indicatore troppo zelante.** Compariva anche per gli aggiornamenti periodici, dopo una
breve attesa. Ma un aggiornamento periodico aggiorna i valori dove sono, senza toccare
l'elenco dei dischi: coprire la pagina non protegge da niente. E con l'intervallo a un
secondo, tre dischi da interrogare (uno dei quali dietro un ponte USB) e i sensori di
processore e scheda video che servono al riquadro, il ciclo supera comodamente quella
attesa: la pagina passava più tempo coperta che scoperta.

Adesso l'indicatore compare **solo per una rienumerazione**, che è l'unico caso in cui
l'elenco può cambiare sotto. Misurato col riquadro aperto e intervallo a un secondo, su 25
secondi di osservazione: dopo la scansione iniziale la pagina non viene più coperta,
nessuna volta su 157 campioni.

### Perché *Rileva dischi* poteva sembrare inerte

Un aggiornamento in corso non veniva mai interrotto, e ogni richiesta che arrivava nel
frattempo veniva semplicemente scartata: bastava un giro di lettura lento perché il clic
sul pulsante non facesse niente, e un disco appena collegato comparisse solo riavviando
l'applicazione. I giri lenti erano la norma, per tre motivi tutti misurabili:

- un'unità di rete non raggiungibile costava **9 secondi** a ogni scansione, perché
  l'elenco dei volumi la interrogava come tutte le altre. Ora si guardano solo i volumi
  che possono stare su un disco fisico (`Fixed` e `Removable`);
- rileggere la geometria di certe chiavette USB costa **7 secondi**. Adesso l'aggiornamento
  periodico non ripete le letture di ciò che non cambia — capacità, settori, tipo,
  identità — e riparte direttamente dagli attributi;
- i dischi vengono interrogati **in parallelo**, così il più lento non fa aspettare gli altri.

> **"Ciò che non cambia" va deciso con prudenza.** La capacità è archiviata sotto
> l'identità che il dispositivo dichiara — modello e numero di serie — ed è un'etichetta
> che regge per un disco, non per un **box USB**: il box dichiara sé stesso, e cambiando
> l'SSD che ha dentro modello e seriale restano identici. Risultato: un SSD da 256 GB
> messo al posto di uno da 512 continuava a essere annunciato come da 512, e nemmeno
> *Rileva dischi* lo correggeva, perché trovava l'etichetta già nota e si fermava lì.
> Ora la rienumerazione completa — quella del pulsante, dell'avvio e del collegamento di
> un dispositivo — riparte davvero da capo e rilegge anche la geometria: è l'unico
> momento in cui si può scoprire che dietro la stessa etichetta c'è un disco diverso. Gli
> aggiornamenti periodici continuano a ricopiare dal giro precedente, e restano gratuiti.

Sulla macchina di prova la scansione completa è passata da ~10 s a ~1,7 s e
l'aggiornamento periodico da ~10 s a **7 ms**. Le richieste che arrivano durante una
lettura vengono comunque messe in coda ed eseguite subito dopo, e la barra di stato dice
che la rienumerazione è in corso.

## Scheda Sistema

| Riquadro | Contenuto | Come viene letto |
|---|---|---|
| Processore | Temperatura e carico per core, più caldo, media, margine dal limite termico, frequenza e potenza | Driver di lettura dei registri **incorporato nell'applicazione**; Core Temp solo come riserva |
| Identità del processore | Modello, microarchitettura, famiglia/modello/stepping, core fisici e thread, frequenza di base, cache L1/L2/L3 con vie e dimensione linea, istruzioni supportate | Istruzione **CPUID** e `GetLogicalProcessorInformationEx` |
| Scheda video | Modello, produttore, memoria dedicata, driver, temperatura del core, punto più caldo, carico, frequenze, memoria in uso (in GB **e in percentuale**, colorata sulle soglie di carico), potenza e ventola | **LibreHardwareMonitor** (librerie NVIDIA/AMD/Intel) e registro degli adattatori |
| Memoria | Totale, in uso, disponibile, e ogni banco con capacità, tipo, velocità, produttore e sigla | `GlobalMemoryStatusEx` e tabelle **SMBIOS** (`GetSystemFirmwareTable`) |
| Scheda madre e BIOS | Produttore e modello della scheda, versione e data del BIOS, versione di Windows | Registro `HARDWARE\DESCRIPTION\System\BIOS` |

La **griglia dei core prende tutta la larghezza**, perché è quella che ha bisogno di
spazio: ventiquattro core si dispongono su quattro colonne invece che su due, e occupano
la metà delle righe. Le altre quattro schede stanno a coppie sotto, e quella della scheda
video mette i suoi valori — tutti corti — su due colonne. Ogni riga è alta quanto serve al
proprio contenuto, calcolata sul numero di righe: prima l'altezza era fissa, e l'ultima
voce della scheda madre (il nome del computer) restava tagliata fuori.

Il risultato sta in una finestra da 1200×880 senza bisogno di scorrere; se lo spazio non
basta il contenuto scorre comunque, con la barra di scorrimento nei colori del tema.

Ogni core è colorato secondo le **soglie impostate per il processore**
(`Impostazioni → Soglie di processore e scheda video`), le stesse che valgono per l'icona
in area di notifica: un valore che lì è rosso è rosso anche qui. Anche il riempimento della
barra segue la stessa scala, da 20 °C alla soglia critica.

Quando il processore si avvicina al limite termico (meno di 5 °C di margine, o la soglia
critica impostata se il limite non è noto) arriva una notifica, come per la scheda video
oltre la sua soglia critica. Valgono le stesse preferenze degli avvisi sui dischi.

### Da dove arrivano le temperature del processore

Le temperature dei core stanno in registri interni alla CPU (gli *MSR*), che solo il
kernel può leggere: **nessun programma normale può accedervi**, serve per forza un driver
che faccia da tramite.

L'applicazione **se lo porta dietro**: non dipende da nessun programma esterno. Il driver
è incorporato come risorsa in LibreHardwareMonitor, che lo estrae, lo registra come
servizio all'avvio e lo rimuove alla chiusura. Per questo serve l'esecuzione come
amministratore, che l'applicazione richiede comunque per i dischi.

> **Perché la libreria è ferma alla 0.9.4.** Dalla 0.9.5 il driver non è più incluso: la
> libreria si appoggia a **PawnIO**, che l'utente dovrebbe scaricare e installare a parte.
> Con la 0.9.6 su questo PC tutte le temperature tornavano nulle anche da amministratore,
> con l'Isolamento core perfino disattivato — non era un difetto, era il driver che
> semplicemente non c'era. La 0.9.4 legge 51 sensori su 51.

Restano due situazioni in cui la lettura può fallire, e in entrambe l'applicazione lo dice
invece di mostrare valori vuoti senza spiegazione:

- **Isolamento core** di Windows attivo (*Sicurezza di Windows → Sicurezza del
  dispositivo*): blocca per principio i driver di questo tipo, che danno accesso diretto
  alle porte e ai registri della macchina.
- un **antivirus** che consideri sospetto lo stesso driver.

In quei casi vale ancora la riserva: se **Core Temp** risulta installato, l'intestazione
della scheda *Processore* mostra il pulsante **Avvia Core Temp**, e i valori arrivano dalla
memoria condivisa che quel programma pubblica. È una riserva, non un requisito: la riga
*Origine dati* nell'intestazione dice sempre da dove arrivano i numeri che stai guardando.

> Sui processori ibridi Intel i nomi dei sensori non coincidono fra loro: temperature e
> frequenze arrivano come `P-Core #n` ed `E-Core #n`, mentre i carichi restano numerati di
> seguito come `CPU Core #n`, con una voce per thread. Abbinarli per numero, come si
> faceva, accavallava `P-Core #1` ed `E-Core #1` sulla stessa riga: l'elenco dei core viene
> perciò costruito dalle temperature — prima i core ad alte prestazioni, poi quelli a basso
> consumo — e accoppiato posizionalmente con i carichi.

## Scheda Autotest

Una riga per disco con lo stato della sua autodiagnosi e i comandi, e sotto l'elenco delle
esecuzioni registrate.

**Quello che si può fare lo decide il dispositivo, non l'applicazione.** Vale la pena
essere espliciti, perché le aspettative sono ragionevoli ma il firmware non le soddisfa
tutte:

| Azione | Possibile? |
|---|---|
| Avviare una verifica **breve** o **estesa** | sì, è il comando standard |
| **Interrompere** quella in corso | sì: NVMe con codice `0Fh`, ATA con sottocomando `7Fh` |
| **Mettere in pausa** | **no**: non esiste in nessuno dei due standard, il test si interrompe e basta |
| **Cancellare** il registro del disco | **no**: il disco tiene l'esito dell'ultima verifica e non si azzera |
| Vedere l'**esito** | sì, quello dell'ultima secondo il disco, più la cronologia tenuta qui |

Il disco, per conto suo, ricorda soltanto l'esito dell'ultima verifica: non quando sia
stata avviata, quanto sia durata, né se l'abbia interrotta l'utente. Quelle cose esistono
solo se le annota l'applicazione, e stanno in `%AppData%\DiskTempMonitorutotest.json`.
Per questo il pulsante **Svuota** cancella la cronologia di qui, non il registro del disco,
e lo dice prima di farlo.

Mentre una verifica è in corso la riga mostra l'avanzamento in percentuale, chiesto al
disco ogni quattro secondi; al termine arriva la notifica e la riga passa all'esito. Una
diagnosi che risultava in corso quando l'applicazione viene chiusa non è più seguibile:
alla riapertura viene chiusa come *esito non osservato*, invece di restare appesa per
sempre.

## Riquadro compatto

Una finestrella sempre in primo piano da tenere in un angolo dello schermo, con le sole
misure. Si apre col pulsante *Riquadro* o con **F8**.

Ogni riga mostra il pallino dello stato, la sigla, l'andamento recente in miniatura e il
valore, colorato secondo le soglie. Non ha intestazione fissa: i comandi compaiono solo al
passaggio del mouse, così a riposo resta una striscia di soli dati.

Oltre ai dischi può mostrare, a scelta:

Le righe seguono sempre quest'ordine: prima le temperature, che sono il motivo per cui il
riquadro esiste, poi le percentuali di carico.

| Riga | Cosa mostra |
|---|---|
| *(dischi)* | Una riga per disco, con l'andamento recente |
| CPU | Core più caldo, media dei core o package, a scelta |
| GPU | Core della scheda video, o il punto più caldo se è l'unico esposto |
| CPU % | Carico del processore, come barra invece del micro-grafico |
| GPU % | Carico della scheda video |
| VRAM % | Memoria della scheda video occupata |
| RAM % | Memoria fisica occupata |
| *(griglia dei core)* | In fondo, una casella per core: bordo e numero dicono la temperatura, il riempimento dal basso il carico |

Il riquadro si ridimensiona da solo secondo cosa è acceso.

- **Trascinala** ovunque col mouse: ricorda la posizione fra un avvio e l'altro, e se lo
  schermo cambia si risistema da sola dentro l'area visibile.
- **Doppio clic** riapre la finestra intera, **Esc** o la ✕ chiudono il riquadro.
- **Clic destro** apre un menu con *Cosa mostrare*, la modalità della temperatura del
  processore, *Vista compatta* (nasconde il micro-grafico e stringe il riquadro) e
  *Trasparenza* (dal 55% al 100%). Le stesse voci stanno in `Impostazioni → Riquadro
  compatto`.

> Gli angoli arrotondati li disegna il compositore di Windows 11
> (`DWMWA_WINDOW_CORNER_PREFERENCE`), non un ritaglio manuale della finestra: una
> `Region` non ha antialiasing, lascia il bordo scalettato e soprattutto scopre il colore
> di fondo della finestra, che senza un `BackColor` esplicito è il grigio chiaro
> predefinito di Windows e appare come un contorno biancastro. Il colore del bordo si
> imposta con `DWMWA_BORDER_COLOR`. Sulle versioni che non espongono questi attributi si
> torna alla regione ritagliata, con `BackColor` coerente col tema.

La percentuale di memoria non passa dalla libreria dei sensori: è `GlobalMemoryStatusEx`,
una sola chiamata di sistema che costa niente e funziona sempre, anche quando il driver di
lettura dei registri non si carica. Si aggiorna a ogni giro come le altre righe.

## Notifiche

Usa le **notifiche native di Windows**, quelle che restano nel Centro notifiche. Perché
funzionino da un'applicazione non pacchettizzata, Windows deve sapere a chi attribuirle:
al primo avvio viene creato un collegamento nel menu Start che dichiara l'identificativo
dell'app. Se qualcosa non va, si ripiega sul fumetto dell'area di notifica.

La prima voce, **Mostra le notifiche**, è l'interruttore generale: da spento non parte
nessun avviso, di nessun tipo, e le scelte sottostanti si disattivano per far vedere che
non contano più. Serve a zittire tutto senza dover spegnere una voce alla volta.

Si può poi scegliere cosa segnalare (`Impostazioni → Notifiche`):

| Avviso | Quando |
|---|---|
| Temperatura | Superata la soglia critica |
| Spazio disco | Occupazione oltre la percentuale impostata (predefinita 90%) |
| Salute | Lo stato del disco peggiora rispetto alla lettura precedente |
| Autotest | Al termine di un'autodiagnosi |

Un intervallo minimo configurabile evita che lo stesso avviso si ripeta di continuo. Il
pulsante **Prova** manda subito una notifica di esempio.

## Sovrimpressione di gioco

Una targhetta sopra al gioco con **FPS**, temperature e carichi — le stesse misure del
riquadro compatto, più i fotogrammi al secondo. Si accende da `Impostazioni →
Sovrimpressione di gioco`. La combinazione da tastiera (predefinita `Ctrl+Alt+F9`) la
accende e la spegne ovunque ci si trovi, gioco compreso.

| Impostazione | Cosa fa |
|---|---|
| Solo quando si leggono gli FPS | Compare mentre un programma sta davvero disegnando e sparisce quando si torna al desktop |
| Disposizione | **Verticale**, un valore per riga: colonna stretta e alta, da tenere lungo un bordo. **Orizzontale**, tre per riga: striscia bassa e larga, da tenere sopra o sotto |
| Posizione | In quale dei quattro angoli agganciarla |
| Scostamento | Ritocco fine in pixel rispetto a quell'angolo, positivo verso destra e verso il basso |
| Sfondo / dimensione | Quanto è coprente lo sfondo e quanto è grande il testo |

Lo scostamento serve a scansare quello che il gioco disegna proprio in quell'angolo — una
minimappa, una barra della vita, l'orologio di un'altra sovrimpressione — senza dover
rinunciare all'angolo che si preferisce.

### La posizione si sceglie guardandola

Il pulsante *Posiziona...* apre una finestra a sé: lo schermo in miniatura, in
proporzione vera, con la targhetta al suo posto da **trascinare** dove la si vuole, e
sotto la stessa targhetta a **grandezza naturale** per giudicarne la leggibilità. Angolo,
disposizione, scostamento, sfondo e dimensione si regolano lì e si vedono subito.

Regolare la posizione a numeri significava chiudere le impostazioni, entrare in partita,
guardare, uscire e ricominciare.

Due dettagli che rendono l'anteprima affidabile invece che somigliante:

- **È la targhetta vera.** La disegna lo stesso codice che la disegna in partita, non un
  disegno rifatto per l'occasione: due disegni diversi finirebbero per divergere, e
  l'anteprima prometterebbe qualcosa che poi non si vede.
- **È lo stesso conto di posizione.** Angolo più scostamento si calcolano come li calcola
  la sovrimpressione quando si mette a posto da sola.

Trascinando si cambia lo scostamento, non l'angolo. Cambiando angolo lo scostamento torna
a zero: era riferito a un altro punto di partenza, e tenerlo lascerebbe la targhetta in un
posto che nessuno ha scelto.

> **Senza FPS non compare.** È una scelta, non un limite: una targhetta di sole temperature
> sopra al desktop è esattamente quello che fa già il riquadro compatto, e averne due
> uguali non aiuta nessuno. Quindi finché non c'è un numero di fotogrammi da mostrare la
> targhetta resta via, anche subito dopo averla accesa da tastiera: comparirà da sola
> appena un gioco comincia a disegnare. Chi la vuole sempre visibile toglie la spunta a
> *Solo quando si leggono gli FPS*.

Il nome del programma non si mostra: chi sta giocando sa a cosa sta giocando, e quella
riga rubava spazio ai numeri.

### La combinazione da tastiera si preme, non si sceglie da un elenco

Il campo *Accendi e spegni con* si comanda premendo i tasti: clic, e poi la combinazione
che si vuole — serve almeno un tasto fra Ctrl, Alt e Maiusc, perché una scorciatoia di un
tasto solo lo ruberebbe a tutto il sistema. Esc annulla, Canc la toglie. Accanto al campo
compare subito se quella combinazione è **libera** o **occupata**.

> **Perché quel controllo c'è.** La prima versione proponeva un elenco di combinazioni
> pronte, con `Ctrl+Alt+O` come predefinita. Durante le prove la combinazione smise di
> funzionare, e una traccia temporanea mostrò che `RegisterHotKey` falliva in partenza
> con l'errore **1409**, *hotkey già registrata*: Windows non lascia strappare a nessuno
> una combinazione già assegnata.
>
> A tenerla occupata era, a conti fatti, un processo di prova rimasto in esecuzione — non
> un programma dell'utente. Ma il difetto che quell'episodio ha scoperto è reale e non
> dipende da chi tiene la combinazione: **il fallimento restava muto**, e da fuori era
> indistinguibile da un difetto del programma. Un elenco di combinazioni pronte, poi, è
> comodo solo finché sono libere: driver video, programmi di registrazione e la barra di
> gioco di Windows se ne prendono parecchie, e quali siano cambia da macchina a macchina.
> Adesso si preme quello che si vuole, si vede subito se è libera, e se la registrazione
> fallisce lo dice anche la barra di stato.

### Come sta sopra al gioco senza entrarci

È una normale finestra di Windows, solo dichiarata come strato trasparente
(`WS_EX_LAYERED`), fuori dalla catena dei clic (`WS_EX_TRANSPARENT`), che non prende mai
il fuoco (`WS_EX_NOACTIVATE`) e non compare con Alt+Tab (`WS_EX_TOOLWINDOW`).

**Non inietta niente nel gioco e non si aggancia alle sue funzioni di disegno**, che è il
modo in cui lavorano gli strumenti più noti del genere: dal punto di vista del gioco — e
del suo anticheat — questo programma non esiste. Il prezzo di questa scelta è uno solo:
funziona sopra ai giochi **a finestra** e **a finestra senza bordi**, che oggi sono la
quasi totalità, ma non sopra a quelli in **schermo intero esclusivo**, dove Windows non
lascia comparire nessuna finestra di nessuno.

Il disegno passa da `UpdateLayeredWindow` invece che dal normale ciclo di ridisegno: così
ogni pixel ha la sua trasparenza e si ottiene lo sfondo scuro translucido con sopra il
testo pieno. Con la trasparenza di finestra, che vale per tutto allo stesso modo, anche le
cifre sarebbero sbiadite.

Il contorno è **più scuro** dello sfondo, non più chiaro: era una riga bianca a bassa
opacità, invisibile sul chiaro ma evidente come un filo bianco tutto intorno appena il
gioco diventava scuro.

> **Due trabocchetti, tutti e due misurati.** `UpdateLayeredWindow` **sposta** la finestra
> alle coordinate che gli si passano: passandogli (0,0) — come verrebbe da fare pensando
> che sia l'origine del disegno — la targhetta tornava nell'angolo in alto a sinistra a
> ogni aggiornamento, qualunque angolo fosse stato scelto. E lo schermo su cui posarsi è
> quello della finestra in primo piano, non quello dove sta il puntatore: durante una
> partita il mouse può essere fermo da mezz'ora su un secondo monitor.

> **Un difetto che valeva la pena raccontare.** Alla prima prova la casella *Attiva* non
> restava spuntata: si chiudeva la finestra e la sovrimpressione era di nuovo spenta. La
> finestra delle impostazioni lavora su una copia e, alla conferma, la riversa in quella
> vera — e lo faceva **voce per voce, con un elenco scritto a mano**. Alla lista mancavano
> le voci nuove, che quindi si lasciavano configurare e poi tornavano com'erano senza
> dire niente: la sovrimpressione, le percentuali di VRAM e la scelta di quali dischi
> mostrare in area di notifica. Ora la copia va per riflessione, su tutte le impostazioni
> che esistono, con un breve elenco di eccezioni per quelle che la finestra non mostra e
> che cambiano per conto loro (posizione e stato del riquadro compatto). Un elenco da
> tenere aggiornato a mano è una trappola che scatta sempre: meglio non averlo.

### Come si contano gli FPS

Windows annuncia su **ETW** — lo stesso canale da cui legge PresentMon — ogni volta che un
programma consegna un fotogramma alla scheda video. Qui ci si limita ad ascoltare e
contare: nessuna libreria da iniettare, nessuna funzione da agganciare. Serve
l'esecuzione come amministratore, che l'applicazione ha già per leggere i sensori.

Si ascolta il **driver di visualizzazione** (`Microsoft-Windows-DxgKrnl`, evento 184) e
non i provider di Direct3D, perché dal driver passa *tutto*: Direct3D 9, 11 e 12, ma anche
OpenGL e Vulkan.

> **Perché non bastava Direct3D.** La prima versione ascoltava `Microsoft-Windows-DXGI`,
> che copre Direct3D 11 e 12 con pochi eventi al secondo. Alla prova sul campo, sopra a un
> gioco in OpenGL, la targhetta mostrava tutte le temperature e un trattino al posto degli
> FPS: da DXGI non arrivava un solo evento. L'evento 184 del driver lo seguiva invece
> fotogramma per fotogramma, 439 al secondo.

Il prezzo è il volume: la parola chiave che porta quell'evento ne porta con sé molti altri
— una cinquantina di migliaia al secondo con un gioco in corso. Provate una per una, non
ce n'è una più stretta che lo consegni lo stesso. Per questo la funzione parte spenta, e
la funzione che riceve gli eventi fa il minimo: legge **due byte** per capire se l'evento
la riguarda e, quando non la riguarda, torna indietro senza toccare altro.

Della finestra in primo piano si guarda il processo: se sta disegnando, i suoi FPS sono
quelli da mostrare. Il valore si calcola su mezzo secondo, ma finché quella prima mezza
finestra non si è chiusa se ne dà una stima sui fotogrammi già arrivati: aspettarla
faceva tardare la comparsa della targhetta quanto bastava perché sembrasse che non
comparisse affatto. Shell e compositore di Windows disegnano di continuo ma non sono
giochi, e sono esclusi per nome.

## Area di notifica

Per i dischi, tre modalità (`Impostazioni → Area di notifica`):

- **Una icona per ogni disco** (predefinita, stile Core Temp) — un numero per disco.
- **Una sola icona** con la temperatura del disco più caldo; il tooltip elenca tutti i dischi.
- **Nessuna icona.**

Nella riga *Quali dischi* si sceglie **a quali dischi** dare l'icona: prima era tutto o
niente. Le impostazioni tengono l'elenco di quelli **esclusi**, non di quelli inclusi, e
li riconoscono dal numero di serie (in mancanza, da modello e capacità): così un disco
collegato dopo compare da solo, già acceso, invece di restare invisibile finché non lo si
spunta, e resta quello giusto anche se Windows lo rinumera o gli cambia lettera.

Accanto si possono accendere **sei icone in più**, le stesse misure del riquadro
compatto:

| Icona | Sigla | Cosa mostra |
|---|---|---|
| Processore | `C°` | Temperatura, con la modalità scelta: core più caldo, media o package |
| Scheda video | `G°` | Temperatura del core |
| Carico processore | `C%` | Percentuale di utilizzo |
| Carico scheda video | `G%` | Percentuale di utilizzo |
| Memoria della scheda video | `V%` | Percentuale occupata |
| Memoria | `R%` | Percentuale occupata |

Le percentuali hanno una scala di colore propria — verde sotto il 70%, ambra fino al 90%,
rosso oltre — perché le soglie delle temperature lì non vorrebbero dire niente.

> **La percentuale di VRAM si calcola, non si legge.** Sembrerebbe naturale prendere il
> sensore di carico chiamato `GPU Memory`, ma quel nome copre due misure diverse: su una
> Radeon indica quanto *lavora* la memoria — su questa macchina segnava 20% mentre ne
> erano occupati 7,6 GB su 8,1 — mentre su una GeForce indica proprio l'occupazione. Il
> rapporto fra memoria usata e totale invece vuol dire sempre la stessa cosa, e coincide
> con la «memoria GPU dedicata» di Gestione attività. Il sensore resta come ripiego per
> le schede che non dicono quanta memoria hanno in uso.

Ognuna ha un identificativo fisso e alto (da 101 a 106), così restano dove le hai messe
nella barra anche quando cambia il numero di dischi collegati. Il tooltip riporta nome e
valore per esteso.

### Perché la sigla, e perché così corta

Sotto il valore c'è una sigla che dice cosa sia. Il primo segno dice **da dove** viene la
misura, l'ultimo **che cosa** è:

| | |
|---|---|
| `C:` `D:` | Disco: lettera di unità e due punti |
| `C°` `G°` | Temperatura di processore e scheda video |
| `C%` `G%` `V%` `R%` | Percentuali: carico processore, carico scheda video, memoria della scheda video, memoria |

I due punti sui dischi non sono un vezzo: senza, la `C` del disco `C:` e la `C` del
processore erano lo stesso segno. Il grado invece distingue una temperatura da una
percentuale anche quando il numero, da solo, potrebbe essere l'una o l'altra — sui dischi
non serve, perché i due punti dicono già tutto e un segno in più sarebbe soltanto un
pallino da guardare.

Senza sigla, due icone che segnano `43` sarebbero indistinguibili. Sulle icone di sistema
c'è sempre; per i dischi la governa l'impostazione *Sigla dei dischi*.

Le sigle sono corte perché lo spazio è quello che è: su un'icona da 16 px — la dimensione
che Windows usa al 100% di scala — alla sigla restano **sette pixel di altezza**. Un
carattere solo si legge, due si distinguono, tre diventano una macchia.

### Come si ottiene un numero grande su sedici pixel

Tre accorgimenti, tutti misurati sul foglio di prova ingrandito cinque volte:

- **Si ragiona sull'altezza delle cifre, non della riga.** `MeasureString` restituisce
  l'altezza della riga di testo, che comprende lo spazio per gli accenti sopra e per le
  code sotto: su cifre, che non hanno né gli uni né le altre, è circa il 40% in più del
  necessario. Pretendere che quella riga stesse dentro l'icona lasciava inutilizzata quasi
  metà dell'altezza, ed è il motivo per cui i numeri erano minuti. Ora si parte
  dall'altezza delle cifre (il 72% del corpo) e la posizione verticale si calcola dalla
  linea di base.
- **Prima si stringe, poi si rimpicciolisce.** Un numero di tre cifre non ci sta in
  larghezza su sedici pixel; rimpicciolirlo lo riduceva a sette pixel su dieci
  disponibili. Adesso viene compresso in orizzontale fino al 78%, e i dieci pixel li usa
  tutti: alto e stretto si legge molto meglio di piccolo e proporzionato.
- **Sotto i dieci pixel si disegna a mano.** A quella misura nessun carattere vero
  regge, e non c'è impostazione che lo salvi: senza antialiasing `43` diventa un blocco
  unico, con l'antialiasing le cifre restano grigie e molli. Lì entra un alfabeto **5×7**
  fatto di rettangoli pieni, senza antialiasing e allineato ai pixel dell'icona — cifre e
  lettere. Un numero netto di sette pixel si legge meglio di uno sfocato di otto. Se fra i
  glifi non c'è posto per il pixel di stacco (`100` su sedici pixel) lo si toglie: quasi
  tutte queste forme hanno già una colonna vuota ai lati. Da 20 px in su il numero torna
  al carattere vero, la sigla lo fa da 32 px.

Fra numero e sigla resta sempre almeno un pixel vuoto: attaccati, a sedici pixel, si
leggevano come una macchia sola. E la striscia della sigla non scende mai sotto i sette
pixel, l'altezza dell'alfabeto: il numero cede il pixel che serve — su un'icona da 16 px
passa da nove a sette — perché una sigla che non si distingue non serve a niente, ed è
proprio il suo mestiere distinguere un'icona dall'altra.

> **Perché il primo tentativo non bastava.** L'alfabeto era 3×5, e sul foglio di prova
> ingrandito cinque volte si leggeva benissimo. Sulla barra vera, dove ingrandimenti non
> ce ne sono, cinque pixel erano troppo pochi: il numero si vedeva, la sigla no. Le prove
> adesso mostrano anche una striscia a grandezza naturale, che è l'unico modo onesto di
> giudicarle.

> **Perché la lettera del disco non si vedeva affatto.** La sigla veniva disegnata con lo
> stesso criterio del numero: corpo pari al 95% dell'altezza disponibile, meno la
> correzione di dimensione scelta dall'utente, e la ricerca si fermava sotto i 4 pixel. Su
> un'icona da 16 px quel conto dava **2,9 pixel**: il ciclo usciva al primo giro e non
> disegnava niente. Il valore, intanto, si rimpiccioliva per fare spazio a una sigla che
> non compariva mai.

Personalizzabili: intervallo di aggiornamento (1–3600 s, predefinito 3), tema, °C/°F,
quali dischi mostrare, carattere,
grassetto, dimensione, sfondo trasparente o pieno, sigla sotto la temperatura (lettera
dell'unità per i dischi, `C` e `G` per processore e scheda video), soglie di
attenzione/critica con i rispettivi colori — per i dischi, e separatamente per processore
e scheda video, che scaldano molto di più. L'anteprima mostra le icone a 16 e 32 px, cioè
come appaiono davvero.

Doppio clic sull'icona apre la finestra; il menu contestuale offre Mostra / Aggiorna /
Impostazioni / Esci.

**Impostazioni** apre solo la finestra delle impostazioni: quella principale resta dov'è.
Aperta da lì non ha una finestra su cui centrarsi, quindi si centra sullo schermo, compare
fra le applicazioni della barra e viene portata davanti a forza — senza, chiedendola da un
gioco a schermo intero, si aprirebbe dietro e non si saprebbe come tornarci.

> **Windows 11 nasconde le icone delle app nuove** nel menu a scomparsa (la freccia `^`).
> Per fissarla accanto all'orologio: *Impostazioni → Personalizzazione → Barra delle applicazioni
> → Altre icone nell'area di notifica* → attiva **Disk Temp Monitor**. La scelta viene
> ricordata: l'icona usa un identificativo fisso (vedi sotto).

### Perché l'icona restava fissata solo fino al riavvio

Windows memorizza la posizione scelta in `HKCU\Control Panel\NotifyIconSettings`, usando
come chiave **il percorso dell'eseguibile più l'uID dell'icona**. La `NotifyIcon` di
WinForms assegna l'uID da un contatore interno che riparte e avanza a ogni creazione del
componente: ogni ricreazione produceva quindi un'icona "nuova", che tornava nel menu a
scomparsa. L'icona è perciò gestita direttamente con `Shell_NotifyIcon`
(`UI/TrayIcon.cs`) con uID fisso, pari alla posizione del disco.

Attenzione: l'identità comprende il percorso, quindi avviare l'app da una cartella
diversa (per esempio `bin\Release\` invece di `publish\`) vale come applicazione diversa
e richiede di rifissare l'icona.

Riaprire il programma mentre è già in esecuzione non avvia una seconda copia e non mostra
avvisi: richiama la finestra di quella attiva, tramite un evento con nome.

## Impostazioni: Applica, e configurazioni su file

La finestra delle impostazioni ha quattro pulsanti: **Predefiniti**, **Applica**,
**Annulla**, **OK**.

*Applica* rende effettivo quello che si è cambiato **senza chiudere la finestra**: si
ritocca, si guarda l'effetto, si ritocca ancora. Fa esattamente quello che fa la conferma
— intervallo di aggiornamento, tema, icone, riquadro compatto, sovrimpressione — perché
sono lo stesso pezzo di codice: due percorsi separati finirebbero per divergere alla prima
aggiunta. L'unica differenza è che la combinazione da tastiera non viene ripresa finché la
finestra resta aperta, altrimenti la verifica lì dentro la troverebbe occupata da noi
stessi.

Nella scheda **Configurazione**, *Esporta...* e *Importa...* scrivono e rileggono tutte le
impostazioni in un file `.json`: per portarle su un'altra macchina, o per tenersi da parte
una configurazione a cui tornare. Quello che si importa entra nella copia di lavoro — si
vede subito nella finestra — e diventa definitivo solo con Applica o OK: chi importa il
file sbagliato se ne accorge e annulla. Le voci mancanti, se il file viene da una versione
più vecchia, restano ai valori predefiniti invece di far fallire tutta la lettura.

## Avvio automatico

`Impostazioni → Avvia automaticamente con Windows`. Se l'app è in esecuzione come
amministratore crea un'attività pianificata con privilegi elevati (nessun prompt UAC al
login), altrimenti usa la chiave `Run` del registro. In entrambi i casi parte con `--tray`,
cioè direttamente in area di notifica.

## File

```
Program.cs                  avvio, istanza singola, gestione errori
app.manifest                asInvoker + DPI per monitor
Native/NativeMethods.cs     P/Invoke, codici IOCTL e comandi ATA
Models/DiskModels.cs        DiskInfo, SmartAttribute, stati di salute
Services/StorageQuery.cs    descrittore, sensori del driver, Identify e log page NVMe
Services/AtaSmart.cs        SMART_RCV_DRIVE_DATA, IDENTIFY, attributi e soglie ATA
Services/AtaPassThrough.cs  IOCTL_ATA_PASS_THROUGH, il canale ATA moderno
Services/ScsiLogSense.cs    pagine di log SCSI 0Dh e 2Fh (temperatura)
Services/UsbNvmeBridge.cs   comandi NVMe attraverso i ponti USB Realtek
Services/SmartNames.cs      nomi degli attributi S.M.A.R.T.
Services/DiskScanner.cs     enumerazione dischi e composizione dei dati
Services/AppSettings.cs     impostazioni (%AppData%\DiskTempMonitor\settings.json)
Services/Startup.cs         avvio con Windows, riavvio elevato
Services/Report.cs          rapporto testuale
UI/Theme.cs                 tavolozza, tipografia, primitive di disegno, tema scuro
UI/Controls.cs              controlli disegnati: scheda, pulsante, casella di spunta,
                            elenco a discesa, campo numerico, riga chiave/valore
UI/MainForm.cs              finestra principale
UI/SettingsForm.cs          impostazioni con anteprima delle icone
UI/DiskCard.cs              scheda riassuntiva per disco con indicatore circolare
UI/TempChart.cs             grafico dell'andamento della temperatura
UI/TrayManager.cs           icone in area di notifica
UI/TrayIcon.cs              Shell_NotifyIcon con uID stabile
UI/MiniWindow.cs            riquadro compatto sempre in primo piano
UI/OverlayWindow.cs         sovrimpressione di gioco (finestra a strato, per pixel)
UI/OverlayRenderer.cs       disegno della targhetta, condiviso con l'anteprima
UI/OverlayPlacementForm.cs  posizionamento con anteprima trascinabile
Services/FrameRateMonitor.cs conteggio degli FPS via ETW, senza iniezione
Services/Hotkey.cs          combinazioni globali da testo a RegisterHotKey
Services/Notifier.cs        regole e preferenze degli avvisi
Services/WindowsToast.cs    notifiche native (AppUserModelID e collegamento)
Services/SatPassThrough.cs  comandi ATA incapsulati in SCSI per i box USB
Services/SelfTest.cs        autodiagnosi del disco (NVMe e ATA)
Services/SensorHub.cs       unico punto di lettura di processore e scheda video
Services/HardwareMonitor.cs sensori via LibreHardwareMonitor (CPU e GPU)
Services/CoreTempReader.cs  temperature dalla memoria condivisa di Core Temp
Services/GpuInfo.cs         schede video installate, dal registro
UI/SystemView.cs            scheda Sistema: core, scheda video, memoria, scheda madre
UI/IconRenderer.cs          generazione icone (ICO a 32 bit con canale alfa)
```

> Le ComboBox `DropDownList`, i `NumericUpDown` e le `CheckBox` di WinForms ignorano
> `BackColor` o lo rendono illeggibile sul fondo scuro: sono stati sostituiti da
> controlli disegnati a mano in `Controls.cs`. Il campo numerico, a differenza di quello
> di sistema, risponde alla rotellina solo quando ha il fuoco, così scorrendo la pagina
> non si alterano i valori che si attraversano.
>
> **Attenzione a liberare un `ContextMenuStrip` nel suo evento `Closed`:** WinForms
> continua a usarlo subito dopo (`OnItemClicked` chiama `SetVisibleCore`, che legge
> l'handle), e il risultato è una `ObjectDisposedException` che chiude l'applicazione.
> I menu delle tendine e dell'area di notifica vengono perciò creati una volta sola e
> riusati.
>
> **Il DataGridView non va messo in doppio buffer.** La proprietà `DoubleBuffered` è
> protetta e si attiva per sottoclasse, ma il controllo ridipinge solo le celle "sporche"
> dando per scontato di disegnare sullo schermo: con il buffer restano residui di bordi
> verticali. La griglia degli attributi perciò non lo usa, non fa disegnare i bordi a
> WinForms (`CellBorderStyle.None`, separatori orizzontali tracciati in `RowPostPaint`),
> rimanda il ridisegno completo a layout concluso — quando la colonna elastica cambia di
> pochi pixel il controllo ricicla i pixel già disegnati, e invalidare subito arriva
> troppo presto — e disattiva il rettangolo di messa a fuoco della cella corrente.

Impostazioni e log errori: `%AppData%\DiskTempMonitor\`

## Ricompilare

```bash
cd C:\Users\elia.mazzuchelli\Downloads\DiskTempMonitor && dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```
