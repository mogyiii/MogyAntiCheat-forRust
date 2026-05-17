# MogyAntiCheat for Rust (Oxide/uMod + Carbon)

[English docs](README.md) | [Source of truth](docs/SOURCE_OF_TRUTH.md)

![License](https://img.shields.io/badge/license-MIT-green)
![Version](https://img.shields.io/badge/version-1.9.8-blue)
![API](https://img.shields.io/badge/api-1.3.0-purple)
![Game](https://img.shields.io/badge/game-Rust-orange)

## Projekt dokumentáció

- Source of truth: `docs/SOURCE_OF_TRUTH.md`
- Roadmap: `docs/ROADMAP.md`
- Public API: `docs/PUBLIC_API.md`
- Plugin fejlesztői útmutató (HU): `docs/PLUGIN_DEV_GUIDE.hu.md`
- Config séma: `docs/CONFIG_SCHEMA.md`
- RFC sablon: `docs/RFCs/TEMPLATE.md`
- Változásnapló: `CHANGELOG.md`

A MogyAntiCheat egy statisztikai alapú anti-cheat plugin Rust szerverekhez (Oxide/uMod vagy Carbon).
A hagyományos azonnali tiltás helyett dinamikusan csökkenti a gyanús játékosok kimenő sebzését, így kisebb a false positive találatokból adódó kár.

## Működési elv

A plugin nem fájlokat vagy folyamatokat vizsgál, hanem harci eseményekből dolgozik.

1. Minden lövés egy ideiglenes várólistára kerül.
2. A valós játékos-játékos találatok vissza vannak párosítva a friss lövésekhez.
3. Fegyverenként gördülő pontossági statisztika készül.
4. A távoli találatok nagyobb súlyt kapnak, mint a közeliek.
5. Küszöbátlépés esetén a kimenő sebzés fokozatosan csökkenhet akár 0-ig.

## Főbb jellemzők

**Magdetektor:**
- Időablakos lövés-találat párosítás.
- Fegyverenként külön finomhangolható küszöbök.
- Távolsággal súlyozott pontosság a nagyobb precizitásért.
- Valós idejű sebzéscsökkentés a statisztikai gyanú alapján.

**Haladó funkciók:**
- K/D/A (ölés/halál/assziszt) nyomkövetés és tartós adattárolás.
- Játékosonkénti ping alapvonal + hálózatkezelési anomáliák felismerése.
- Lagswitch detekció: összetett pontozás ping-kiugrás, ölés minősége és reconnect minták alapján.
- ML szerviz integráció: opcionális külső megbízhatósági pontozás és automatikus hangolási javaslatok.

**Admin és adatok:**
- Élő admin irányítópult: játékos áttekintés, nerf státusz, felülbírálat követés.
- Kézi sebzés-felülbírálat játékosonként teljes audit-naplóval.
- ASCII diagramok: pontosság trendek, ping alapvonalak, K/D/A vizualizáció.
- CSV adatexport külső elemzéshez és jelentéskészítéshez.
- Élő config hangolás újratöltés nélkül.

**Integráció:**
- Public API v1.3.0 külső pluginok számára.
- Opcionális webhook/HTTP küldés (queue, retry/backoff, rate limit).
- Discord webhook endpoint automatikus támogatás (`content` payload).
- Oxide/uMod és Carbon runtime támogatás.
- Nemzetköziesítés: beépített angol és magyar; bővíthető nyelvi rendszer.

## Telepítés

1. Telepítsd az Oxide/uMod vagy Carbon rendszert a Rust szerveredre.
2. Másold a `MogyAntiCheat.cs` fájlt ide:
   - Oxide/uMod: `server/<identity>/oxide/plugins/`
   - Carbon: `server/<identity>/carbon/plugins/`
3. Töltsd újra a plugint vagy indítsd újra a szervert.
4. Állítsd be a küszöböket itt:
   - Oxide/uMod: `server/<identity>/oxide/config/MogyAntiCheat.json`
   - Carbon: `server/<identity>/carbon/configs/MogyAntiCheat.json` (vagy a szervered Carbon config könyvtára)

## Konfiguráció

A konfigurációban fegyverenkénti bejegyzések vannak a `Weapons` alatt, plusz globális beállítások:

- `MissExpirySeconds`: mennyi ideig számít érvényesnek egy leadott lövés a találat párosításához.
- `DefaultLanguage`: alapértelmezett plugin nyelv (`en` alap).
- `DebugMode`: debug logok be/ki (`false` alap).
- `PublicApi`: bővítmény API beállítások.
- `Webhook`: opcionális külső eseményküldés beállításai.

Fegyverenkénti paraméterek:

- `MaxAccuracy`: maximálisan megengedett találati arány (pl. `0.38 = 38%`).
- `SampleCount`: gördülő mintaméret (hány lövést tartson meg a statisztikához).
- `SafeDistance`: távolsági referencia a súlyozáshoz.

Webhook főbb mezők:

- `Enabled`: webhook küldés be/ki (`false` alap).
- `Endpoint`: cél webhook URL.
- `AuthToken`, `AuthHeader`: opcionális auth fejléc.
- `MaxRetries`, `BaseBackoffSeconds`, `MaxBackoffSeconds`: retry/backoff viselkedés.
- `RateLimitPerSecond`: másodpercenkénti max küldések száma.
- `QueueMaxSize`: memóriában tartott esemény queue méretlimit.
- `EmitSuspicionEvents`, `EmitPenaltyEvents`: eseménytípusonkénti ki/bekapcsolás.

## Discord webhook gyors használat

1. `Webhook.Enabled = true`
2. `Webhook.Endpoint = https://discord.com/api/webhooks/...`
3. (Opcionális) `/ac-debug on` a könnyebb hibakereséshez
4. Plugin reload

Megjegyzés:
- Discord endpoint esetén a plugin automatikusan Discord-kompatibilis (`username` + `content`) payloadot küld.
- A webhook küldés független a `PublicApi.Enabled` értéktől.

## Runtime megjegyzések (Oxide/Carbon)

- A plugin közös kódbázist használ mindkét runtime-hoz.
- Induláskor felismeri a környezetet (`Oxide/uMod` vagy `Carbon`), és ehhez igazítja a data/debug fájl útvonalat.
- Ha tárhely/host sajátosság miatt eltérő pathot használsz, a szerver startup log sora mutatja az aktív data útvonalat.

## Nyelvi testreszabás

Alap fájlok:

- Oxide/uMod: `oxide/lang/en/MogyAntiCheat.json`, `oxide/lang/hu/MogyAntiCheat.json`
- Carbon: használd a saját Carbon nyelvi könyvtárad (gyakran `carbon/lang/...`)

Lépések:

1. Szerkeszd a kívánt nyelvi JSON fájlt.
2. Állítsd a `DefaultLanguage` értéket a configban, vagy használd in-game: `/ac-lang <nyelvkód>`.
3. Ellenőrizd pl. `/ac-check` paranccsal.

## Jogosultságok

- `mogyanticheat.admin`: Hozzáférés az összes chat parancshoz.
- `mogyanticheat.bypass`: Mentesítés a sebzés nerf logika alól (kivéve ha `DebugMode` be van kapcsolva).

## Parancsok

- `/ac-check [játékosnév]` - Részletes statisztika egy játékosról.
- `/ac-list` - Online játékosok listázása átlag pontossággal és aktuális sebzés-szorzóval.
- `/ac-reset [játékosnév]` - Játékos statisztikáinak törlése.
- `/ac-lang <nyelvkód>` - Alapértelmezett plugin nyelv váltása (pl. `en`, `hu`).
- `/ac-debug <on|off>` - Debug mód ki/bekapcsolása (bekapcsolva a bypass figyelmen kívül van hagyva, és minden nem épület combat entity találatai beleszámítanak).
- `/ac-weapon <fegyverShortName|active> <MaxAccuracy|SampleCount|SafeDistance> <érték>` - Fegyverértékek módosítása in-game (és mentése configba).
- `/ac-debug-log [clear]` - Debug log fájl útvonala vagy törlése.
- `/ac-why [weaponShortName|active]` - Megmutatja, miért (nem) aktiválódik nerf az adott fegyvernél.
- `/ac-help` - Elérhető admin parancsok listázása.

Példák:

- `/ac-weapon rifle.ak MaxAccuracy 0.36`
- `/ac-weapon active SafeDistance 30`
- `/ac-debug on`

## Gyakori kérdések (FAQ)

### Bünteti a jó játékosokat, ha épp nagyon jó napjuk van?
Nem. A rendszer gördülő mintát használ (`SampleCount`), ezért a rövid "hot streak" időszakok beleférnek a normál szórásba. Érdemi nerfhez tartósan, statisztikailag szokatlan teljesítmény kell.

### Ez csak konzisztenciát mér, nem emberi viselkedést?
Kontekstussal mér konzisztenciát, nem csak nyers hit-rate-et. A távolság súlyozása (`SafeDistance`) számít, ezért a közeli találatok és a nagyon pontos távoli spray-k eltérő elbírálást kapnak.

### Egy szerencsés spray-től hirtelen jöhet nerf?
Gyakorlatban nem. A rendszer nem bináris és nem instant ban logika; fokozatosan skáláz. Kisebb túlteljesítés legfeljebb átmeneti, kis mértékű szorzóváltozást okozhat.

### A magas pinges játékosokat bünteti?
A hálózati ingadozás sok rendszert érinthet, de ez a plugin hosszabb távú lövés-találat mintát néz, nem egyetlen késő eseményt. A rövid latency spike-ok önmagukban nem néznek ki cheat-szintű konzisztenciának.

### Ez gyakorlatilag shadow ban?
Nem. A plugin önmagában nem tilt. Konfigurálható kimenő sebzés-szorzót alkalmaz ideiglenes, "soft" mitigációként, amíg a gyanús minta fennáll.

### Mi van a nagyon erős, de legit játékosokkal?
Az alapértékek szándékosan konzervatívak, és nagy skill buffert hagynak. A szervertulajdonosok a `MaxAccuracy`, `SampleCount` és `SafeDistance` értékeket a saját közösséghez igazíthatják.

### Könnyen megkerülhető csaló oldalon?
Bármely anti-cheat támadható, de a viselkedésalapú megközelítés drágítja a megkerülést, mert tartós statisztikai mintát céloz, nem egyetlen egyszerű szignatúrát.

### Minden fegyvert ugyanúgy kezel?
Nem. A hangolás fegyverenként történik a `Weapons` részben, így külön küszöbök adhatók az eltérő recoil és tipikus harci távok alapján.

### Ez kiváltja az EAC-t vagy az admin moderációt?
Nem. Ezt inkább egy kiegészítő mitigációs rétegként érdemes kezelni. Csökkenti a gyanús viselkedés sebzés-hatását, és hasznos jelzéseket ad az adminoknak.

### Hogyan látja az admin, miért kapott valaki nerfet?
Használd az `/ac-check` és `/ac-why` parancsokat; határesetek vizsgálatához érdemes debug módot is bekapcsolni.

## Licenc

MIT License.

---
Készítette: **Mogy**





