# OpenTv

IPTV-spelare för Windows med inbyggt VPN-stöd. Byggd med .NET 8, Avalonia UI 11
och LibVLCSharp.

**Klart:** M3U-import, Xtream Codes, uppspelning, WireGuard, TV-guide (XMLTV).
**Återstår:** OpenVPN. Se längst ned.

---

## Projektstruktur

Logiken är avsiktligt separerad från plattformen så att `Core` och `Core.Vpn` kan
återanvändas rakt av i en framtida Android/iOS-version.

| Projekt | Target | Innehåll |
|---|---|---|
| `Core/` | `net8.0` | M3U-parsning, Xtream-klient, XMLTV-parsning och EPG-matchning, datamodeller, playlist-laddning, JSON-lagring. Inga Windows- eller UI-beroenden. |
| `Core.Vpn/` | `net8.0` | Enbart kontrakt: `IVpnService`, `VpnManager`, `VpnProfile`, `VpnState`. Plattformsneutralt. |
| `Windows.Vpn/` | `net8.0-windows` | WireGuard-implementation via den officiella `wireguard.exe` + tunnel-tjänsten. UAC-hantering. |
| `Windows.UI/` | `net8.0-windows` | Avalonia-app (MVVM). Producerar `OpenTv.exe`. |

Beroendekedjan går bara åt ett håll: `Windows.UI` → `Windows.Vpn` → `Core.Vpn` → `Core`.
`Core` känner inte till att VPN eller Avalonia existerar.

---

## Krav

* **.NET 8 SDK** — installerat och verifierat (`dotnet --version` → 8.0.424)
* **WireGuard for Windows** — krävs bara för VPN-funktionen: https://www.wireguard.com/install/
  Appen upptäcker automatiskt om det saknas och säger till i VPN-fliken.

VLC-motorn behöver du **inte** installera — den följer med som NuGet-paket
(`VideoLAN.LibVLC.Windows`) och packas in i bygget.

---

## Bygga och köra

```bash
dotnet build OpenTv.sln
```

```bash
dotnet run --project Windows.UI/OpenTv.Windows.UI.csproj
```

## Bygga en fristående .exe

Self-contained betyder att .NET-runtime och VLC packas med — mottagaren behöver
inget installerat. Det finns två varianter.

### Alternativ 1: en enda .exe-fil (rekommenderas för att skicka vidare)

```bash
dotnet publish Windows.UI/OpenTv.Windows.UI.csproj -c Release -r win-x64 --self-contained true -p:EmbedLibVlc=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish-single
```

Ger **exakt en fil: `publish-single\OpenTv.exe`, ca 88 MB**. Inget annat behövs.

Första gången den startar packar den upp VLC-motorn (~100 MB) till
`%LOCALAPPDATA%\OpenTv\runtime\libvlc-3.0.21\` — det tar ungefär en sekund. Alla
senare starter är omedelbara. Mappen kan raderas när som helst; den återskapas.

### Alternativ 2: mapp-utgåva (snabbast att bygga, inget uppackningssteg)

```bash
dotnet publish Windows.UI/OpenTv.Windows.UI.csproj -c Release -r win-x64 --self-contained true -o publish
```

Ger `publish\` med ca 430 filer på ~198 MB. Hela mappen måste följa med —
`OpenTv.exe` ensam fungerar inte här.

### Varför enfilsläget kräver `EmbedLibVlc`

`PublishSingleFile` ensamt **räcker inte** och ger en app som startar men inte kan
spela upp något. .NET plattar ut inbäddade nativbibliotek till en temporär katalog,
vilket förstör den `libvlc\win-x64\plugins\`-struktur som VLC kräver för att hitta
sina codecs. `Core.Initialize()` letar då förgäves bredvid exe:n och rapporterar
"Failed to load required native libraries".

`EmbedLibVlc=true` löser det genom att i stället komprimera motorn till en resurs i
assemblyn. [VlcRuntime.cs](Windows.UI/VlcRuntime.cs) packar upp den vid första start
och ger LibVLCSharp en explicit sökväg. Uppackningen sker till en temporär
grannkatalog som flyttas på plats först när den är komplett, så ett avbrutet första
försök aldrig lämnar en halv motor som senare starter skulle lita på.

Mappnamnet innehåller VLC-versionen, så en uppgradering packar upp på nytt i stället
för att blanda plugins från två versioner — vilket VLC vägrar ladda.

---

## Lägga till kanaler

Under **Settings → Playlists → Add** finns tre källtyper:

| Typ | Fält |
|---|---|
| `M3uUrl` | URL till en M3U/M3U8-playlist |
| `M3uFile` | Sökväg till en lokal .m3u-fil |
| `Xtream` | Serveradress + användarnamn + lösenord |

För Xtream finns en **Test connection**-knapp som autentiserar mot panelen och
visar kontostatus, utgångsdatum och antal samtidiga anslutningar — så du upptäcker
felaktiga uppgifter direkt i stället för vid uppspelning.

`samples/test-playlist.m3u` innehåller fyra publika test-strömmar (Apple BipBop,
Big Buck Bunny, Tears of Steel, Sintel). Den är redan förkonfigurerad i din
`%APPDATA%\OpenTv\sources.json`, så appen spelar upp direkt vid start.

---

## TV-guide

Fyll i **XMLTV EPG URL** på en playlist för att slå på guiden. Fältet är valfritt —
saknas det använder appen den guide-URL källan själv annonserar:

* **M3U:** attributet `url-tvg` / `x-tvg-url` på `#EXTM3U`-raden
* **Xtream:** kontots `xmltv.php`-endpoint, som byggs automatiskt

Både `.xml` och `.xml.gz` fungerar, och en lokal filsökväg går lika bra som en URL.
Gzip upptäcks på magic bytes i stället för filändelsen, eftersom leverantörer ofta
har fel filändelse.

Guiden syns på två ställen:

* **Kanallistan** får en rad med vad som sänds just nu plus en progress-stapel
* **TV guide**-knappen öppnar ett eget fönster med kanaler till vänster och tablån
  för vald dag till höger, sju dagar framåt. Listan hoppar automatiskt till det
  som sänds nu, och **Watch this channel** byter kanal i huvudfönstret.

Guidefönstret är modeslöst med flit — poängen är att kunna bläddra i tablån medan
en kanal fortsätter spela.

### Kanalmatchning

`tvg-id` används när den finns och guiden känner igen den. Annars faller appen
tillbaka på namnmatchning, som normaliserar bort det IPTV-listor brukar hänga på:
landsprefix (`SE:`, `|SE|`, `[US]`), kvalitetssuffix (`HD`, `FHD`, `4K`), accenter
och skiljetecken. `SE: SVT 1 HD` och `svt1` blir båda `svt1`.

Kolonformen kapas bara för en tvåbokstavskod, så en kanal som faktiskt heter
`MTV: Hits` behåller sitt namn. Statusraden visar hur många kanaler som matchade,
t.ex. `Guide: 118/240 channels`.

### Prestanda

XMLTV-filer på 50–200 MB är normalt. Parsern är strömmande och bygger aldrig en
DOM, och den kastar poster för kanaler som inte finns i din playlist *medan* den
läser — det är den enskilt största besparingen. Bara ett fönster på 12 timmar bakåt
och 8 dagar framåt behålls.

Nedladdningen cachas i `%LOCALAPPDATA%\OpenTv\epg\` i sex timmar. Misslyckas en
uppdatering används den gamla kopian hellre än att lämna dig utan guide.

### Testdata

`samples/generate-test-epg.ps1` genererar en XMLTV-fil som matchar test-playlisten:

```bash
pwsh samples/generate-test-epg.ps1
```

Den utgår från dagens datum, så kör om den när filen blivit gammal.
`-SlotMinutes 5` ger en tätare tablå, vilket är praktiskt för att testa scrollning.

---

## Var data sparas

Allt ligger under `%APPDATA%\OpenTv\`:

| Fil | Innehåll |
|---|---|
| `sources.json` | Playlist-profiler, senast använd kanal, volym |
| `vpn-profiles.json` | VPN-profilernas metadata (namn, typ, sökväg) |
| `vpn\*.conf` | Importerade WireGuard-konfigurationer |
| `crash.log` | Skrivs bara om appen kraschar vid start |

Regenererbara filer ligger separat i `%LOCALAPPDATA%\OpenTv\` och kan raderas när
som helst: `epg\` (cachade guide-nedladdningar) och `runtime\` (den uppackade
VLC-motorn i enfilsutgåvan).

Skrivningar går via en temporärfil och byts in atomärt, så en krasch mitt i en
sparning kan inte lämna en trasig config. En config som ändå blir korrupt döps om
till `.corrupt` och appen startar med tomma inställningar i stället för att vägra starta.

### Xtream-lösenord

Lösenord krypteras med **Windows DPAPI** bundet till ditt användarkonto innan de
skrivs till `sources.json`. Kopieras filen till en annan maskin eller ett annat
konto går den inte att dekryptera — appen frågar då efter lösenordet igen i stället
för att skicka chiffertext till leverantören.

Klartext-lösenord från en äldre `sources.json` läses fortfarande, och krypteras
automatiskt nästa gång profilen sparas.

> Observera: Xtream-protokollet skickar användarnamn och lösenord som
> **query-parametrar i URL:en**. Så fungerar alla Xtream-paneler — det är inget val
> som gjorts här. Därför redigeras request-URL:er bort ur alla felmeddelanden, så
> att ett lösenord aldrig kan hamna i en logg.

---

## Så fungerar VPN-integrationen

Valet blev att **styra de officiella binärerna** i stället för att bädda in
WireGuardNT via P/Invoke. Det ger signerade drivrutiner utan eget underhåll.

* **Anslut** kör `wireguard.exe /installtunnelservice <config>`, vilket registrerar
  och startar Windows-tjänsten `WireGuardTunnel$<namn>`.
* **Koppla ner** kör `wireguard.exe /uninstalltunnelservice <namn>`.
* Appen litar inte på processens exit-kod utan **pollar tjänstens verkliga tillstånd**
  innan den påstår att tunneln är uppe.

Tunnelnamnet är konfigurationsfilens basnamn — så härleder `wireguard.exe` det.
Därför saneras namnet vid import (`My VPN (Sweden)!` → `My-VPN-Sweden`) och görs
unikt, eftersom det också blir en del av ett Windows-tjänstnamn.

### UAC

Appen körs som vanlig användare (`asInvoker` i `app.manifest`) — att tvinga hela
mediaspelaren att köra som administratör vore dålig praxis. Enbart tunnel-kommandona
höjer rättigheter, via `ShellExecute` med verbet `runas`. Du får alltså **en
UAC-prompt per anslutning/nedkoppling**, inte en vid appstart. Avbryter du prompten
rapporteras det som ett tydligt fel i stället för att tolkas som lyckat.

### Tunneln överlever appen

Att stänga OpenTv river **inte** tunneln. Det är avsiktligt: alternativet är att
användaren plötsligt hamnar på sin bara uppkoppling utan förvarning. Vid start
adopterar appen en tunnel som redan är uppe (`RefreshAsync`), och en watchdog var
femte sekund märker om tunneln stoppats utifrån.

---

## Kända begränsningar

* **Kanallogotyper visas inte.** Xtream levererar `stream_icon` och M3U levererar
  `tvg-logo`, och båda sparas på `Channel` — men att rendera dem kräver en asynkron
  bildladdare för fjärr-URL:er.
* **Kontrollerna ligger under videon, inte ovanpå.** `VideoView` är ett nativt
  barnfönster, så Avalonia kan inte komposita ovanpå det. En TiviMate-liknande
  overlay kräver antingen `VideoView.Content` eller rendering via `WriteableBitmap`.
* **"Ansluten" = tjänsten kör.** Det bevisar att adaptern är uppe, men ännu inte att
  handskakningen med peern lyckats. Det kräver `wg show`.
* **Xtream: bara live-TV.** VOD och serier använder andra `action`-anrop
  (`get_vod_streams`, `get_series`) och är inte implementerade.
* **TV-guiden är en tablå per kanal, inte ett tidslinjeraster.** Den visar en kanal
  i taget, inte TiviMates rutnät med alla kanaler mot en tidsaxel. Ett sådant raster
  kräver en egen virtualiserad layout-panel.
* Sidopanelen har fast bredd (330 px), ingen splitter.

## Vad som återstår

1. **OpenVPN** — `IVpnService`-kontraktet och `VpnManager`-routingen finns redan på
   plats; implementationen ska styra en `openvpn.exe`-process via dess
   management-interface (TCP-socket) för realtidsstatus och loggar.
2. **Tidslinjeraster i guiden** — kanaler som rader mot en tidsaxel, TiviMate-stil.
   All data finns redan i `EpgGuide`; det som saknas är layout-panelen.
3. **Enhetstester** — `M3uParser`, `XtreamClient` och `XmltvParser` är verifierade
   med engångsharnesser (se nedan) men förtjänar riktiga tester i repot, eftersom de
   ska återanvändas på mobil.

---

## Verifiering som gjorts

| Område | Resultat |
|---|---|
| M3U-parsning | Komma i kanalnamn, `#EXTGRP`, `#EXTVLCOPT`, dubbletter, trasiga rader |
| Xtream-klient | 22 kontroller mot en mock-panel med inkonsekventa JSON-typer |
| XMLTV / EPG | 35 kontroller: tidsstämpelformat, namnnormalisering, kanalfiltrering, nu/nästa, gzip, XXE-skydd |
| WireGuard | Full tunnel-livscykel: import → connect → tjänst + nätverksadapter skapade → adoptera vid omstart → disconnect → allt borttaget |
| DPAPI | Round-trip verifierad; fel entropi avvisas |
| Uppspelning | HLS-teststream, inbäddad i fönstret, D3D11VA-hårdvaruavkodning |
| Publicering | Båda lägena verifierade: enfils-exe (88 MB — kall start packar upp motorn på 1 s, varm start omedelbar) och mapp-utgåva |
