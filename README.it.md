# VU Studio

**Configura i tuoi dial Streacom VU1 in pochi minuti: mostra dati hardware, di rete o software con quadranti e-ink personalizzati e luci RGBW.**

🇬🇧 [Read in English](README.md)

> Stato: **sviluppo iniziale**. Il progetto è definito e i moduli sono in costruzione. Vedi la [roadmap](docs/roadmap.md).

VU Studio sostituisce il VU-Server ufficiale con un'unica applicazione che:

* parla **direttamente con l'hub VU1**, senza server separati né API key;
* legge **questo PC**: CPU, GPU, RAM, dischi, rete, ventole e consumi tramite LibreHardwareMonitor, più HWiNFO quando è aperto;
* legge la **rete di casa**:
  * altri PC, Unraid, Steam Deck, Raspberry Pi e NAS tramite l'agente VU Studio, Glances o Prometheus;
  * il router (UPnP) e il ping;
* legge **dati software e online**:
  * un vero VU-meter audio, il brano in riproduzione e gli FPS nei giochi;
  * meteo, criptovalute, Home Assistant e MQTT;
  * qualsiasi API JSON, i tuoi script e un'API locale per inviare dati;
* crea **quadranti e-ink** partendo da temi (vintage, minimal, strumentazione, HUD sci-fi) o con un editor visuale, con anteprima a 1 bit identica al dispositivo;
* gestisce la **retroilluminazione RGBW** in base al valore (gradienti, soglie), con effetti, allarmi e sincronizzazione (colore d'accento di Windows, copertina dell'album);
* offre **profili automatici**, rotazione delle pagine, grafici storici e modalità notte/standby;
* gira come servizio Windows con icona nel tray e un pannello web opzionale per telefono o altri PC.

![Temi e-ink inclusi](docs/images/themes.png)

## Documentazione (in inglese)

* [Requisiti](docs/requirements.md)
* [Architettura](docs/architecture.md)
* [Protocollo seriale dell'hub VU1](docs/vu1-protocol.md)
* [Roadmap](docs/roadmap.md)

## Compilare dai sorgenti

Requisiti: .NET 10 SDK. Servirà anche Node.js 22 quando arriverà l'interfaccia web.

```bash
dotnet build VuStudio.slnx
dotnet test --project tests/VuStudio.Tests
```

## Licenza

VU Studio è software libero, distribuito con licenza [GNU GPL v3.0 o successiva](LICENSE).

* I font inclusi sono rilasciati con la SIL Open Font License.
* Le icone provengono da Material Design Icons (Pictogrammers).
* I loghi di marchi presenti tra le icone appartengono ai rispettivi proprietari.

VU Studio è un progetto della community. Non è affiliato a Streacom né a Karanović Research.
