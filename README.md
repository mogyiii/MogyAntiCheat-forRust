# MogyAntiCheat for Rust (Oxide/uMod)

[English docs](README.en.md) | [Source of truth](docs/SOURCE_OF_TRUTH.md)

![License](https://img.shields.io/badge/license-MIT-green)
![Version](https://img.shields.io/badge/version-1.8.0-blue)
![Game](https://img.shields.io/badge/game-Rust-orange)

## Projekt dokumentáció

- Source of truth: `docs/SOURCE_OF_TRUTH.md`
- Roadmap: `docs/ROADMAP.md`
- Public API: `docs/PUBLIC_API.md`
- Config séma: `docs/CONFIG_SCHEMA.md`
- RFC sablon: `docs/RFCs/TEMPLATE.md`
- Változásnapló: `CHANGELOG.md`

A MogyAntiCheat egy statisztikai alapú anti-cheat plugin Rust (Oxide/uMod) szerverekhez.
A hagyományos azonnali tiltás helyett dinamikusan csökkenti a gyanús játékosok kimenő sebzését, így kisebb a false positive találatokból adódó kár.

## Működési elv

A plugin nem fájlokat vagy folyamatokat vizsgál, hanem harci eseményekből dolgozik.

1. Minden lövés egy ideiglenes várólistára kerül.
2. A valós játékos-játékos találatok vissza vannak párosítva a friss lövésekhez.
3. Fegyverenként gördülő pontossági statisztika készül.
4. A távoli találatok nagyobb súlyt kapnak, mint a közeliek.
5. Küszöbátlépés esetén a kimenő sebzés fokozatosan csökkenhet akár 0-ig.

## Főbb jellemzők

- Időablakos lövés-találat párosítás.
- Fegyverenként külön finomhangolható küszöbök.
- Tartós adattárolás újraindítás után is (`oxide/data/MogyAntiCheat_Stats.json`).
- Épületek kizárása; NPC találatok debug módban bevonhatók az elemzésbe.
- Admin mentesség a sebzéscsökkentés alól.
- In-game admin parancsok ellenőrzéshez, resethez és finomhangoláshoz.

## Telepítés

1. Telepítsd az Oxide/uMod rendszert a Rust szerveredre.
2. Másold a `MogyAntiCheat.cs` fájlt a `server/<identity>/oxide/plugins/` mappába.
3. Töltsd újra a plugint vagy indítsd újra a szervert.
4. Állítsd be a küszöböket a `server/<identity>/oxide/config/MogyAntiCheat.json` fájlban.

## Konfiguráció

A konfigurációban fegyverenkénti bejegyzések vannak a `Weapons` alatt, plusz globális beállítások:

- `MissExpirySeconds`: mennyi ideig számít érvényesnek egy leadott lövés a találat párosításához.
- `DefaultLanguage`: alapértelmezett plugin nyelv (`en` alap).
- `DebugMode`: debug logok be/ki (`false` alap).
- `PublicApi`: bővítmény API beállítások.

Fegyverenkénti paraméterek:

- `MaxAccuracy`: maximálisan megengedett találati arány (pl. `0.38 = 38%`).
- `SampleCount`: gördülő mintaméret (hány lövést tartson meg a statisztikához).
- `SafeDistance`: távolsági referencia a súlyozáshoz.

## Nyelvi testreszabás

Alap fájlok:

- `oxide/lang/en/MogyAntiCheat.json`
- `oxide/lang/hu/MogyAntiCheat.json`

Lépések:

1. Szerkeszd a kívánt nyelvi JSON fájlt.
2. Állítsd a `DefaultLanguage` értéket a configban, vagy használd in-game: `/ac-lang <nyelvkód>`.
3. Ellenőrizd pl. `/ac-check` paranccsal.

## Parancsok (csak admin)

- `/ac-check [jatekosnev]` - Részletes statisztika egy játékosról.
- `/ac-list` - Online játékosok listázása átlag pontossággal és aktuális sebzés-szorzóval.
- `/ac-reset [jatekosnev]` - Játékos statisztikáinak törlése.
- `/ac-lang <nyelvkod>` - Alapértelmezett plugin nyelv váltása (pl. `en`, `hu`).
- `/ac-debug <on|off>` - Debug mód ki/bekapcsolása (bekapcsolva adminra is mehet nerf, és minden nem épület combat entity találatai beleszámítanak).
- `/ac-weapon <fegyverShortName|active> <MaxAccuracy|SampleCount|SafeDistance> <ertek>` - Fegyverértékek módosítása in-game (és mentése configba).
- `/ac-debug-log [clear]` - Debug log fájl útvonala vagy törlése.
- `/ac-why [weaponShortName|active]` - Megmutatja, miért (nem) aktiválódik nerf az adott fegyvernél.
- `/ac-help` - Elérhető admin parancsok listázása.

Példák:

- `/ac-weapon rifle.ak MaxAccuracy 0.36`
- `/ac-weapon active SafeDistance 30`
- `/ac-debug on`

## Licenc

MIT License.

---
Készítette: **Mogy**
