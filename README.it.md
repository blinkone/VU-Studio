# VU Studio

**Un'app open source per configurare i dial Streacom VU1: mostra dati hardware, di rete o software con quadranti e-ink personalizzati e luci RGBW.**

🇬🇧 [Read in English](README.md)

![Dashboard di VU Studio con quattro dial](docs/images/screenshots/dashboard.png)

> **Stato: alpha iniziale.** Gran parte di VU Studio è stata provata con un simulatore di dial e con test automatici. Il collaudo su hardware Windows reale è appena iniziato. Aspettati qualche imperfezione e [segnala quello che trovi](https://github.com/blinkone/VU-Studio/issues). Vedi la [roadmap](docs/roadmap.md).

## Perché esiste

Ho quattro dial Streacom VU1 sul mio PC Windows. Il VU-Server ufficiale mi sembrava troppo complicato da configurare e troppo limitato per quello che volevo mostrare, così ho creato l'app che volevo per uso personale. Può essere utile anche ad altri possessori di VU1, quindi è pubblica e gratuita.

**Questo progetto è vibecoded.** È stato scritto con un assistente di programmazione AI (Claude Code). Ho deciso io il design e le funzioni e l'ho provato, ma il codice non l'ho scritto a mano. Tutto il codice sorgente è in questo repository: chiunque può leggerlo, controllarlo e compilarlo da sé. Revisioni del codice e segnalazioni di bug sono benvenute.

## Download

Scarica l'ultimo installer (`VuStudio-Setup-x.y.z.exe`) dalle [Release](https://github.com/blinkone/VU-Studio/releases). Ogni release contiene anche l'agente per Linux e Windows, i checksum SHA-256 e l'archivio del codice sorgente.

* L'installer non è ancora firmato digitalmente, quindi Windows SmartScreen mostrerà un avviso: scegli *Ulteriori informazioni → Esegui comunque*.
* Chiudi VU-Server prima di avviare VU Studio: la porta COM dell'hub può usarla un solo programma alla volta.
* Log: `C:\ProgramData\VU Studio\logs` (servizio) e `%LocalAppData%\VU Studio\logs` (app nel tray).

## Cosa fa

* Parla **direttamente con l'hub VU1** via USB seriale: niente server separati né API key.
* Legge **questo PC**: CPU, GPU, RAM, dischi, rete, ventole e consumi tramite LibreHardwareMonitor, più HWiNFO quando è aperto.
* Legge **dati software**: un vero VU-meter audio, il brano in riproduzione, il programma in primo piano e gli FPS nei giochi (RivaTuner).
* Legge la **rete di casa**: altri PC, Unraid, Steam Deck, Raspberry Pi e NAS tramite l'agente VU Studio, Glances o Prometheus, più il router (UPnP) e il ping.
* Legge **dati online e di domotica**: meteo, criptovalute, Home Assistant, MQTT, qualsiasi API JSON, i tuoi script e un'API locale per inviare dati.
* Crea **quadranti e-ink** partendo da temi (vintage, minimal, strumentazione, HUD sci-fi) o con un editor visuale a livelli, con anteprima a 1 bit identica al dispositivo.
* Gestisce la **retroilluminazione RGBW** in base al valore (gradienti, soglie), con effetti, allarmi e sincronizzazione (colore d'accento di Windows, copertina dell'album).
* Offre **profili automatici** (per esempio mentre gira un gioco), rotazione delle pagine, grafici storici e modalità notte/standby.
* Gira come servizio Windows con un'app nel tray. L'interfaccia web, in italiano e inglese, si apre anche dal telefono in rete locale se lo abiliti.

## Screenshot

Tutti gli screenshot usano il simulatore di dial integrato (`--simulate 4`).

| | |
|---|---|
| ![Editor del quadrante con la galleria dei temi](docs/images/screenshots/dial-face.png) **Editor del quadrante**: scegli un tema o personalizza ogni livello, con anteprima a 1 bit dal vivo. | ![Editor della retroilluminazione con un gradiente](docs/images/screenshots/dial-light.png) **Retroilluminazione**: colore in base al valore con gradienti o soglie, effetti e allarmi. |
| ![Pagina Fonti](docs/images/screenshots/sources.png) **Fonti**: questo PC, dispositivi in rete, domotica e servizi online. | ![Pagina Sensori](docs/images/screenshots/sensors.png) **Sensori**: tutti i valori con letture dal vivo e ricerca. |
| ![Pagina Storico con grafici](docs/images/screenshots/history.png) **Storico**: grafici con minimo, media e massimo, esportazione CSV. | ![Pagina Profili](docs/images/screenshots/profiles.png) **Profili**: cambiano cosa mostrano i dial in base al programma aperto, all'orario o a un sensore. |
| ![Impostazioni hardware](docs/images/screenshots/settings.png) **Impostazioni**: hub, dial, calibrazione, modalità notte e accesso. | ![Dashboard in tema chiaro](docs/images/screenshots/dashboard-light.png) **Tema chiaro** |

<p align="center"><img src="docs/images/screenshots/mobile.png" alt="Dashboard su telefono" width="260"></p>

### Temi e-ink inclusi

![Temi e-ink inclusi](docs/images/themes.png)

## Documentazione (in inglese)

* [Requisiti](docs/requirements.md)
* [Architettura](docs/architecture.md)
* [Protocollo seriale dell'hub VU1](docs/vu1-protocol.md)
* [API HTTP e WebSocket](docs/api.md)
* [VU Studio Agent per Unraid, Steam Deck e Linux](deploy/agent/README.md)
* [Roadmap](docs/roadmap.md)

## Compilare dai sorgenti

Requisiti: .NET 10 SDK e Node.js 22.

```bash
(cd web && npm ci && npm run build)      # interfaccia web, generata in src/VuStudio.Server/wwwroot
dotnet build VuStudio.slnx
dotnet test --project tests/VuStudio.Tests

# Prova senza hardware: quattro dial simulati, interfaccia su http://127.0.0.1:5341
dotnet run --project src/VuStudio.Server -- --simulate 4
```

I pacchetti per Windows (installer e binari dell'agente) si creano con `build/publish.ps1` / `build/publish.sh` e, a ogni tag `v*`, con il workflow di release.

## Ringraziamenti e lavori precedenti

* **[VU-Server](https://github.com/SasaKaranovic/VU-Server)** di Saša Karanović (Karanović Research) è il software ufficiale dei VU1 e il riferimento su cui si basa questo progetto. Il protocollo seriale dell'hub l'ho imparato leggendo il suo codice sorgente (vedi [le note sul protocollo](docs/vu1-protocol.md)). VU Studio è una nuova implementazione in C# e TypeScript: non contiene codice di VU-Server e non è un fork. Grazie per averlo reso aperto.
* I dial e l'hub VU1 sono progettati da Streacom e Karanović Research. VU Studio è un progetto indipendente della community e non è affiliato a nessuno dei due.
* VU Studio non è la prima app per i VU1 e non pretende di esserlo. Se conosci un progetto esistente che andrebbe citato qui, [apri una issue](https://github.com/blinkone/VU-Studio/issues) e lo aggiungo.

## Licenza

VU Studio è software libero, distribuito con licenza [GNU GPL v3.0 o successiva](LICENSE).

* I font inclusi sono rilasciati con la SIL Open Font License.
* Le icone provengono da Material Design Icons (Pictogrammers).
* I loghi di marchi presenti tra le icone appartengono ai rispettivi proprietari.

VU Studio è un progetto della community. Non è affiliato a Streacom né a Karanović Research.
