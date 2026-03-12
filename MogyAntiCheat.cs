using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("MogyAntiCheat", "Mogy", "1.5.0")]
    public class MogyAntiCheat : RustPlugin
    {
        private class WeaponData
        {
            public List<ShotResult> History = new List<ShotResult>();
            public void AddShot(bool hit, float distance, int limit)
            {
                History.Add(new ShotResult { IsHit = hit, Distance = distance });
                if (History.Count > limit) History.RemoveAt(0);
            }
            public float GetAccuracy() => History.Count == 0 ? 0 : (float)History.Count(x => x.IsHit) / History.Count;
            public float GetWeightedScore(float safeDist)
            {
                if (History.Count == 0) return 0;
                float totalScore = 0;
                int hits = 0;
                foreach (var shot in History)
                {
                    if (shot.IsHit)
                    {
                        float distFactor = shot.Distance > safeDist ? (shot.Distance / safeDist) : 1f;
                        totalScore += distFactor;
                        hits++;
                    }
                }
                return hits == 0 ? 0 : totalScore / hits;
            }
        }

        private struct ShotResult { public bool IsHit; public float Distance; }
        private Dictionary<ulong, Dictionary<string, WeaponData>> _playerStats = new Dictionary<ulong, Dictionary<string, WeaponData>>();

        protected override void LoadDefaultConfig()
        {
            Config["Weapons"] = new Dictionary<string, object>
            {
                // GÉPKARABÉLYOK (Magas tűzerő, közepes táv, szigorúbb kontroll)
                ["rifle.ak"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.38, ["SampleCount"] = 40, ["SafeDistance"] = 25.0 },
                ["rifle.lr300"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.40, ["SampleCount"] = 40, ["SafeDistance"] = 25.0 },
                ["rifle.semiauto"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.45, ["SampleCount"] = 30, ["SafeDistance"] = 30.0 },
                ["rifle.m39"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.50, ["SampleCount"] = 25, ["SafeDistance"] = 40.0 },

                // SMG-K ÉS AUTOMATA PISZTOLYOK (Rövid táv, nagy szórás)
                ["smg.2"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.35, ["SampleCount"] = 40, ["SafeDistance"] = 15.0 }, // Custom SMG
                ["smg.thompson"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.35, ["SampleCount"] = 40, ["SafeDistance"] = 18.0 },
                ["smg.mp5"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.35, ["SampleCount"] = 45, ["SafeDistance"] = 20.0 },
                ["ak47u"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.35, ["SampleCount"] = 40, ["SafeDistance"] = 15.0 }, // Prototype 17

                // PISZTOLYOK
                ["pistol.semiauto"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.40, ["SampleCount"] = 20, ["SafeDistance"] = 15.0 },
                ["pistol.m92"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.42, ["SampleCount"] = 25, ["SafeDistance"] = 15.0 },
                ["pistol.revolver"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.38, ["SampleCount"] = 15, ["SafeDistance"] = 12.0 },
                ["pistol.python"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.45, ["SampleCount"] = 15, ["SafeDistance"] = 20.0 },

                // SNIPER / TÁVOLSÁGI (Kevesebb lövés is elég a büntetéshez)
                ["rifle.bolt"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.65, ["SampleCount"] = 12, ["SafeDistance"] = 50.0 },
                ["rifle.l96"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.70, ["SampleCount"] = 10, ["SafeDistance"] = 70.0 },
                ["rifle.m249"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.30, ["SampleCount"] = 60, ["SafeDistance"] = 30.0 },
                ["hmlmg"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.30, ["SampleCount"] = 50, ["SafeDistance"] = 25.0 },

                // PRIMITÍV
                ["bow.hunting"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.50, ["SampleCount"] = 15, ["SafeDistance"] = 20.0 },
                ["bow.compound"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.60, ["SampleCount"] = 10, ["SafeDistance"] = 30.0 },
                ["crossbow"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.55, ["SampleCount"] = 10, ["SafeDistance"] = 25.0 },

                // SÖRÉTESEK (Itt a pontosság csalóka lehet, magasabbra vettem)
                ["shotgun.pump"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.70, ["SampleCount"] = 15, ["SafeDistance"] = 10.0 },
                ["shotgun.spas12"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.70, ["SampleCount"] = 20, ["SafeDistance"] = 10.0 }
            };
            SaveConfig();
        }

        void OnWeaponFired(BaseProjectile weapon, BasePlayer player)
        {
            if (player == null || weapon == null) return;
            string weaponName = weapon.ShortPrefabName.Replace(".entity", "");
            RecordShot(player.userID, weaponName, false, 0f);
        }

        void OnEntityTakeDamage(BaseEntity entity, HitInfo info)
        {
            if (info == null || info.InitiatorPlayer == null || entity == null) return;
            if (entity is BuildingBlock) return;

            BasePlayer attacker = info.InitiatorPlayer;

            // Fegyver beazonosítása
            var weapon = attacker.GetActiveItem()?.GetHeldEntity() as BaseProjectile;
            string weaponName = weapon != null ? weapon.ShortPrefabName.Replace(".entity", "") : "unknown";

            float dist = Vector3.Distance(info.HitPositionWorld, info.PointStart);

            // 1. Statisztika frissítése
            UpdateLastShotToHit(attacker.userID, weaponName, dist);

            // 2. Globális büntetés lekérése
            float globalNerf = GetLowestNerf(attacker.userID);

            // --- ADMIN VÉDELEM KIKOMMENTELVE TESZTHEZ ---
            // if (attacker.IsAdmin) return; 

            // 3. Sebzés módosítása (Ha a nerf < 1.0, akkor érvényesítjük)
            if (globalNerf < 1.0f)
            {
                info.damageTypes.ScaleAll(globalNerf);

                // Csak hogy lásd a konzolban is, hogy dolgozik:
                if (globalNerf == 0f)
                {
                    // Opcionális: küldhetünk egy üzenetet az adminnak, hogy most épp 0-t sebez
                    // SendReply(attacker, "DEBUG: 0% damage applied!");
                }
            }
        }

        private void UpdateLastShotToHit(ulong userId, string weapon, float dist)
        {
            if (!_playerStats.ContainsKey(userId) || !_playerStats[userId].ContainsKey(weapon)) return;
            var history = _playerStats[userId][weapon].History;
            for (int i = history.Count - 1; i >= 0; i--)
            {
                if (!history[i].IsHit)
                {
                    history[i] = new ShotResult { IsHit = true, Distance = dist };
                    break;
                }
            }
        }

        private void RecordShot(ulong userId, string weapon, bool isHit, float dist)
        {
            if (!_playerStats.ContainsKey(userId)) _playerStats[userId] = new Dictionary<string, WeaponData>();
            if (!_playerStats[userId].ContainsKey(weapon)) _playerStats[userId][weapon] = new WeaponData();

            int limit = 30;
            var weaponsCfg = Config["Weapons"] as Dictionary<string, object>;
            if (weaponsCfg != null && weaponsCfg.ContainsKey(weapon))
                limit = System.Convert.ToInt32(((Dictionary<string, object>)weaponsCfg[weapon])["SampleCount"]);

            _playerStats[userId][weapon].AddShot(isHit, dist, limit);
        }

        // Kiszámolja a legdurvább büntetést az összes fegyver közül
        private float GetLowestNerf(ulong userId)
        {
            if (!_playerStats.ContainsKey(userId)) return 1.0f;
            float lowestNerf = 1.0f;
            var weaponsCfg = Config["Weapons"] as Dictionary<string, object>;

            foreach (var weaponEntry in _playerStats[userId])
            {
                if (weaponsCfg == null || !weaponsCfg.ContainsKey(weaponEntry.Key)) continue;
                var cfg = weaponsCfg[weaponEntry.Key] as Dictionary<string, object>;

                float max = System.Convert.ToSingle(cfg["MaxAccuracy"]);
                float safe = System.Convert.ToSingle(cfg["SafeDistance"]);

                var data = weaponEntry.Value;

                // Csökkentettük a várakozást: már 10 lövés után büntethet
                if (data.History.Count < 10) continue;

                float acc = data.GetAccuracy();
                if (acc > max)
                {
                    float distW = data.GetWeightedScore(safe);

                    // Kiszámoljuk a túllépést (0.0 - 1.0 között)
                    float excess = (acc - max) / (1.0f - max);

                    // Távolsági büntetés: ha distW > 1, exponenciálisan növeljük a büntetést
                    // Ha messzire lő 100%-ot, a penaltyFactor pillanatok alatt 1.0 (vagy több) lesz
                    float penaltyFactor = excess * (distW > 1.0f ? Mathf.Pow(distW, 2f) : 1.0f);

                    float currentNerf = 1.0f - penaltyFactor;

                    // Ha 100%-os a pontosság és messze van, azonnali 0
                    if (acc > 0.95f && distW > 1.2f) currentNerf = 0f;

                    // 30% alatt már ne is sebezzen semmit
                    if (currentNerf < 0.30f) currentNerf = 0f;

                    if (currentNerf < lowestNerf) lowestNerf = currentNerf;
                }
            }
            return Mathf.Clamp(lowestNerf, 0f, 1.0f);
        }

        [ChatCommand("checkac")]
        void CmdChatCheck(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;
            BasePlayer target = (args.Length > 0) ? BasePlayer.Find(args[0]) : player;
            if (target == null) return;

            float globalDmg = GetLowestNerf(target.userID);
            string globalColor = globalDmg < 0.5f ? "#ff6666" : (globalDmg < 1.0f ? "#ffff55" : "#88ff88");

            string report = $"<color=#55ff55>=== MogyAC STATS: {target.displayName} ===</color>\n";
            report += $"<color=#ffffff>GLOBAL DAMAGE: </color><color={globalColor}>{globalDmg:P0}</color>\n";
            report += "----------------------------\n";

            if (_playerStats.ContainsKey(target.userID))
            {
                foreach (var w in _playerStats[target.userID])
                {
                    report += $"<color=#ffff55>{w.Key}:</color> {w.Value.GetAccuracy():P1} ({w.Value.History.Count} lövés)\n";
                }
            }
            else report += "Nincs adat.";

            SendReply(player, report);
        }
        // --- ÚJ PARANCS: LISTA MINDENKIRŐL ---
        [ChatCommand("aclist")]
        void CmdAcList(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;

            string report = "<color=#55ff55>=== MogyAC AKTÍV LISTA ===</color>\n";
            report += "<color=#aaaaaa>Játékos | Átlag Acc | Sebzés</color>\n";

            foreach (var target in BasePlayer.activePlayerList)
            {
                float globalDmg = GetLowestNerf(target.userID);
                float totalAcc = 0;
                int count = 0;

                if (_playerStats.ContainsKey(target.userID) && _playerStats[target.userID].Count > 0)
                {
                    foreach (var w in _playerStats[target.userID])
                    {
                        totalAcc += w.Value.GetAccuracy();
                        count++;
                    }
                }

                float avgAcc = count > 0 ? (totalAcc / count) : 0;
                string dmgColor = globalDmg < 1.0f ? "#ff6666" : "#88ff88";
                
                report += $"{target.displayName} | {avgAcc:P0} | <color={dmgColor}>{globalDmg:P0}</color>\n";
            }

            SendReply(player, report);
        }

        // --- ÚJ PARANCS: RESET ---
        [ChatCommand("acreset")]
        void CmdAcReset(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;

            if (args.Length == 0)
            {
                SendReply(player, "Használat: /acreset <játékosnév>");
                return;
            }

            BasePlayer target = BasePlayer.Find(args[0]);
            if (target == null)
            {
                SendReply(player, "Játékos nem található.");
                return;
            }

            if (_playerStats.ContainsKey(target.userID))
            {
                _playerStats.Remove(target.userID);
                SendReply(player, $"<color=#55ff55>[MogyAC] {target.displayName} statisztikái törölve. Sebzés újra 100%.</color>");
                Puts($"[MogyAC] Admin {player.displayName} resetelte {target.displayName} statisztikáit.");
            }
            else
            {
                SendReply(player, "Nincs tárolt adat a játékosról.");
            }
        }
    }
}