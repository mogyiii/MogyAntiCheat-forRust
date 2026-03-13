# MogyAntiCheat for Rust (Oxide)

![License](https://img.shields.io/badge/license-MIT-green)
![Version](https://img.shields.io/badge/version-1.6.7-blue)
![Game](https://img.shields.io/badge/game-Rust-orange)

Egy intelligens, statisztikai alapú anti-cheat rendszer Rust (Oxide/Umod) szerverekhez. A hagyományos tiltásokkal (ban) ellentétben ez a plugin dinamikusan korlátozza a gyanús játékosok sebzését, így minimalizálva a false-positive (téves) felismerésekből adódó károkat.

## 🚀 Működési elv

A **MogyAntiCheat** nem fájlokat vagy folyamatokat ellenőriz, hanem a játékos harci teljesítményét figyeli:

1.  **Lövéskövetés:** Minden lövést regisztrál és egy ideiglenes várólistára tesz.
2.  **Találat-validálás:** Csak azokat a találatokat számolja, amelyek valódi játékosok ellen történnek (NPC-ket és épületeket figyelmen kívül hagy).
3.  **Dinamikus pontosság:** Kiszámolja a fegyver-specifikus pontosságot (Accuracy %).
4.  **Távolság-súlyozás:** A távoli találatok nagyobb súllyal esnek latba, mint a közeli lövések.
5.  **Sebzéskorlátozás (Nerf):** Ha egy játékos átlépi a fegyverhez tartozó küszöbértéket, a plugin automatikusan és fokozatosan csökkenti a sebzését, akár 0-ig.

## ✨ Főbb jellemzők

* **Időalapú feldolgozás:** Megkülönbözteti a gyors sorozatokat a lassú, pontos lövésektől.
* **Adatperzisztencia:** A statisztikák szerverújraindítás után is megmaradnak (`oxide/data` fájlban).
* **Admin mentesség:** Az adminisztrátorok lövéseit nem korlátozza.
* **NPC szűrés:** A Scientistek és állatok nem rontják el a statisztikát.
* **Reszponzív lekérdezés:** Azonnali in-game riportok az adminok számára.

## 🛠 Konfiguráció

A `MogyAntiCheat.json` fájlban minden fegyverre külön paraméterek adhatóak meg:

| Paraméter | Leírás |
| :--- | :--- |
| `MaxAccuracy` | A maximálisan megengedett találati arány (pl. 0.38 = 38%). |
| `SampleCount` | Hány lövést vegyen alapul a statisztikához (Golyó-memória). |
| `SafeDistance` | Az a távolság, ami felett a találatok "gyanúsnak" számítanak. |

## 💻 Parancsok

* `/ac-check [játékosnév]` - Részletes statisztika megtekintése egy játékosról.
* `/ac-list` - Az összes online játékos listázása a jelenlegi sebzés-szorzójukkal.
* `/ac-reset [játékosnév]` - Egy játékos statisztikájának törlése.

## 📥 Telepítés

1.  Telepítsd az [Oxide](https://umod.org/games/rust) rendszert a szerveredre.
2.  Másold a `MogyAntiCheat.cs` fájlt a `server/your_identity/oxide/plugins` mappába.
3.  A konfiguráció automatikusan létrejön az első induláskor.

## 📄 Licenc

Ez a projekt az MIT licenc alatt áll - szabadon módosítható és terjeszthető.

---
Created by **Mogy**