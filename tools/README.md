# MogyAntiCheat — Elemzési eszközök

## analyze.py — Játékos vizualizáció & Excel riport

Beolvassa a `/ac-export csv` által generált CSV-t és automatikusan:
- **6 scatter plot diagramot** generál PNG formátumban
- **Excel munkafüzetet** hoz létre beágyazott diagramokkal, színkódolt táblákkal

### Telepítés

```bash
pip install pandas matplotlib seaborn openpyxl scikit-learn
```

### Használat

```bash
# Szervered oxide/data/ mappájából másold ki a CSV-t, majd:
python analyze.py MogyAntiCheat_Export_20260517_123456.csv

# Egyedi kimeneti névvel:
python analyze.py MogyAntiCheat_Export_20260517_123456.csv --output szerver1_riport
```

---

## Generált diagramok

### 1. Pontosság szigetek (`1_accuracy_islands.png`)

**Mit mutat:** Átlag pontosság (X) vs. legjobb fegyveren mért max pontosság (Y)

```
Max acc%
  100 |                         🔴🔴
   90 |                     🔴🔴
   80 |               🟠🟠
   70 |         🟡🟡
   60 |   🟢🟢🟢🟢🟢
      +────────────────────────────
          40   60   80   100  Átlag acc%

🟢 = Normál játékosok → bal-alső "sziget"
🔴 = Gyanús/cheateres → jobb-felső "sziget"
```

**Amit keresünk:** A játékosok két jól elkülönülő csoportra (szigetre) válnak szét.
- **Normál cluster**: alacsony-közepes pontosság, logikus távolság az X és Y között
- **Gyanús cluster**: mindkét tengely magas, egymáshoz közel → aimbot-szerű konzisztencia

**Pontnagyság** = lövések száma (nagyobb pont = megbízhatóbb adat)

---

### 2. Pontosság vs Ping anomáliák (`2_accuracy_vs_ping_anomalies.png`)

**Mit mutat:** Átlag pontosság (X) vs. ping anomáliák száma (Y)

```
Ping
anomália
   20 |               🔴
   15 |         🔴   🔴
   10 |     🟠
    5 |  🟢🟢🟢🟢
    0 |  🟢🟢🟢
      +──────────────────
          40  60  80  100  Pontosság%

Lagswitch veszélyzóna: jobb-felső negyed
(magas pontosság ÉS sok ping anomália)
```

**Amit keresünk:** Lagswitch gyanús játékosok a jobb-felső negyedben:
- Magas pontosság + sok ping anomália = valószínűleg hálózatot manipulál
- A két küszöbvonal meghúzza a "veszélyzónát"

---

### 3. KDR vs Pontosság (`3_kdr_vs_accuracy.png`)

**Mit mutat:** Ölés/halál arány (Y) vs. pontosság (X)

**Amit keresünk:**
- Normál játékosok: szétszórva, jól teljesítők is lehetnek jobb KDR-rel
- Cheateres játékosok: extrém magas KDR ÉS magas pontosság egyszerre (top-right cluster)

---

### 4. Lagswitch incidensek vs Ping szórás (`4_lagswitch_incidents.png`)

**Mit mutat:** Ping instabilitás (X, stddev) vs. lagswitch incidensek (Y)

**Amit keresünk:** Lagswitch gyanúsok: **magas stddev + több incidens**

---

### 5. Nerf eloszlás hisztogram (`5_nerf_distribution.png`)

**Mit mutat:** Hány játékos kap mekkora sebzéscsökkentést

```
Játékosok
száma
  25 |████
  20 |████ ████
  15 |████ ████ ████
  10 |████ ████ ████ ████
      0%   30%  60%  90%  Nerf %
     🟢    🟡   🟠   🔴
```

**Amit keresünk:** Egészséges szerveren a legtöbb játékos 0% (nem büntetve).
Ha a hisztogram jobbra tolódik: szerverednek sok a gyanús játékosa.

---

### 6. Korreláció hőtérkép (`6_correlation_heatmap.png`)

**Mit mutat:** Melyik metrikák járnak együtt (+1 = erős pozitív, -1 = inverz)

**Amit keresünk:**
- Magas **pontosság ↔ nerf%** korreláció: az algoritmus jól fogja a gyanúsakat
- Magas **ping anomália ↔ LS incidensek** korreláció: a lagswitch detekció konzisztens

---

## Excel munkafüzet (`_riport.xlsx`)

| Sheet | Tartalom |
|-------|---------|
| Összefoglalás | Szerver-szintű számok: játékosok/szintenként, átlag metrikák |
| Játékos részletek | Összesített per-játékos adatok, **színkódolt gyanú szint** |
| Fegyver részletek | Nyers per-fegyver sorok |
| Pontosság szigetek | PNG diagram beágyazva |
| Pontosság vs Ping | PNG diagram beágyazva |
| KDR vs Pontosság | PNG diagram beágyazva |
| Lagswitch | PNG diagram beágyazva |
| Nerf eloszlás | PNG diagram beágyazva |
| Korreláció | PNG diagram beágyazva |

---

## Tipikus workflow

```
1. Rust szerveren: /ac-export csv
   → Létrejön: oxide/data/MogyAntiCheat_Export_20260517_123456.csv

2. Másold ki a CSV-t a szerveredről

3. python analyze.py MogyAntiCheat_Export_20260517_123456.csv

4. Megnyitod a _riport.xlsx-et:
   - Összefoglalás sheet: gyors áttekintés
   - Pontosság szigetek sheet: látod a két "szigetet"
   - Piros pontok = gyanúsak, zöld pontok = normál játékosok

5. Döntés:
   - /ac-override <gyanúsJátékos> 50  → kézi nerf
   - /ac-ml-feedback <játékos> confirmed_cheater  → ML betanítás
```

---

## Szín kódolás

| Szín | Szint | Nerf % |
|------|-------|--------|
| 🟢 Zöld | Normál | 0% |
| 🟡 Sárga | Enyhe | 1–30% |
| 🟠 Narancs | Mérsékelt | 30–60% |
| 🔴 Piros | Súlyos | >60% |
