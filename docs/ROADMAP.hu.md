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

## Megjegyzés

- Minden mérföldkőhöz külön RFC készüljön a `docs/RFCs/` alatt.
- Implementáció csak RFC elfogadás után induljon.

