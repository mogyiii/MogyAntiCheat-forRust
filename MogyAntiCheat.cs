using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("MogyAntiCheat", "Mogy", "1.2.6")]
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
                foreach (var shot in History)
                {
                    if (shot.IsHit)
                    {
                        float distFactor = shot.Distance > safeDist ? (shot.Distance / safeDist) : 1f;
                        totalScore += distFactor;
                    }
                }
                return totalScore / History.Count;
            }
        }

        private struct ShotResult { public bool IsHit; public float Distance; }
        private Dictionary<ulong, Dictionary<string, WeaponData>> _playerStats = new Dictionary<ulong, Dictionary<string, WeaponData>>();

        protected override void LoadDefaultConfig()
        {
            Config["Weapons"] = new Dictionary<string, object>
            {
                ["rifle.ak"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.40, ["SampleCount"] = 60, ["SafeDistance"] = 30.0 },
                ["ak47u"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.40, ["SampleCount"] = 50, ["SafeDistance"] = 25.0 },
                ["smg.2"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.40, ["SampleCount"] = 50, ["SafeDistance"] = 25.0 },
                ["rifle.bolt"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.60, ["SampleCount"] = 20, ["SafeDistance"] = 50.0 },
                ["bow.hunting"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.45, ["SampleCount"] = 15, ["SafeDistance"] = 20.0 }
            };
            SaveConfig();
        }

        void OnWeaponFired(BaseProjectile weapon, BasePlayer player)
        {
            if (player == null || weapon == null) return;
            string weaponName = weapon.ShortPrefabName.Replace(".entity", "");

            // Itt rögzítjük a lövést (alapból Miss)
            RecordShot(player.userID, weaponName, false, 0f);
        }

        // 2. TALÁLAT REGISZTRÁLÁSA
        void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (info == null || info.InitiatorPlayer == null || entity == null) return;
            if (entity is BuildingBlock) return;

            BasePlayer attacker = info.InitiatorPlayer;

            // A fegyver nevét ugyanúgy kell kinyernünk, mint lövésnél!
            var weapon = attacker.GetActiveItem()?.GetHeldEntity() as BaseProjectile;
            if (weapon == null) return;

            string weaponName = weapon.ShortPrefabName.Replace(".entity", "");
            float dist = Vector3.Distance(info.HitPositionWorld, info.PointStart);

            // Javítottunk: pontosan ugyanazt a weaponName-t küldjük be
            UpdateLastShotToHit(attacker.userID, weaponName, dist);

            ApplyNerf(attacker, info, weaponName);
        }

        private void UpdateLastShotToHit(ulong userId, string weapon, float dist)
        {
            if (!_playerStats.ContainsKey(userId) || !_playerStats[userId].ContainsKey(weapon)) return;

            var data = _playerStats[userId][weapon];
            if (data.History.Count == 0) return;

            // Hátulról előre megkeressük az utolsó lövést, ami még nem talált
            for (int i = data.History.Count - 1; i >= 0; i--)
            {
                if (!data.History[i].IsHit)
                {
                    // Beállítjuk találatnak
                    data.History[i] = new ShotResult { IsHit = true, Distance = dist };
                    return; // Megtaláltuk, megállunk
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
            {
                var weaponEntry = weaponsCfg[weapon] as Dictionary<string, object>;
                limit = System.Convert.ToInt32(weaponEntry["SampleCount"]);
            }

            _playerStats[userId][weapon].AddShot(isHit, dist, limit);
        }

        private void ApplyNerf(BasePlayer player, HitInfo info, string weapon)
        {
            if (player.IsAdmin || !_playerStats.ContainsKey(player.userID) || !_playerStats[player.userID].ContainsKey(weapon)) return;

            var weaponsCfg = Config["Weapons"] as Dictionary<string, object>;
            if (weaponsCfg == null || !weaponsCfg.ContainsKey(weapon)) return;

            var weaponEntry = weaponsCfg[weapon] as Dictionary<string, object>;
            var data = _playerStats[player.userID][weapon];

            float maxAcc = System.Convert.ToSingle(weaponEntry["MaxAccuracy"]);
            float safeDist = System.Convert.ToSingle(weaponEntry["SafeDistance"]);
            int sampleCount = System.Convert.ToInt32(weaponEntry["SampleCount"]);

            float currentAcc = data.GetAccuracy();
            float weightedScore = data.GetWeightedScore(safeDist);

            if (data.History.Count >= (sampleCount / 2) && currentAcc > maxAcc)
            {
                float nerf = maxAcc / (currentAcc * (weightedScore > 0 ? weightedScore : 1f));
                nerf = Mathf.Clamp(nerf, 0.05f, 1.0f);
                info.damageTypes.ScaleAll(nerf);

                if (nerf < 0.8f)
                    Puts($"[MogyAC][NERF] {player.displayName} | Wep: {weapon} | Acc: {currentAcc:P0} | Damage: {nerf:P0}");
            }
        }

        [ChatCommand("checkac")]
        void CmdChatCheck(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin) return;
            BasePlayer target = (args.Length > 0) ? BasePlayer.Find(args[0]) : player;
            if (target == null) { SendReply(player, "Játékos nem található."); return; }

            string report = $"<color=#55ff55>=== MogyAC STATS: {target.displayName} ===</color>\n";
            if (_playerStats.ContainsKey(target.userID))
            {
                var weaponsCfg = Config["Weapons"] as Dictionary<string, object>;
                foreach (var w in _playerStats[target.userID])
                {
                    float acc = w.Value.GetAccuracy();
                    string nerfStatus = "<color=#88ff88>100%</color>";

                    if (weaponsCfg != null && weaponsCfg.ContainsKey(w.Key))
                    {
                        var entry = weaponsCfg[w.Key] as Dictionary<string, object>;
                        float max = System.Convert.ToSingle(entry["MaxAccuracy"]);
                        float safe = System.Convert.ToSingle(entry["SafeDistance"]);
                        if (w.Value.History.Count >= 10 && acc > max)
                        {
                            float nerfVal = Mathf.Clamp(max / (acc * w.Value.GetWeightedScore(safe)), 0.05f, 1.0f);
                            if (nerfVal < 1.0f) nerfStatus = $"<color=#ff6666>{nerfVal:P0}</color>";
                        }
                    }
                    report += $"<color=#ffff55>{w.Key}:</color> {acc:P1} | DMG: {nerfStatus}\n";
                }
            }
            else report += "Nincs adat.";
            SendReply(player, report);
        }
    }
}