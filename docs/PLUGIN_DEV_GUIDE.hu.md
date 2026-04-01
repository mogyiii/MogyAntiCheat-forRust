# MogyAntiCheat Plugin Fejlesztői Gyorsútmutató (HU)

Ez a leírás abban segít, hogyan írj saját Oxide/uMod plugint, ami a `MogyAntiCheat` publikus eseményeire reagál.

Kiemelt példa:
- Ha egy játékos sebzése 0%-ra csökken (`appliedMultiplier == 0`), jelenjen meg szerver értesítés.

## 1) Előfeltételek

- Rust szerver Oxide/uMod környezettel.
- Betöltött `MogyAntiCheat` plugin.
- A `MogyAntiCheat` configban:
  - `PublicApi.Enabled = true`
  - `PublicApi.EmitPenaltyEvents = true`

Részletes API mezők: `docs/PUBLIC_API.md`

## 2) Hova kerüljön a plugin fájl

A működő szerveren a pluginok tipikus helye:
- `server/<identity>/oxide/plugins/`

Ebben a repository-ban példa fejlesztői mappa:
- `plugins/`

Példa fájl:
- `plugins/MogyAcZeroDamageAlert.cs`

## 3) Milyen hookot figyelj

Ehhez a funkcióhoz ezt használd:
- `OnMogyAcPenaltyApplied(Dictionary<string, object> payload)`

Ez akkor fut le, amikor a MogyAntiCheat ténylegesen skálázza a kimenő sebzést.

Hasznos mezők a payloadban:
- `attackerId` (`ulong`)
- `weaponShortName` (`string`)
- `appliedMultiplier` (`float`)
- `originalDamage` (`float`)
- `scaledDamage` (`float`)
- `timestampUtc` (`string`)

## 4) Minimál plugin sablon

```csharp
using Oxide.Plugins;
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("MyAcSubscriber", "TeNeved", "1.0.0")]
    [Description("MogyAntiCheat penalty event subscriber.")]
    public class MyAcSubscriber : RustPlugin
    {
        private void OnMogyAcPenaltyApplied(Dictionary<string, object> payload)
        {
            if (payload == null) return;
            // Itt kezeld a mezőket, és végezd az egyedi logikát.
        }
    }
}
```

## 5) 0%-os sebzés figyelés logika

Ajánlott szabály:
- Akkor tekintsd 0%-nak, ha `appliedMultiplier <= 0.0001` vagy `scaledDamage <= 0.0001`.

Miért:
- Lebegőpontos számoknál a pontos `== 0` összehasonlítás bizonytalan lehet.

Ajánlott védelem:
- Játékosonként cooldown (pl. 60 mp), hogy ne spamelje a chatet.

## 6) Szerver üzenet küldés

Globális chat üzenet:
- `PrintToChat("...")`

Célszerű beleírni:
- játékos neve vagy SteamID
- fegyver neve
- rövid jelzés, hogy csalásgyanú van

## 7) Tesztelés lépései

1. Töltsd be / reloadold a `MogyAntiCheat` és a saját pluginod.
2. Ellenőrizd, hogy a `PublicApi` és `EmitPenaltyEvents` be vannak kapcsolva.
3. Válts debug módra, ha gyorsabban akarsz eseményeket látni (`/ac-debug on`).
4. Generálj olyan szituációt, ahol az AC 0%-ra viszi a sebzést.
5. Ellenőrizd, hogy az értesítés egyszer jelenik meg cooldown időn belül.

## 8) Bővítési ötletek

- Discord webhook küldés külön pluginból.
- Admin-only értesítés (globális chat helyett privát).
- Automatikus logfájl írás bizonyíték mezőkkel.
- Többnyelvű üzenetek (`lang.RegisterMessages`).

## 9) Kompatibilitási tanácsok

- Mindig számolj azzal, hogy payload kulcs hiányozhat.
- Használj `try/catch`-et `Convert.ToSingle` / `Convert.ToUInt64` körül.
- Új API minor verzióban új mezők jöhetnek, de a régiekre építs.

---

Kapcsolódó fájlok:
- `docs/PUBLIC_API.md`
- `docs/examples/MogyAcExampleSubscriber.cs`
- `plugins/MogyAcZeroDamageAlert.cs`

