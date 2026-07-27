# MogyAntiCheat Tesztelési Útmutató

Ez a dokumentum a MogyAntiCheat teljes tesztelési stratégiáját tartalmazza az összes funkció és szélsőséges eset validálásához.

**Plugin verzió:** 1.10.0  
**API verzió:** 1.3.0  
**Runtime:** Oxide/uMod, Carbon

> Megjegyzés: az alábbi tesztesetek 1.9.8-hoz (M9 mérföldkő) készültek. Az 1.10.0 újdonságok
> (SteamID hashelés, opt-in heti telemetria riport) **még nincsenek lefedve**, teszteset kell hozzájuk.

---

## Tesztkörnyezet beállítása

### Előfeltételek

- Rust game szerver (legújabb verzió ajánlott)
- Oxide/uMod v2.0.44+ VAGY Carbon
- Admin/moderátor in-game fiók
- Tesztjátékos fiókok (2–5 többjátékos forgatókönyvekhez)

### Telepítés ellenőrzése

1. Másold `MogyAntiCheat.cs`-t `oxide/plugins/` vagy `carbon/plugins/` mappába
2. Töltsd újra: `/oxide.reload MogyAntiCheat` vagy Carbon megfelelő parancs
3. Ellenőrizd az indítási naplót: `[MogyAntiCheat] <verzió> initialized`
4. Ellenőrizd a config fájl létrehozása: `oxide/config/MogyAntiCheat.json`
5. Ellenőrizd az adatmappát: `oxide/data/` runtime stats fájlért

### Alapértelmezett config

Az alapértelmezett config-nak tartalmaznia kell:
- `PublicApi.Enabled = true`
- `PublicApi.ApiVersion = 1.3.0`
- `KDATracking.Enabled = true`
- `PingMonitoring.Enabled = true`
- `LagswitchDetection.Enabled = true`
- `MLService.Enabled = false` (M9 teszthez, vagy konfigurálj ha elérhető)

---

## 1. rész: Lövés nyomkövetés & Pontosság detekció

### 1.1 teszt: Alapvető lövésregisztráció

**Cél:** Ellenőrizd, hogy a lövések nyomon vannak követve és tárolva vannak.

**Lépések:**
1. Admin spawn-ol A tesztjátékost biztonságos zónában
2. Admin spawn-ol B tesztjátékost közeli helyen (20m-re)
3. A játékos 10-szer lő B-re puska-val
4. Admin futtatja: `/ac-check A`
5. Ellenőrizd, hogy "puska" fegyver megjelenik ~10 lövéssel

**Várható eredmény:**
- Lövések száma = 10
- Pontosság % megjelenítve (0% lesz, ha nincs találat)

---

### 1.2 teszt: Találat párosítás

**Cél:** Ellenőrizd a lövés-találat párosítás helyes működését.

**Lépések:**
1. A játékos 20-szor lő B-re (puska, 20–50m-es távolságok)
2. B játékos kb. 15 lövésből találatot kap (a többi szándékos tévesztés)
3. Admin futtatja: `/ac-check A`
4. Ellenőrizd a pontosság kiszámítást

**Várható eredmény:**
- Pontosság ≈ 75% (15/20)
5. Lövéstörténet keveredett találatokat és tévesztéseket mutat
- Fegyver helyes távolságot regisztrál a találatokhoz

---

### 1.3 teszt: Nagyobb távolság súlyozása

**Cél:** Ellenőrizd, hogy a hosszú távolságú lövések nagyobb súlyt kapnak.

**Lépések:**
1. A játékos 30-szor lő B-re 80m-ről
2. Mind a 30 lövés találat (aimbot-szerű pontosság távolságban)
3. Admin futtatja `/ac-check A`-t és ellenőrzi a pontosság % és súlyozott pontszám
4. Összehasonlít C játékossal, aki 30-szor lő 15m-ről, mind encontra

**Várható eredmény:**
- A játékos: pontosság ~100%, súlyozott pontszám > 1.5
- C játékos: pontosság ~100%, súlyozott pontszám ≈ 1.0
- A játékos súlyozott pontszáma magasabb a távolság miatt

---

### 1.4 teszt: Függőben lévő lövés lejárta

**Cél:** Ellenőrizd a lövések lejárnak `MissExpirySeconds` után.

**Lépések:**
1. A játékos 5-ször lő B-re (puska)
2. Várj 25 másodpercet (alapértelmezett lejárat 20mp)
3. B játékos találatot kap (sebzést szenved)
4. Admin futtatja: `/ac-check A`
5. Ellenőrizd, hogy a találat NINCS párosítva a lejárt függőben lövésekhez

**Várható eredmény:**
- Találat regisztrálva, de függőben lövések már lejártak
- Új találat bejegyzés létrehozva az ősi függőben lövések párosítása nélkül
- Pontosság nem inflálódott a késleltetett találat miatt

---

## 2. rész: Gyanú & Sebzéscsökkentés

### 2.1 teszt: Gyanú esemény излучения

**Cél:** Ellenőrizd, hogy `OnMogyAcSuspicion` hook tüzel küszöb túllépése után.

**Beállítás:**
- Puska `MaxAccuracy = 0.70` konfigurálása
- Puska `SampleCount = 10` konfigurálása

**Lépések:**
1. A játékos 20-szor lő B-re (puska)
2. 18 találat (90% pontosság, meghaladja 70% küszöböt)
3. Admin futtatja: `/ac-check A`
4. Külső plugin listener (ha konfigurálva) kaphatott gyanú eseményt

**Várható eredmény:**
- `/ac-check` "SUSPICIOUS" státuszt mutat puska-hoz
- Külső plugin naplózza az eseményt pontossággal, maxAccuracy, súlyozott pontszám stb.

---

### 2.2 teszt: Sebzéscsökkentés alkalmazása

**Cél:** Ellenőrizd, hogy sebzés csökken, ha játékos gyanús.

**Lépések:**
1. Puska `MaxAccuracy = 0.70` konfigurálása
2. A játékos gyanús állapotba jut: 20 lövés, 18 találat (90% acc)
3. A játékos puska-val lő B-re; B játékos sebzést kap
4. Rögzítsd a sebzést: `originalDamage`
5. Admin futtatja debug naplót vagy webhook-ot `scaledDamage` megtekintéséhez
6. Szorzó kiszámítása = `scaledDamage / originalDamage`

**Várható eredmény:**
- Szorzó < 1.0 (pl. 0.60–0.80 90% pontossághoz)
- B játékos túléli a támadásokat, amely normál végzetes lenne
- Minden lövés csökkentett sebzést mutat az ölési naplóban

---

### 2.3 teszt: Admin kivételezés

**Cél:** Ellenőrizd, hogy adminok mentesülnek a sebzéscsökkentés alól.

**Lépések:**
1. Puska `MaxAccuracy = 0.50` beállítása
2. Admin fiók gyanú állapotba jut: 20 lövés, 18 találat (90% acc)
3. Admin lő B-re; sebzést méri
4. Non-admin C játékos azonos gyanú állapotba jut
5. C játékos lő B-re; sebzést méri

**Várható eredmény:**
- Admin sebzése: teljes (nem csökkentett)
- C játékos sebzése: csökkentett
- Admin csak `DebugMode = true`-ban csökkenthető

---

### 2.4 teszt: Hard Clamps

**Cél:** Ellenőrizd, hogy büntető nincs erős körülmények között (hamis pozitív védelem).

**Lépések:**
1. A játékos: 20 lövés, 19 találat (95% pontosság) 200m távolságban
2. Súlyozott pontszám = 2.5 (nagyon hosszú távolság)
3. Admin futtatja `/ac-check A`-t
4. Ellenőrizd, hogy büntető 0% (nincs csökkentés hard clamp miatt)

**Várható eredmény:**
- Magas pontosság + hosszú távolság ellenére, nerf = 0%
- Ok: pontosság > 95% ÉS súlyozott pontszám > 1.2 indítja a hard clamp-ot

---

## 3. rész: K/D/A nyomkövetés

### 3.1 teszt: Ölésbeli számláló

**Cél:** Ellenőrizd a játékosonkénti ölések számlálása.

**Lépések:**
1. A játékos öl B-t (puska, fejlövés)
2. A játékos öl C-t (puska)
3. A játékos öl D-t (más fegyver)
4. Admin futtatja `/ac-stats A`-t

**Várható eredmény:**
- Ölések = 3
- Minden fegyver megjelenik a statisztikában

---

### 3.2 teszt: Halál számláló

**Cél:** Ellenőrizd a halálok számlálása.

**Lépések:**
1. B játékos meghal A-nak
2. B játékos meghal C-nak
3. Admin futtatja `/ac-stats B`-t

**Várható eredmény:**
- Halálok = 2
- KDR helyesen mutatva

---

### 3.3 teszt: Assziszt követés

**Cél:** Ellenőrizd az aszisztenciákat a sebzés közreműködői kapják.

**Lépések:**
1. A játékos 40 sebzést okoz B-nek
2. C játékos 60 sebzést okoz B-nek
3. D játékos végzetes lövést ad le, megöli B-t
4. Admin futtatja `/ac-stats A`, `/ac-stats C`, `/ac-stats D`-t

**Várható eredmény:**
- D játékos: Ölések +1
- A játékos: Aszisztenciák +1
- C játékos: Aszisztenciák +1
- B játékos: Halálok +1

---

## 4. rész: Ping monitorozás & Anomáliadetekció

### 4.1 teszt: Ping alapvonal megállapítása

**Cél:** Ellenőrizd, hogy ping alapvonal kiszámítódik küszöbminta után.

**Config:** `PingBaselineSamples = 50` (kódállandó)

**Lépések:**
1. A játékos csatlakozik szerverhez (ping alapvonal még nincs)
2. A játékos 50 lövést ad le (alapvonal frissítés minden lövésnél)
3. Admin futtatja `/ac-stats A`-t
4. Ellenőrizd "Ping: Ping alapvonal megállapítva" (vagy hasonló)

**Várható eredmény:**
- 50. lövés után: alapvonal EMA, stddev mutatva
- Min/max ping regisztrálva
- Minta szám = 50+

---

### 4.2 teszt: Ping anomáliadetekció

**Cél:** Ellenőrizd a kiugrás detekció működését.

**Config:** `PingMonitoring.AnomalyThresholdStdDev = 2.5`

**Lépések:**
1. A játékos alapvonalat hoz létre: 70ms átlag, 5ms stddev
2. A játékos normálisan lő (ping marad ~70ms) 20 lövésnél
3. Hálózati kiugrás: A játékos 140ms ping-gel lő (70ms kiugrás)
4. Kiugrás küszöb = 70 + (2.5 * 5) = 82.5ms
5. Tényleges ping (140ms) > küszöb → anomália detektálva
6. Admin futtatja `/ac-stats A`-t

**Várható eredmény:**
- Ping anomáliák száma növekszik
- Esemény naplózva, ha eseménynaplózás engedélyezve

---

### 4.3 teszt: Ping alapvonal frissítés hook

**Cél:** Ellenőrizd, hogy `OnMogyAcPingBaselineUpdate` tüzel alapvonal megállapítása után.

**Lépések:**
1. Külső plugin listener feliratkozik hookra
2. Új játékos csatlakozik, 50+ lövést ad le
3. Ellenőrizd, hogy hook kapott-e payload-dal: `playerId`, `avg`, `min`, `max`, `stddev`, `sampleCount`

**Várható eredmény:**
- Hook pontosan egyszer tüzel játékosazonként
- Payload mezők helyesen vannak feltöltve

---

## 5. rész: Lagswitch detekció

### 5.1 teszt: Lagswitch incidens rögzítése

**Cél:** Ellenőrizd, hogy lagswitch incidens detektálódik és naplózódik.

**Config:** `LagswitchDetection.Threshold = 0.70`

**Lépések:**
1. A játékos alapvonalat hoz létre: 80ms, stddev 5ms
2. A játékos ping-je 150ms-re ugrik egy öléseseménynél
3. Ölésbeli minőség: pontosság 95%, fejlövés igaz
4. Reconnect ablak: nincs közelmúltbeli lecsatlakozás
5. Öléseseményt regisztrálnak; összetett megbízhatóság kiszámítódik
6. Ha megbízhatóság >= 0.70 → incidens regisztrálva
7. Admin futtatja `/ac-lagswitch-audit A`-t

**Várható eredmény:**
- Incidens felsorolva időbélyeggel, áldozat, fegyver, távolság
- Megbízhatósági pontszám mutatva (0.0–1.0)
- Ping kiugrás komponens mutatva
- Ölési pontosság komponens mutatva

---

### 5.2 teszt: Lagswitch mintázat detekció

**Cél:** Ellenőrizd, hogy mintázat figyelmeztetés tüzel ismételt incidensnél.

**Config:** `MinIncidentsForPattern = 3`, `PatternThreshold = 0.75`

**Lépések:**
1. A játékosnak 3 lagswitch incidensze van 24 órán belül, mind >= 0.75 megbízhatósággal
2. Admin futtatja `/ac-lagswitch-audit A`-t

**Várható eredmény:**
- Összegzés mutatja "Mintázat detektálva" figyelmeztetést
- 24 órás incidens szám >= 3
- Átlagos megbízhatóság >= 0.75

---

## 6. rész: Kézi felülbírálat & Auditnapló

### 6.1 teszt: Felülbírálat beállítása

**Cél:** Ellenőrizd, hogy kézi felülbírálat alkalmazódik és naplózódik.

**Lépések:**
1. A játékos lő, gyanús állapotba jut (80% pontosság, ~50% csökkenés)
2. Admin futtatja `/ac-override A 30`-at
3. A játékos B-re lő; sebzés mérése
4. Szorzó kiszámítása = scaledDamage / originalDamage

**Várható eredmény:**
- Sebzésszorzó = 0.70 (30% csökkentés)
- Auditnapló bejegyzés létrehozva: admin ID, admin név, cél ID, cél név, régi érték (auto), új érték (30%)

---

### 6.2 teszt: Felülbírálat törlése

**Cél:** Ellenőrizd, hogy felülbírálat törölhető.

**Lépések:**
1. A játékosnak 30%-os felülbírálat van beállítva
2. Admin futtatja `/ac-override A off`-ot
3. A játékos ismét lő; sebzés mérése

**Várható eredmény:**
- Sebzés visszatér algoritmus-számított csökkentésre (vagy nincs, ha nem gyanús)
- Auditnapló bejegyzés: új érték = "auto"

---

### 6.3 teszt: Felülbírálat elsőbbséget kap

**Cél:** Ellenőrizd, hogy kézi felülbírálat felülírja az algoritmust.

**Lépések:**
1. A játékos: algoritmus 70% csökkentést számít (nagyon gyanús)
2. Admin beállít `/ac-override A 20`-at (csak 20% csökkentés)
3. A játékos lő; sebzés mérése
4. Szorzó kiszámítása

**Várható eredmény:**
- Szorzó = 0.80 (20% csökkentés, nem 30% az algoritmusból)
- Admin választása elsőbbséget kap

---

## 7. rész: Admin parancsok

### 7.1 teszt: `/ac-dashboard`

**Cél:** Ellenőrizd az élő játékos áttekintés helyes megjelenítése.

**Lépések:**
1. Több játékos online, néhány nyomon követett
2. Admin futtatja `/ac-dashboard`-t
3. Ellenőrizd, hogy tábla mutatja: nevek, nerf %, ping, LS szám, K/D/A, felülbírálat státusz

**Várható eredmény:**
- Összes nyomon követett játékos felsorolva
- Helyes statisztika játékosonként
- Felülbírálat státusz mutatja %, ha beállítva, "-" ha nem

---

### 7.2 teszt: `/ac-chart <játékos> accuracy`

**Cél:** Ellenőrizd az ASCII pontosság diagram megjelenítése.

**Lépések:**
1. A játékosnak lövéstörténete van több fegyverhez
2. Admin futtatja `/ac-chart A accuracy`-t
3. Diagram mutatja fegyverenként pontosság sparkline-okat

**Várható eredmény:**
- Fegyverenkénti bar diagram █▓▒░ karakterekkel
- Pontosság % mutatva fegyverenként
- Lövés szám mutatva

---

### 7.3 teszt: `/ac-chart <játékos> ping`

**Cél:** Ellenőrizd a ping vizualizáció.

**Lépések:**
1. A játékosnak megállapított ping alapvonala van
2. Admin futtatja `/ac-chart A ping`-et
3. Diagram mutatja vonalzót min/átlag/max-szal

**Várható eredmény:**
- Min, átlag (EMA), max, stddev értékek
- ASCII vonalzó ▲ markerrel az átlagnál
- Minta szám mutatva

---

### 7.4 teszt: `/ac-chart <játékos> kda`

**Cél:** Ellenőrizd K/D/A bar diagram.

**Lépések:**
1. A játékosnak K=5, D=3, A=2 van
2. Admin futtatja `/ac-chart A kda`-t

**Várható eredmény:**
- Arányos bar diagram K, D, A-hoz
- KDR helyesen számítódik

---

### 7.5 teszt: `/ac-export csv`

**Cél:** Ellenőrizd a CSV export működik.

**Lépések:**
1. Több játékos nyomon követve
2. Admin futtatja `/ac-export csv`-t
3. Ellenőrizd a fájl létrehozása `oxide/data/MogyAntiCheat_Export_*.csv` alatt
4. CSV megnyitása és oszlopok ellenőrzése

**Várható eredmény:**
- Fájl létrehozva időbélyeg-nal a fájlnévben
- Oszlopok: player_id, weapon, accuracy, shots, hits, global_nerf, manual_override, kills, deaths, assists, ping_avg, ping_stddev, ping_anomalies, ls_incidents
- Egy sor minden játékos-fegyver kombinációnál
- Az adatok egyeznek `/ac-check` kimenettel

---

### 7.6 teszt: `/ac-config-tune`

**Cél:** Ellenőrizd az élő config hangolás.

**Lépések:**
1. Admin futtatja `/ac-config-tune MissExpirySeconds 25`-öt
2. Ellenőrizd a config fájl frissítése (nézd meg `oxide/config/MogyAntiCheat.json`)
3. Lövések adása új lejárati ablakban; ellenőrizd a viselkedés megváltozott-e

**Várható eredmény:**
- Config frissítve memóriában és perzisztálva
- Üzenet mutatja régi és új értéket
- Plugin azonnal új értéket használ (nincs újratöltés szükséges)

---

### 7.7 teszt: `/ac-suggest`

**Cél:** Ellenőrizd az ML ajánlás lekérésér (ha ML engedélyezve).

**Lépések:**
1. `MLService.Enabled = true` konfigurálása ML szerviz végponttal
2. Admin futtatja `/ac-suggest`-et
3. Várd a választ

**Várható eredmény:**
- Javaslatok megjelenítve (ha ML szerviz elérhető)
- Formátum: paraméter, jelenlegi érték, javasolt érték, megbízhatóság %
- Ha szerviz nem elérhető: hibaüzenet

---

## 8. rész: Public API & Hookok

### 8.1 teszt: Külső plugin hook feliratkozás

**Cél:** Ellenőrizd, hogy külső pluginok feliratkozhatnak hookra.

**Beállítás:** Hozz létre egy kis teszter plugint, amely naplózza a hook eseményeket.

**Lépések:**
1. Teszter plugin feliratkozik `OnMogyAcSuspicion`-re
2. A játékos gyanús állapotba jut
3. Ellenőrizd a teszter plugin naplóit

**Várható eredmény:**
- Hook tüzel helyes payload-szal
- Payload tartalmazza: apiVersion, playerId, weaponShortName, accuracy, maxAccuracy, weightedScore, suggestedNerf, sampleCount, pingBaselineAvg, pingBaselineStdDev, timestampUtc

---

### 8.2 teszt: Lekérdezési módszer `GetPlayerAcState`

**Cél:** Ellenőrizd a read-only lekérdezés helyes adatot visszaadja.

**Lépések:**
1. Hívd `GetPlayerAcState(playerID)`-t teszter pluginból
2. Ellenőrizd a visszaadott adatstruktúra

**Várható eredmény:**
- Visszaad dict-et: apiVersion, playerId, globalNerf, weapons[], pingAvg, pingStdDev, pingAnomalyCount, kills, deaths, assists, timestampUtc
- Weapons[] tartalmazza: weaponShortName, accuracy, sampleCount, weightedScore, maxAccuracy, safeDistance, isSuspicious, suggestedNerf

---

### 8.3 teszt: Lekérdezési módszer `GetPlayerKDAStats`

**Cél:** Ellenőrizd a K/D/A lekérdezés működik.

**Lépések:**
1. Hívd `GetPlayerKDAStats(playerID)`-t teszter pluginból
2. Ellenőrizd a visszaadott értékek egyeznek `/ac-stats`-szal

**Várható eredmény:**
- Visszaad: kills, deaths, assists, kdaRatio
- kdaRatio = kills / deaths (vagy kills ha deaths=0)

---

### 8.4 teszt: Lekérdezési módszer `GetMLPenaltySuggestion`

**Cél:** Ellenőrizd az ML büntető javaslat cacheing (M9 funkció).

**Lépések:**
1. ML szerviz konfigurálása
2. Gyanú esemény indítása → ML javaslatot kér le
3. Hívd `GetMLPenaltySuggestion(playerId, weapon)`-t a cache lejárta előtt
4. Hívd újra cache lejárata után (`CacheSuggestionsSeconds`)

**Várható eredmény:**
- Első hívás azonnal cache értéket visszaad
- Második hívás frissített értéket ad vissza (vagy null ha cache lejárt)

---

## 9. rész: Adatperzisztencia

### 9.1 teszt: Statisztika perzisztencia restart után

**Cél:** Ellenőrizd, hogy játékos statisztika túléli a szerver restart-ot.

**Lépések:**
1. A játékos 50-szer lő, 80% pontosságot ér el (puska)
2. Szerver restart (plugin újratöltés)
3. A játékos ismét lő; futtatja `/ac-check A`-t

**Várható eredmény:**
- Előző 50 lövés helyreállítódik `MogyAntiCheat_Stats.json`-ből
- Új lövések hozzáadódnak a meglévő történethez
- Pontosság helyesen újra kiszámítódik

---

### 9.2 teszt: KDA perzisztencia

**Cél:** Ellenőrizd a K/D/A statisztika perzisztenciáját.

**Lépések:**
1. A játékosnak 5 ölése, 3 halála, 2 aszisztenzia van
2. Szerver restart
3. Futtatja `/ac-stats A`-t

**Várható eredmény:**
- K/D/A értékek helyreállítódtak
- KDR változatlan

---

### 9.3 teszt: Kézi felülbírálat perzisztencia (várakozás nélkül)

**Cél:** Ellenőrizd, hogy felülbírálat NINCS perzisztálva (runtime-only design).

**Lépések:**
1. Beállított `/ac-override A 50`
2. Szerver restart
3. A játékos lő

**Várható eredmény:**
- Felülbírálat törlődik restart után
- Sebzés az algoritmus szerint működik (nem kézi felülbírálat)

---

## 10. rész: Webhook integráció (opcionális)

### 10.1 teszt: Webhook esemény sorba állítása

**Cél:** Ellenőrizd az eseményeket sorba állítása szállításra.

**Beállítás:** `Webhook.Enabled = true` konfigurálása mock végponttal.

**Lépések:**
1. A játékos gyanú eseményt indít
2. B játékos büntető eseményt indít
3. Ellenőrizd a webhook sort debug naplókon keresztül

**Várható eredmény:**
- Események sorba állítva helyes payload struktúrával
- Események tartalmazzák: esemény típus, player_id, fegyver, megbízhatóság, időbélyeg stb.

---

### 10.2 teszt: Discord webhook kompatibilitás

**Cél:** Ellenőrizd a Discord-specifikus payload formázás.

**Beállítás:** Discord webhook végpont konfigurálása.

**Lépések:**
1. Gyanú esemény indítása
2. Discord csatorna ellenőrzése formázott üzenethez

**Várható eredmény:**
- Üzenet tartalmazza a username mezőt (admin vagy plugin)
- Content mező olvasható összegzéssel
- Nincsenek nyerssuron JSON kivetítések

---

## 11. rész: Nemzetköziesítés (i18n)

### 11.1 teszt: Nyelv váltás

**Cél:** Ellenőrizd az üzenetek lokalizálódása.

**Lépések:**
1. Alapértelmezett nyelv: Angol
2. Admin futtatja `/ac-lang hu`-t
3. Admin futtatja `/ac-help`-et
4. Ellenőrizd a kimenet magyar

**Várható eredmény:**
- Összes üzenet lefordítva
- Támogatott nyelvek felsorolva érvénytelen nyelvnél

---

### 11.2 teszt: Fallback lánc

**Cél:** Ellenőrizd a fallback stratégia működik.

**Beállítás:** Töröljön egy lang kulcsot a magyar fájlból a fallback teszteléshez.

**Lépések:**
1. Nyelvbeállítás magyaros
2. Az elmaradó kulcs indítása
3. Ellenőrizd a fallback-et angolra (vagy alapértelmezett)

**Várható eredmény:**
- Elmaradó kulcs helyesen fallback-kel
- Nincsenek hibák

---

## 12. rész: Teljesítmény & Load tesztelés

### 12.1 teszt: Magas játékosszám

**Cél:** Ellenőrizd a plugin kezeli 50+ játékost lag nélkül.

**Beállítás:** 50+ tesztjátékos spawn-olása vagy magas játékosszám szimulálása.

**Lépések:**
1. Összes játékos gyorsan lő fegyvereket
2. Szerver FPS/TPS figyelése
3. RAM használat ellenőrzése
4. `/ac-list` futtatása időnként

**Várható eredmény:**
- Nem TPS csökkenés 10 alatt
- RAM használat < 500MB marad
- Parancsok normálisan válaszolnak

---

### 12.2 teszt: Nagy lövéstörténet

**Cél:** Ellenőrizd a plugin kezel játékosokat több ezer regisztrált lövéssel.

**Beállítás:** A játékos 5000+ lövést ad le (pl. gyors spam-en keresztül).

**Lépések:**
1. A játékos folyamatosan lő (históriát `SampleCount`-vel felemészti)
2. Futtatja `/ac-check A`-t
3. CSV export
4. Teljesítmény marad normális

**Várható eredmény:**
- Történet limitálva `SampleCount`-hez (pl. 100)
- Nincs memória szivárgás
- Pontosság kiszámítás azonnali

---

## 13. rész: Szélsőséges esetek

### 13.1 teszt: NPC/AI ellenségek

**Cél:** Ellenőrizd az NPC találatok NINCSENEK nyomon követve játékos találatokként.

**Lépések:**
1. NPC spawn-ol (tudós, zombi)
2. A játékos lő az NPC-re
3. NPC-t találat éri; admin futtatja `/ac-check A`-t

**Várható eredmény:**
- NPC-re lövések nincsenek regisztrálva játékos statban
- Csak valódi játékos-játékos párosítások követve

---

### 13.2 teszt: Szerkezet sebzése

**Cél:** Ellenőrizd a szerkezet/épület sebzése ignorálódik.

**Lépések:**
1. A játékos kövét lő egy falhoz
2. Fal sebzést kap
3. Futtatja `/ac-check A`-t

**Várható eredmény:**
- Épület blokk sebzése ignorálva
- Nincsenek lövések regisztrálva épület találatokhoz

---

### 13.3 teszt: Játékos lecsatlakozás a lövéstörténet alatt

**Cél:** Ellenőrizd a megfelelő takarítás lecsatlakozásnál.

**Lépések:**
1. A játékos lő, történetet épít
2. A játékos lecsatlakozik
3. A játékos visszacsatlakozik új karakterként (más ID)
4. Ellenőrizd az öreg adatok nem kerülnek újra felhasználásra

**Várható eredmény:**
- Öreg játékos ID statisztika törlődik az aktív memóriából (vagy elszigetelve)
- Újra csatlakozó karakter frissen indul
- Nincsenek kereszt-szennyeződések

---

### 13.4 teszt: Szélsőségesen hosszú távolságú lövések

**Cél:** Ellenőrizd a hosszú távolság súlyozása nem törik szélsőséges távolságban.

**Lépések:**
1. A játékos 500m+ távolságból lő B-re
2. Admin futtatja `/ac-check A`-t

**Várható eredmény:**
- Távolság helyesen regisztrálva
- Súlyozott pontszám kiszámítódik hibák nélkül
- Nincsenek osztás nullával vagy túlcsordulás

---

## 14. rész: Carbon vs. Oxide runtime kompatibilitás

### 14.1 teszt: Adatútvonal feloldása (Carbon)

**Cél:** Ellenőrizd a plugin megtalálja a helyes adatkönyvtárat Carbon-on.

**Beállítás:** Deploy Carbon szerverhez.

**Lépések:**
1. Ellenőrizd a szerver naplóit az indítási után
2. Ellenőrizd az adatkönyvtár naplózva
3. Erősítsd meg a config és adatfájlok a helyes helyen létrehozódnak

**Várható eredmény:**
- Naplók mutatják: `[MogyAC] Data path: ...`
- Fájlok létrehozva Carbon adatkönyvtárában
- Config helyesen betöltődik

---

### 14.2 teszt: Hookok mindkét runtime-on működnek

**Cél:** Ellenőrizd `OnWeaponFired`, `OnEntityTakeDamage` stb. tüzelden mindkettőn.

**Beállítás:** Ugyanaz a plugin deploy mindkét Oxide és Carbon szerverhez.

**Lépések:**
1. Játékos lő Oxide szerverhez → plugin naplózza lövést
2. Játékos lő Carbon szerverhez → plugin naplózza lövést
3. Mindkettőnek hasonlóan kellene nyomon követniük

**Várható eredmény:**
- Mindkét runtime naplózza az eseményeket
- Statisztika azonosan gyűjtve

---

## Tesztvégrehajtási ellenőrző lista

```
Alapvető funkciók:
[ ] Lövés nyomkövetés regisztrál lövéseket
[ ] Találat párosítás párosít lövéseket találatokkal
[ ] Gyanú detektálódik a küszöb felett
[ ] Sebzéscsökkentés helyesen alkalmazódik

Admin parancsok:
[ ] /ac-dashboard megjelenít összes játékost
[ ] /ac-override beállít és törül felülbírálatok
[ ] /ac-chart renderel pontosság, ping, KDA-t
[ ] /ac-export csv fájlt ír
[ ] /ac-config-tune frissít config-ot élően
[ ] /ac-suggest lekérdez ML szerviz

Adatok:
[ ] Játékos stat perzisztálódik restart után
[ ] KDA perzisztálódik restart után
[ ] Felülbírálat NEM perzisztálódik (runtime-only)
[ ] CSV export helyes adatot tartalmaz

Haladó:
[ ] K/D/A nyomkövetés működik
[ ] Ping alapvonal megállapítódik
[ ] Lagswitch detekció tüzel
[ ] Public API hookok működnek
[ ] Külső pluginok lekérdezhetik státuszt

Kompatibilitás:
[ ] Oxide/uMod runtime működik
[ ] Carbon runtime működik
[ ] Webhook szállítás működik (ha engedélyezve)
[ ] Nyelv váltás működik

Load:
[ ] 50+ játékos nincs lag
[ ] Nagy lövéstörténet kezelt
[ ] Teljesítmény elfogadható
```

---

## Ismert korlátok & buktatók

1. **Függőben lövés távolság = 0** — Csak megerősített találatok tárolnak valódi távolságot. Design trade-off a teljesítmény miatt.

2. **Debounce ablak (0.05mp)** — Gyors többtalálat események ezen az ablakon belül elrejtve lehetnek. Nem alkalmazható legtöbb játékmenet forgatókönyvben.

3. **Nincs fájl/folyamat scan** — Plugin tisztán harci-statisztikai; nem detektál külső cheat-okat.

4. **Pontosság súlyozás** — Hosszú távolság több súlyú, de közel távolság továbbra is indíthat gyanút ha minták konzisztensek.

5. **Assziszt kredit** — Csak azok a játékosok kapnak aszisztenciát, akik sebzést okoztak az ölés előtt. Építők, orvosok stb. nem kapnak auto-aszisztenciát.

6. **ML szerviz opcionális** — Plugin önmagában működik; ML csak kiegészítés.

---

## Sikerkritériumok

- [x] Összes parancs hibamentesen végrehajtódik
- [x] Adatok perzisztálódnak és helyesen helyreállítódnak
- [x] Sebzéscsökkentés megbízhatóan alkalmazódik
- [x] Admin irányítópult pontosan
- [x] Teljesítmény elfogadható 50+ játékosnál
- [x] Hookok tüzelnek és külső pluginok integrálódhatnak
- [x] Oxide és Carbon támogatott

---

**Utolsó frissítés:** 2026-05-17  
**Státusz:** Milestone M9 Teljes (1.9.8, API 1.3.0)
