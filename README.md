# MogyAntiCheat for Rust (Oxide/uMod)

[English docs](README.en.md) | [Source of truth](docs/SOURCE_OF_TRUTH.md)

![License](https://img.shields.io/badge/license-MIT-green)
![Version](https://img.shields.io/badge/version-1.6.8-blue)
![Game](https://img.shields.io/badge/game-Rust-orange)

## Projekt dokumentáció

- Source of truth: `docs/SOURCE_OF_TRUTH.md`
- Roadmap: `docs/ROADMAP.md`
- Public API (draft): `docs/PUBLIC_API.md`
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
- NPC-k és épületek kizárása a releváns statisztikából.
- Admin mentesség a sebzéscsökkentés alól.
- In-game admin parancsok ellenőrzéshez és resethez.

## Telepítés

1. Telepítsd az Oxide/uMod rendszert a Rust szerveredre.
2. Másold a `MogyAntiCheat.cs` fájlt a `server/<identity>/oxide/plugins/` mappába.
3. Töltsd újra a plugint vagy indítsd újra a szervert.
4. Állítsd be a küszöböket a `server/<identity>/oxide/config/MogyAntiCheat.json` fájlban.

## Konfiguráció

A konfigurációban fegyverenkénti bejegyzések vannak a `Weapons` alatt, plusz globális beállítások:

- `MissExpirySeconds`: mennyi ideig számít érvényesnek egy leadott lövés a találat párosításához.
- `DefaultLanguage`: alapértelmezett nyelv, ha nincs játékos-specifikus nyelv (`en` alap).

Fegyverenkénti paraméterek:

- `MaxAccuracy`: maximálisan megengedett találati arány (pl. `0.38 = 38%`).
- `SampleCount`: gördülő mintaméret (hány lövést tartson meg a statisztikához).
- `SafeDistance`: távolsági referencia a súlyozáshoz.

Példa:

```json
{
  "Weapons": {
    "rifle.ak": {
      "MaxAccuracy": 0.38,
      "SampleCount": 40,
      "SafeDistance": 25.0
    }
  },
  "MissExpirySeconds": 20.0,
  "DefaultLanguage": "en"
}
```

## Nyelvi testreszabás

A plugin kulcs-alapú üzeneteket használ, külön nyelvi JSON fájlokkal.

Alap fájlok:

- `oxide/lang/en/MogyAntiCheat.json`
- `oxide/lang/hu/MogyAntiCheat.json`

Lépések:

1. Szerkeszd a kívánt nyelvi JSON fájlt.
2. Állítsd a `DefaultLanguage` értéket a configban.
3. Plugin reload után ellenőrizd pl. `/ac-check` paranccsal.
4. Opcionálisan használd: `/ac-lang <nyelvkód>` az alapértelmezett nyelv váltásához.

## Parancsok (csak admin)

- `/ac-check [jatekosnev]` - Részletes statisztika egy játékosról.
- `/ac-list` - Online játékosok listázása átlag pontossággal és aktuális sebzés-szorzóval.
- `/ac-reset [jatekosnev]` - Játékos statisztikáinak törlése.
- `/ac-lang <nyelvkod>` - Alapértelmezett plugin nyelv állítása (pl. `en`, `hu`).

## Hogyan működik a sebzéscsökkentés

A plugin fegyverenként számol nerfet, majd a legalacsonyabb (legszigorúbb) szorzót alkalmazza.

- Kevés adatnál nincs büntetés (`History.Count < 10`).
- Küszöb feletti pontosságnál a büntetés mértéke függ:
  - a túllépés mértékétől,
  - és a távolság-súlyozott teljesítménytől.
- Szélsőséges esetben a kimenő sebzés 0-ra állhat.

## Megjegyzések

- Ez elsősorban mitigációs eszköz, nem teljes anti-cheat ökoszisztéma.
- Akkor működik a legjobban, ha a küszöbök a szerver PvP stílusára vannak hangolva.
- Nagy Rust combat/meta változások után érdemes újrakalibrálni az értékeket.

## Licenc

MIT License.

---
Készítette: **Mogy**
