# MogyAntiCheat Útiterv

Ez a dokumentum a tervezett funkció mérföldköveit követi.
Státusz értékek: `Planned`, `In Progress`, `Done`, `Deferred`.

## M1 - Nemzetköziesítés (i18n) alapok

Státusz: `Done`
Célidő: 2026 Q2

Cél:
- Többnyelvű plugin üzenetek és admin riportok támogatása.

Szállítandók:
- Nyelvi fájlok az `oxide/lang/` alatt (kezdés: `en` és `hu`).
- `DefaultLanguage` config kulcs.
- Hiányzó kulcs fallback stratégia (`selected -> default -> en`).
- Üzenetkulcs audit az összes chat/admin kimenetre.

Elfogadási feltételek:
- Minden jelenlegi felhasználói üzenet kulcs alapú.
- Az üzemeltető kódmódosítás nélkül tud alapnyelvet váltani.

RFC:
- `docs/RFCs/RFC-0001-i18n-foundation.md`

## M2 - Többszintű büntetési profilok

Státusz: `Deferred`
Célidő: Újraértékelés M3 után

Indok:
- A jelenlegi mitigációs filozófia konzervatív, és az agresszív lépcsőzés növelheti a false positive hatását.

Policy megjegyzés:
- Alapértelmezetten maradjon a fokozatos, kíméletes büntetés.
- Hard nerf csak extrém mintázatnál legyen opció (kb. 90%+ gyanús konzisztencia), ne normál magas skill esetén.

## M3 - Publikus bővítmény API külső pluginokhoz

Státusz: `Done`
Célidő: 2026 Q3

Cél:
- Külső pluginok eseményekre fel tudjanak iratkozni és viselkedést bővíteni.

Szállítandók:
- Dokumentált hook szerződés (eseménynevek + payload mezők).
- Read-only lekérdező API játékos anti-cheat állapothoz.
- API verziózási szabály (`ApiVersion`).

Elfogadási feltételek:
- Egy példa külső plugin tud reagálni suspicion/penalty eseményekre.
- Törő API változások verziózottan és dokumentáltan történnek.

RFC:
- `docs/RFCs/RFC-0002-public-extension-api.md`

## M4 - Opcionális külső webhook/HTTP integráció

Státusz: `Done`
Célidő: 2026 Q3-Q4

Cél:
- Kiválasztott anti-cheat események továbbítása külső rendszerek felé.

Szállítandók:
- Opcionális webhook config (endpoint, auth token, retry policy).
- Rate limiting és backoff viselkedés.
- Fail-safe mód (ha webhook hibázik, az anti-cheat mag működik tovább).
- Discord webhook kompatibilis payload (`content`) automatikus használata Discord endpoint esetén.

Elfogadási feltételek:
- Magas jelértékű események játékmenet-romlás nélkül kiküldhetők.

RFC:
- `docs/RFCs/RFC-0003-webhook-http-integration.md`

## M5 - Carbon Mod kompatibilitás

Státusz: `Planned`
Célidő: 2026 Q4

Cél:
- Első osztályú kompatibilitás biztosítása Carbon alapú Rust szerverekhez az Oxide/uMod támogatás megtartása mellett.

Szállítandók:
- Kompatibilitási mátrix futásidejű viselkedésre (hookok, jogosultságok, data/config/lang útvonalak, chat parancsok).
- Absztrakciós pontok a framework-specifikus API és fájlrendszer eltérések kezelésére.
- Validációs checklist mindkét környezetre (Oxide/uMod és Carbon).
- Frissített telepítési és üzemeltetési dokumentáció mindkét runtime-hoz.

Elfogadási feltételek:
- A plugin mindkét runtime alatt fut, a core anti-cheat működés regressziója nélkül.
- Az admin parancsok és az adattárolás konzisztensen működik Oxide/uMod és Carbon alatt is.
- Minden ismert runtime-specifikus korlát explicit dokumentálva van.

RFC:
- `docs/RFCs/RFC-0004-carbon-compatibility.md`

## M6 - Kiterjesztett naplózás & KDA + Ping monitorozás

Státusz: `Planned`
Célidő: 2026 Q4 - 2027 Q1

Cél:
- Halálos lövések (Kills/Deaths/Assists) nyomon követése az anti-cheat adatokkal együtt.
- Folyamatos játékosonkénti ping monitorozás és lag detekció a kliens oldali vs szerver oldali problémák megkülönböztetésére.
- Ping kiugrások detekciója csatákban, mint lehetséges hálózati manipuláció jele.

Szállítandók:
- Az adatperzisztencia kiterjesztése halálos lövések, halálok és assists naplózásához játékosokként és fegyverekként.
- In-memory csúszó ping-történet (min/max/átlag ping csatamenetekben).
- Ping-kiugrás detekciós algoritmus az olyan szokatlan hálózati viselkedés jelzésére, amely magas sebzés közben történik.
- Kiterjesztett webhook payload KDA és ping statisztikákkal a külső elemzéshez.
- Admin parancs `/ac-stats` a KDA és ping adatok lekérdezéséhez egy adott játékosra.

Elfogadási feltételek:
- KDA adat megmarad szerver mentések és újratöltések között.
- Ping baseline játékosokként létrehozva (statisztikai átlag és szórás).
- Ping kiugrások > 2 σ csatákban naplózódnak korrelációs elemzéshez.
- Lekérdezés API kiterjesztve KDA és ping telemetriai adatokkal.

RFC:
- `docs/RFCs/RFC-0005-enhanced-logging-kda.md`

## M7 - LagSwitch detekció

Státusz: `Planned`
Célidő: 2027 Q1

Cél:
- Szándékos lagswitch támadások detekciója (gyors csatlakozások/lecsatlakozások vagy szándékos hálózati manipulációk, amelyek sebzési ablakokat hoznak létre).
- Mintázatok azonosítása, ahol hirtelen kapcsolat késések egybeesnek azzal, hogy a játékos sebzést kap.
- Forensics adat biztosítása szerver adminisztrátoroknak a gyanús lagswitch visszaélésről.

Szállítandók:
- Csatlakozási állapot nyomkövetésselő (online/offline idővonal játékosokként).
- Latensi anomália detektor (hirtelen ping ugrások a sebzésvétellel párosulva).
- Pontozási algoritmus: súlyosság a gyakoriság + időzítési korreláció alapján a sebzési eseményekkel.
- Admin parancs `/ac-lagswitch-audit <player>` az részletes idővonalas felülvizsgálathoz.
- Webhook esemény `OnMogyAcLagswitchDetected` a sebzési korreláció metaadataival.
- Konfigurálható küszöb a lagswitch jelzéshez.

Elfogadási feltételek:
- A lagswitch mintázat detekció akkor indul, amikor a csatlakozási állapot + ping anomáliák egybeesnek a sebzés időzítésével.
- A false-positive arány alacsony marad a többesemény-korrelációt igénylő feltételek miatt.
- A forensics idővonal exportálható az admin felülvizsgálathoz.

RFC:
- `docs/RFCs/RFC-0006-lagswitch-detection.md`

## M8 - ML/Neurális hálózat szerviz modul

Státusz: `Planned`
Célidő: 2027 Q2+

Cél:
- Egy külön gépi tanulás szerviz felépítése (nem beágyazott a pluginba), amely megtanul az anti-cheat és játékmenet történeti adataiból.
- Neurális hálózatok használata az egyes lövések és teljes csatamenetek értékeléséhez, figyelembe véve az összes kontextusváltozót.
- A detekciós küszöbök automatikus hangolása és optimalizálása a történeti adatok alapján.

Architektúra:
- **Adatgyűjtési szerviz**: A plugin részletes eseménynaplókat küld (lövés részletei, hit-korreláció, ping, KDA kontextus, fegyver típusa, távolság, játékos skill proxy).
- **Tanítási pipeline**: Offline model tanítás az összegyűjtött szerver adatokon címkézett példák felhasználásával (megerősített csalók vs képzett legitim játékosok).
- **Inference API**: Valósidejű vagy kötegelt pontozás a gyanús mintázatokon; a plugin megkapja a minták pontosságát.
- **Automatikus Config optimalizáló**: Hangolt config ajánlások generálása a model meglátások alapján (módosított `SafeDistance`, `MaxAccuracy`, küszöbök fegyverenként).

Szállítandók:
- Külön `MogyAntiCheatML` szerviz (Python/C#/.NET wrapper, dedikált gépen vagy sidecar-ként futtatható).
- Adatexport utility a plugin naplók tanítási adatok előkészítéséhez.
- REST API szerződés a plugin és ML szerviz között (inference végpont, config javasolt végpont).
- Feedback hurok: a plugin valós pozitív/hamis pozitív kimeneti eredményeket jelenthet a modell újratanításának javításához.
- Admin UI vázlat a ML szerviz csatlakozás konfigurálásához és a model egészségességének áttekintéséhez.

Elfogadási feltételek:
- A plugin opcionálisan csatlakozhat az ML szervezethez REST végponton a config-ban.
- Az ML-ből származó pontossági pontok a meglévő heurisztikákat erősítik meg az független plugin működés megtörése nélkül.
- A model újratanítása javítja a recall/precision teljesítményt a szerver-specifikus adatokon az idő múlásával.
- A szerviz kecske módon degradálódik, ha az ML végpont nem érhető el (a plugin önálló módban működik).

RFC:
- `docs/RFCs/RFC-0008-ml-service-module.md`

## M9 - In-game admin eszközök és vizualizáció

Státusz: `Planned`
Célidő: 2027 Q2

Cél:
- Admin felhasználói felület biztosítása a játékon belül az anti-cheat valósidejű monitorozásához és konfigurálásához.
- Elemzési adatok exportálása több formátumban (Excel, CSV, diagram képek) az offline felülvizsgálathoz és döntéshozatalhoz.

Szállítandók:
- In-game parancs panel (UI kiterjesztés vagy chat alapú felület):
  - `/ac-dashboard` — élő nézet a jelzett játékosokról, gyanús mintázatokról, megbízhatósági pontszámokról.
  - `/ac-override <player> <damage-reduction-% | off>` — manuális sebzéscsökkentés váltása egy adott játékoshoz (audit nyomvonallal).
  - `/ac-chart <player> <metric>` — ASCII vagy szöveges diagram renderelés az accuracy trendről, ping-történetről vagy KDA időből.
- Adatexport:
  - `/ac-export csv` — teljes játékos-történet és statisztika exportálása CSV-be a szerver adatkönyvtárba.
  - `/ac-export excel` — formázott Excel munkafüzet generálása pivot táblákkal és feltételes formázással.
  - `/ac-export chart <player> <metric> <format>` — PNG/SVG hőtérkép vagy vonalas diagram generálása és mentése az adatkönyvtárba.
- Config valósidejű újratöltési képessége:
  - `/ac-config-tune <param> <value>` — küszöb paraméterek dinamikus módosítása (megerősítési prompttal).
  - `/ac-suggest` — ML szerviz lekérdezése az automatikus hangolt config ajánlásokhoz és a diffs megjelenítéséhez.

Elfogadási feltételek:
- A sebzéscsökkentési felülbírálat auditálható (naplózza, ki változtatta meg, mikor, miért).
- CSV/Excel exportálások tartalmazzák az összes szükséges adatot a külső statisztikai elemzéshez.
- A diagram exportálások olvashatók és helyesen képviselik az alapul szolgáló metrikákat.
- Az admin parancsok szerepkör-alapú hozzáférés-vezérléssel rendelkeznek (csak adminok/moderátorok használhatják).

RFC:
- `docs/RFCs/RFC-0009-admin-dashboard-export.md`

## Megjegyzés

- Minden mérföldkőhöz külön RFC készüljön a `docs/RFCs/` alatt.
- Implementáció csak RFC elfogadás után induljon.

