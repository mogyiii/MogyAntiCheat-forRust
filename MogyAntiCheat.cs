using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using UnityEngine;
using Oxide.Core.Configuration;

namespace Oxide.Plugins
{
    [Info("MogyAntiCheat", "Mogy", "1.6.8")]
    public class MogyAntiCheat : RustPlugin
    {
        private DynamicConfigFile _storedData;
        private Dictionary<ulong, Dictionary<string, List<ShotResult>>> _savedStats = new Dictionary<ulong, Dictionary<string, List<ShotResult>>>();
        private class WeaponData
        {
            public List<ShotResult> History = new List<ShotResult>();
            public List<KeyValuePair<float, float>> PendingMisses = new List<KeyValuePair<float, float>>();

            public void AddMiss(float distance)
            {
                PendingMisses.Add(new KeyValuePair<float, float>(Time.realtimeSinceStartup, distance));
                if (PendingMisses.Count > 100) PendingMisses.RemoveAt(0);
            }

            public void RegisterHit(float distance, int limit, float expiryTime)
            {
                float now = Time.realtimeSinceStartup;

                // 1. Keressük meg a legfrissebb lövést a várólistán
                // (Hátulról nézzük, mert a legutolsó lövésünk a legvalószínűbb találat)
                int lastIndex = -1;
                for (int i = PendingMisses.Count - 1; i >= 0; i--)
                {
                    if (now - PendingMisses[i].Key <= expiryTime)
                    {
                        lastIndex = i;
                        break;
                    }
                }

                // 2. Ha találtunk érvényes lövést a várólistán:
                if (lastIndex != -1)
                {
                    // Minden lövést, ami EZELŐTT történt és nem járt le, mellélövésnek könyvelünk el
                    for (int i = 0; i < lastIndex; i++)
                    {
                        if (now - PendingMisses[i].Key <= expiryTime)
                        {
                            History.Add(new ShotResult { IsHit = false, Distance = PendingMisses[i].Value });
                        }
                    }

                    // Magát a találatot (a lastIndex-nél lévőt) hozzáadjuk találatként
                    History.Add(new ShotResult { IsHit = true, Distance = distance });

                    // Töröljük a listát a feldolgozott lövésekig (a találattal együtt)
                    PendingMisses.RemoveRange(0, lastIndex + 1);
                }
                else
                {
                    // Ha valamiért nem volt lövés a listán (ritka lag), 
                    // akkor csak simán adjuk hozzá a találatot, hogy ne maradjunk le semmiről
                    History.Add(new ShotResult { IsHit = true, Distance = distance });
                }

                // Tárhely korlátozás
                while (History.Count > limit) History.RemoveAt(0);
            }

            public float GetAccuracy() => History.Count == 0 ? 0 : (float)History.Count(x => x.IsHit) / History.Count;

            public float GetWeightedScore(float safeDist)
            {
                var hits = History.Where(x => x.IsHit).ToList();
                if (hits.Count == 0) return 0;
                float totalScore = hits.Sum(shot => shot.Distance > safeDist ? (shot.Distance / safeDist) : 1f);
                return totalScore / hits.Count;
            }
        }

        private struct ShotResult { public bool IsHit; public float Distance; }
        private Dictionary<ulong, Dictionary<string, WeaponData>> _playerStats = new Dictionary<ulong, Dictionary<string, WeaponData>>();
        private Dictionary<ulong, float> _lastHitTime = new Dictionary<ulong, float>();

        void Init()
        {
            _storedData = Interface.Oxide.DataFileSystem.GetFile("MogyAntiCheat_Stats");
            LoadStats();
        }

        void OnServerSave() => SaveStats(); // Minden automatikus mentésnél mentsen a plugin is
        void Unload() => SaveStats();       // Plugin leálláskor (restart/reload) mentsen

        private void SaveStats()
        {
            var dataToSave = new Dictionary<string, Dictionary<string, List<ShotResult>>>();
            foreach (var player in _playerStats)
            {
                var weaponDict = new Dictionary<string, List<ShotResult>>();
                foreach (var weapon in player.Value)
                {
                    weaponDict[weapon.Key] = weapon.Value.History;
                }
                dataToSave[player.Key.ToString()] = weaponDict;
            }
            _storedData.WriteObject(dataToSave);
        }

        private void LoadStats()
        {
            var data = _storedData.ReadObject<Dictionary<string, Dictionary<string, List<ShotResult>>>>();
            if (data == null) return;

            foreach (var playerEntry in data)
            {
                ulong userId = ulong.Parse(playerEntry.Key);
                _playerStats[userId] = new Dictionary<string, WeaponData>();

                foreach (var weaponEntry in playerEntry.Value)
                {
                    _playerStats[userId][weaponEntry.Key] = new WeaponData { History = weaponEntry.Value };
                }
            }
        }
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
            Config["MissExpirySeconds"] = 20.0;
            SaveConfig();
        }

        void OnWeaponFired(BaseProjectile weapon, BasePlayer player)
        {
            if (player == null || weapon == null || player.IsNpc) return; // NPC ne lőjön bele a statba
            string wName = weapon.ShortPrefabName.Replace(".entity", "");

            if (!_playerStats.ContainsKey(player.userID)) _playerStats[player.userID] = new Dictionary<string, WeaponData>();
            if (!_playerStats[player.userID].ContainsKey(wName)) _playerStats[player.userID][wName] = new WeaponData();

            _playerStats[player.userID][wName].AddMiss(0f);
        }

        void OnEntityTakeDamage(BaseEntity entity, HitInfo info)
        {
            if (info == null || info.InitiatorPlayer == null || entity == null) return;

            // --- PONTOS NPC ÉS ÉPÜLET SZŰRÉS ---
            if (entity is BuildingBlock) return;

            BasePlayer targetPlayer = entity as BasePlayer;

            // Ha nem játékost találtunk el, vagy az illető NPC/Bot:
            if (targetPlayer == null || targetPlayer.IsNpc || !targetPlayer.userID.IsSteamId()) return;
            // ----------------------------------

            BasePlayer attacker = info.InitiatorPlayer;
            if (attacker.IsNpc || !attacker.userID.IsSteamId()) return; // Az NPC-k lövéseit se mérjük

            // Idő alapú védelem marad
            float lastHit;
            if (_lastHitTime.TryGetValue(attacker.userID, out lastHit))
            {
                if (Time.realtimeSinceStartup - lastHit < 0.05f) return;
            }
            _lastHitTime[attacker.userID] = Time.realtimeSinceStartup;

            var weapon = attacker.GetActiveItem()?.GetHeldEntity() as BaseProjectile;
            if (weapon == null) return;

            string wName = weapon.ShortPrefabName.Replace(".entity", "");
            float dist = Vector3.Distance(info.HitPositionWorld, info.PointStart);
            float expiry = Config["MissExpirySeconds"] != null ? System.Convert.ToSingle(Config["MissExpirySeconds"]) : 20f;

            int limit = 40;
            var weaponsCfg = Config["Weapons"] as Dictionary<string, object>;
            if (weaponsCfg != null && weaponsCfg.ContainsKey(wName))
            {
                var entry = weaponsCfg[wName] as Dictionary<string, object>;
                limit = System.Convert.ToInt32(entry["SampleCount"]);
            }

            if (!_playerStats.ContainsKey(attacker.userID)) _playerStats[attacker.userID] = new Dictionary<string, WeaponData>();
            if (!_playerStats[attacker.userID].ContainsKey(wName)) _playerStats[attacker.userID][wName] = new WeaponData();

            // Regisztráljuk a találatot
            _playerStats[attacker.userID][wName].RegisterHit(dist, limit, expiry);

            // Sebzés csökkentés - Csak akkor, ha az áldozat is játékos
            float globalNerf = GetLowestNerf(attacker.userID);
            if (!attacker.IsAdmin && globalNerf < 1.0f)
            {
                info.damageTypes.ScaleAll(globalNerf);
            }
        }

        private float GetLowestNerf(ulong userId)
        {
            if (!_playerStats.ContainsKey(userId)) return 1.0f;
            float lowestNerf = 1.0f;
            var weaponsCfg = Config["Weapons"] as Dictionary<string, object>;

            foreach (var weaponEntry in _playerStats[userId])
            {
                if (weaponsCfg == null || !weaponsCfg.ContainsKey(weaponEntry.Key)) continue;
                var cfg = weaponsCfg[weaponEntry.Key] as Dictionary<string, object>;
                var data = weaponEntry.Value;

                if (data.History.Count < 10) continue;

                float acc = data.GetAccuracy();
                float max = System.Convert.ToSingle(cfg["MaxAccuracy"]);
                float safe = System.Convert.ToSingle(cfg["SafeDistance"]);

                if (acc > max)
                {
                    float distW = data.GetWeightedScore(safe);
                    float excess = (acc - max) / (1.0f - max);
                    float penaltyFactor = excess * (distW > 1.0f ? Mathf.Pow(distW, 2f) : 1.0f);

                    float currentNerf = 1.0f - penaltyFactor;
                    if (acc > 0.95f && distW > 1.2f) currentNerf = 0f;
                    if (currentNerf < 0.30f) currentNerf = 0f;
                    if (currentNerf < lowestNerf) lowestNerf = currentNerf;
                }
            }
            return Mathf.Clamp(lowestNerf, 0f, 1.0f);
        }

        [ChatCommand("ac-check")]
        void CmdChatCheck(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;
            BasePlayer target = (args.Length > 0) ? BasePlayer.Find(args[0]) : player;
            if (target == null) { SendReply(player, "Játékos nem található."); return; }

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

        [ChatCommand("ac-list")]
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

        [ChatCommand("ac-reset")]
        void CmdAcReset(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin || args.Length == 0) return;
            BasePlayer target = BasePlayer.Find(args[0]);
            if (target != null && _playerStats.Remove(target.userID))
                SendReply(player, $"<color=#55ff55>[MogyAC] {target.displayName} statisztikái törölve.</color>");
        }
    }
}