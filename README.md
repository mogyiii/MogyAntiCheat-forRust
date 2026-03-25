# MogyAntiCheat for Rust (Oxide/uMod)

[English docs](README.en.md) | [Source of truth](docs/SOURCE_OF_TRUTH.md)

![License](https://img.shields.io/badge/license-MIT-green)
![Version](https://img.shields.io/badge/version-1.7.0-blue)
![Game](https://img.shields.io/badge/game-Rust-orange)

## Projekt dokumentĂˇciĂł

- Source of truth: `docs/SOURCE_OF_TRUTH.md`
- Roadmap: `docs/ROADMAP.md`
- Public API: `docs/PUBLIC_API.md`
- Config sĂ©ma: `docs/CONFIG_SCHEMA.md`
- RFC sablon: `docs/RFCs/TEMPLATE.md`
- VĂˇltozĂˇsnaplĂł: `CHANGELOG.md`

A MogyAntiCheat egy statisztikai alapĂş anti-cheat plugin Rust (Oxide/uMod) szerverekhez.  
A hagyomĂˇnyos azonnali tiltĂˇs helyett dinamikusan csĂ¶kkenti a gyanĂşs jĂˇtĂ©kosok kimenĹ‘ sebzĂ©sĂ©t, Ă­gy kisebb a false positive talĂˇlatokbĂłl adĂłdĂł kĂˇr.

## MĹ±kĂ¶dĂ©si elv

A plugin nem fĂˇjlokat vagy folyamatokat vizsgĂˇl, hanem harci esemĂ©nyekbĹ‘l dolgozik.

1. Minden lĂ¶vĂ©s egy ideiglenes vĂˇrĂłlistĂˇra kerĂĽl.
2. A valĂłs jĂˇtĂ©kos-jĂˇtĂ©kos talĂˇlatok vissza vannak pĂˇrosĂ­tva a friss lĂ¶vĂ©sekhez.
3. FegyverenkĂ©nt gĂ¶rdĂĽlĹ‘ pontossĂˇgi statisztika kĂ©szĂĽl.
4. A tĂˇvoli talĂˇlatok nagyobb sĂşlyt kapnak, mint a kĂ¶zeliek.
5. KĂĽszĂ¶bĂˇtlĂ©pĂ©s esetĂ©n a kimenĹ‘ sebzĂ©s fokozatosan csĂ¶kkenhet akĂˇr 0-ig.

## FĹ‘bb jellemzĹ‘k

- IdĹ‘ablakos lĂ¶vĂ©s-talĂˇlat pĂˇrosĂ­tĂˇs.
- FegyverenkĂ©nt kĂĽlĂ¶n finomhangolhatĂł kĂĽszĂ¶bĂ¶k.
- TartĂłs adattĂˇrolĂˇs ĂşjraindĂ­tĂˇs utĂˇn is (`oxide/data/MogyAntiCheat_Stats.json`).
- NPC-k Ă©s Ă©pĂĽletek kizĂˇrĂˇsa a relevĂˇns statisztikĂˇbĂłl.
- Admin mentessĂ©g a sebzĂ©scsĂ¶kkentĂ©s alĂłl.
- In-game admin parancsok ellenĹ‘rzĂ©shez Ă©s resethez.

## TelepĂ­tĂ©s

1. TelepĂ­tsd az Oxide/uMod rendszert a Rust szerveredre.
2. MĂˇsold a `MogyAntiCheat.cs` fĂˇjlt a `server/<identity>/oxide/plugins/` mappĂˇba.
3. TĂ¶ltsd Ăşjra a plugint vagy indĂ­tsd Ăşjra a szervert.
4. ĂllĂ­tsd be a kĂĽszĂ¶bĂ¶ket a `server/<identity>/oxide/config/MogyAntiCheat.json` fĂˇjlban.

## KonfigurĂˇciĂł

A konfigurĂˇciĂłban fegyverenkĂ©nti bejegyzĂ©sek vannak a `Weapons` alatt, plusz globĂˇlis beĂˇllĂ­tĂˇsok:

- `MissExpirySeconds`: mennyi ideig szĂˇmĂ­t Ă©rvĂ©nyesnek egy leadott lĂ¶vĂ©s a talĂˇlat pĂˇrosĂ­tĂˇsĂˇhoz.
- `DefaultLanguage`: alapĂ©rtelmezett nyelv, ha nincs jĂˇtĂ©kos-specifikus nyelv (`en` alap).

FegyverenkĂ©nti paramĂ©terek:

- `MaxAccuracy`: maximĂˇlisan megengedett talĂˇlati arĂˇny (pl. `0.38 = 38%`).
- `SampleCount`: gĂ¶rdĂĽlĹ‘ mintamĂ©ret (hĂˇny lĂ¶vĂ©st tartson meg a statisztikĂˇhoz).
- `SafeDistance`: tĂˇvolsĂˇgi referencia a sĂşlyozĂˇshoz.

PĂ©lda:

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

## Nyelvi testreszabĂˇs

A plugin kulcs-alapĂş ĂĽzeneteket hasznĂˇl, kĂĽlĂ¶n nyelvi JSON fĂˇjlokkal.

Alap fĂˇjlok:

- `oxide/lang/en/MogyAntiCheat.json`
- `oxide/lang/hu/MogyAntiCheat.json`

LĂ©pĂ©sek:

1. Szerkeszd a kĂ­vĂˇnt nyelvi JSON fĂˇjlt.
2. ĂllĂ­tsd a `DefaultLanguage` Ă©rtĂ©ket a configban.
3. Plugin reload utĂˇn ellenĹ‘rizd pl. `/ac-check` paranccsal.
4. OpcionĂˇlisan hasznĂˇld: `/ac-lang <nyelvkĂłd>` az alapĂ©rtelmezett nyelv vĂˇltĂˇsĂˇhoz.

## Parancsok (csak admin)

- `/ac-check [jatekosnev]` - RĂ©szletes statisztika egy jĂˇtĂ©kosrĂłl.
- `/ac-list` - Online jĂˇtĂ©kosok listĂˇzĂˇsa Ăˇtlag pontossĂˇggal Ă©s aktuĂˇlis sebzĂ©s-szorzĂłval.
- `/ac-reset [jatekosnev]` - JĂˇtĂ©kos statisztikĂˇinak tĂ¶rlĂ©se.
- `/ac-lang <nyelvkod>` - AlapĂ©rtelmezett plugin nyelv ĂˇllĂ­tĂˇsa (pl. `en`, `hu`).

## Hogyan mĹ±kĂ¶dik a sebzĂ©scsĂ¶kkentĂ©s

A plugin fegyverenkĂ©nt szĂˇmol nerfet, majd a legalacsonyabb (legszigorĂşbb) szorzĂłt alkalmazza.

- KevĂ©s adatnĂˇl nincs bĂĽntetĂ©s (`History.Count < 10`).
- KĂĽszĂ¶b feletti pontossĂˇgnĂˇl a bĂĽntetĂ©s mĂ©rtĂ©ke fĂĽgg:
  - a tĂşllĂ©pĂ©s mĂ©rtĂ©kĂ©tĹ‘l,
  - Ă©s a tĂˇvolsĂˇg-sĂşlyozott teljesĂ­tmĂ©nytĹ‘l.
- SzĂ©lsĹ‘sĂ©ges esetben a kimenĹ‘ sebzĂ©s 0-ra Ăˇllhat.

## MegjegyzĂ©sek

- Ez elsĹ‘sorban mitigĂˇciĂłs eszkĂ¶z, nem teljes anti-cheat Ă¶koszisztĂ©ma.
- Akkor mĹ±kĂ¶dik a legjobban, ha a kĂĽszĂ¶bĂ¶k a szerver PvP stĂ­lusĂˇra vannak hangolva.
- Nagy Rust combat/meta vĂˇltozĂˇsok utĂˇn Ă©rdemes ĂşjrakalibrĂˇlni az Ă©rtĂ©keket.

## Licenc

MIT License.

---
KĂ©szĂ­tette: **Mogy**

