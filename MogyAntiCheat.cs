using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("MogyAntiCheat", "Mogy", "1.7.0")]
    public class MogyAntiCheat : RustPlugin
    {
        private const string DefaultLanguageFallback = "en";
        private const string PublicApiVersionCurrent = "1.0.0";

        private DynamicConfigFile _storedData;
        private readonly Dictionary<ulong, Dictionary<string, WeaponData>> _playerStats = new Dictionary<ulong, Dictionary<string, WeaponData>>();
        private readonly Dictionary<ulong, float> _lastHitTime = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, HashSet<string>> _activeSuspicionByWeapon = new Dictionary<ulong, HashSet<string>>();

        private static readonly Dictionary<string, string> MessagesEn = new Dictionary<string, string>
        {
            ["NoPermission"] = "You do not have permission to use this command.",
            ["PlayerNotFound"] = "Player not found.",
            ["NoData"] = "No data.",
            ["StatsHeader"] = "=== MogyAC STATS: {0} ===",
            ["GlobalDamageLabel"] = "GLOBAL DAMAGE",
            ["WeaponLine"] = "{0}: {1:P1} ({2} shots)",
            ["ActiveListHeader"] = "=== MogyAC ACTIVE LIST ===",
            ["ActiveListColumns"] = "Player | Avg Acc | Damage",
            ["StatsResetSuccess"] = "[MogyAC] {0} stats reset.",
            ["ResetUsage"] = "Usage: /ac-reset <playerName>",
            ["LangUsage"] = "Usage: /ac-lang <languageCode>",
            ["LangUpdated"] = "Default language set to: {0}.",
            ["LangAlreadySet"] = "Default language is already: {0}.",
            ["LangUnsupported"] = "Unsupported language: {0}. Supported: {1}"
        };

        private static readonly Dictionary<string, string> MessagesHu = new Dictionary<string, string>
        {
            ["NoPermission"] = "Nincs jogosultsagod ehhez a parancshoz.",
            ["PlayerNotFound"] = "Jatekos nem talalhato.",
            ["NoData"] = "Nincs adat.",
            ["StatsHeader"] = "=== MogyAC STAT: {0} ===",
            ["GlobalDamageLabel"] = "GLOBAL SEBZES",
            ["WeaponLine"] = "{0}: {1:P1} ({2} loves)",
            ["ActiveListHeader"] = "=== MogyAC AKTIV LISTA ===",
            ["ActiveListColumns"] = "Jatekos | Atlag Acc | Sebzes",
            ["StatsResetSuccess"] = "[MogyAC] {0} statisztikai torolve.",
            ["ResetUsage"] = "Hasznalat: /ac-reset <jatekosnev>",
            ["LangUsage"] = "Hasznalat: /ac-lang <nyelvkod>",
            ["LangUpdated"] = "Alapertelmezett nyelv beallitva: {0}.",
            ["LangAlreadySet"] = "Az alapertelmezett nyelv mar ez: {0}.",
            ["LangUnsupported"] = "Nem tamogatott nyelv: {0}. Tamogatott: {1}"
        };

        private struct ShotResult
        {
            public bool IsHit;
            public float Distance;
        }

        private class WeaponData
        {
            public readonly List<ShotResult> History = new List<ShotResult>();
            public readonly List<KeyValuePair<float, float>> PendingMisses = new List<KeyValuePair<float, float>>();

            public void AddMiss(float distance)
            {
                PendingMisses.Add(new KeyValuePair<float, float>(Time.realtimeSinceStartup, distance));
                if (PendingMisses.Count > 100) PendingMisses.RemoveAt(0);
            }

            public void RegisterHit(float distance, int limit, float expiryTime)
            {
                float now = Time.realtimeSinceStartup;
                int lastIndex = -1;

                for (int i = PendingMisses.Count - 1; i >= 0; i--)
                {
                    if (now - PendingMisses[i].Key <= expiryTime)
                    {
                        lastIndex = i;
                        break;
                    }
                }

                if (lastIndex != -1)
                {
                    for (int i = 0; i < lastIndex; i++)
                    {
                        if (now - PendingMisses[i].Key <= expiryTime)
                        {
                            History.Add(new ShotResult { IsHit = false, Distance = PendingMisses[i].Value });
                        }
                    }

                    History.Add(new ShotResult { IsHit = true, Distance = distance });
                    PendingMisses.RemoveRange(0, lastIndex + 1);
                }
                else
                {
                    History.Add(new ShotResult { IsHit = true, Distance = distance });
                }

                while (History.Count > limit) History.RemoveAt(0);
            }

            public float GetAccuracy()
            {
                return History.Count == 0 ? 0f : (float)History.Count(x => x.IsHit) / History.Count;
            }

            public float GetWeightedScore(float safeDist)
            {
                var hits = History.Where(x => x.IsHit).ToList();
                if (hits.Count == 0) return 0f;

                float totalScore = hits.Sum(shot => shot.Distance > safeDist ? (shot.Distance / safeDist) : 1f);
                return totalScore / hits.Count;
            }
        }

        private class WeaponEvaluation
        {
            public float Accuracy;
            public float MaxAccuracy;
            public float SafeDistance;
            public float WeightedScore;
            public float SuggestedNerf;
            public bool HasEnoughData;
            public bool IsSuspicious;
            public int SampleCount;
        }

        void Init()
        {
            lang.RegisterMessages(MessagesEn, this, "en");
            lang.RegisterMessages(MessagesHu, this, "hu");

            _storedData = Interface.Oxide.DataFileSystem.GetFile("MogyAntiCheat_Stats");
            LoadStats();
            EnsureConfigDefaults();
        }

        void OnServerSave() => SaveStats();
        void Unload() => SaveStats();

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
                ulong userId;
                if (!ulong.TryParse(playerEntry.Key, out userId)) continue;

                _playerStats[userId] = new Dictionary<string, WeaponData>();
                foreach (var weaponEntry in playerEntry.Value)
                {
                    var wd = new WeaponData();
                    if (weaponEntry.Value != null) wd.History.AddRange(weaponEntry.Value);
                    _playerStats[userId][weaponEntry.Key] = wd;
                }
            }
        }

        protected override void LoadDefaultConfig()
        {
            Config["Weapons"] = new Dictionary<string, object>
            {
                ["rifle.ak"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.38, ["SampleCount"] = 40, ["SafeDistance"] = 25.0 },
                ["rifle.lr300"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.40, ["SampleCount"] = 40, ["SafeDistance"] = 25.0 },
                ["rifle.semiauto"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.45, ["SampleCount"] = 30, ["SafeDistance"] = 30.0 },
                ["rifle.m39"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.50, ["SampleCount"] = 25, ["SafeDistance"] = 40.0 },

                ["smg.2"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.35, ["SampleCount"] = 40, ["SafeDistance"] = 15.0 },
                ["smg.thompson"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.35, ["SampleCount"] = 40, ["SafeDistance"] = 18.0 },
                ["smg.mp5"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.35, ["SampleCount"] = 45, ["SafeDistance"] = 20.0 },
                ["ak47u"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.35, ["SampleCount"] = 40, ["SafeDistance"] = 15.0 },

                ["pistol.semiauto"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.40, ["SampleCount"] = 20, ["SafeDistance"] = 15.0 },
                ["pistol.m92"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.42, ["SampleCount"] = 25, ["SafeDistance"] = 15.0 },
                ["pistol.revolver"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.38, ["SampleCount"] = 15, ["SafeDistance"] = 12.0 },
                ["pistol.python"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.45, ["SampleCount"] = 15, ["SafeDistance"] = 20.0 },

                ["rifle.bolt"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.65, ["SampleCount"] = 12, ["SafeDistance"] = 50.0 },
                ["rifle.l96"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.70, ["SampleCount"] = 10, ["SafeDistance"] = 70.0 },
                ["rifle.m249"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.30, ["SampleCount"] = 60, ["SafeDistance"] = 30.0 },
                ["hmlmg"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.30, ["SampleCount"] = 50, ["SafeDistance"] = 25.0 },

                ["bow.hunting"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.50, ["SampleCount"] = 15, ["SafeDistance"] = 20.0 },
                ["bow.compound"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.60, ["SampleCount"] = 10, ["SafeDistance"] = 30.0 },
                ["crossbow"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.55, ["SampleCount"] = 10, ["SafeDistance"] = 25.0 },

                ["shotgun.pump"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.70, ["SampleCount"] = 15, ["SafeDistance"] = 10.0 },
                ["shotgun.spas12"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.70, ["SampleCount"] = 20, ["SafeDistance"] = 10.0 }
            };

            Config["MissExpirySeconds"] = 20.0;
            Config["DefaultLanguage"] = DefaultLanguageFallback;
            Config["PublicApi"] = new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["ApiVersion"] = PublicApiVersionCurrent,
                ["EmitSuspicionEvents"] = true,
                ["EmitPenaltyEvents"] = true
            };
            SaveConfig();
        }

        private void EnsureConfigDefaults()
        {
            bool changed = false;

            if (Config["DefaultLanguage"] == null)
            {
                Config["DefaultLanguage"] = DefaultLanguageFallback;
                changed = true;
            }

            var publicApi = Config["PublicApi"] as Dictionary<string, object>;
            if (publicApi == null)
            {
                Config["PublicApi"] = new Dictionary<string, object>
                {
                    ["Enabled"] = true,
                    ["ApiVersion"] = PublicApiVersionCurrent,
                    ["EmitSuspicionEvents"] = true,
                    ["EmitPenaltyEvents"] = true
                };
                changed = true;
            }
            else
            {
                if (!publicApi.ContainsKey("Enabled"))
                {
                    publicApi["Enabled"] = true;
                    changed = true;
                }

                if (!publicApi.ContainsKey("ApiVersion") || string.IsNullOrWhiteSpace(publicApi["ApiVersion"].ToString()))
                {
                    publicApi["ApiVersion"] = PublicApiVersionCurrent;
                    changed = true;
                }

                if (!publicApi.ContainsKey("EmitSuspicionEvents"))
                {
                    publicApi["EmitSuspicionEvents"] = true;
                    changed = true;
                }

                if (!publicApi.ContainsKey("EmitPenaltyEvents"))
                {
                    publicApi["EmitPenaltyEvents"] = true;
                    changed = true;
                }
            }

            if (changed) SaveConfig();
        }

        private string GetConfiguredDefaultLanguage()
        {
            var raw = Config["DefaultLanguage"] != null ? Config["DefaultLanguage"].ToString() : DefaultLanguageFallback;
            return string.IsNullOrWhiteSpace(raw) ? DefaultLanguageFallback : raw.Trim().ToLowerInvariant();
        }

        private string NormalizeLanguageCode(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        }

        private List<string> GetSupportedLanguageCodes()
        {
            var supported = new List<string>();

            if (MessagesEn.Count > 0) supported.Add("en");
            if (MessagesHu.Count > 0 && !supported.Contains("hu")) supported.Add("hu");

            return supported;
        }

        private bool IsSupportedLanguage(string code)
        {
            var supported = GetSupportedLanguageCodes();
            return supported.Contains(NormalizeLanguageCode(code));
        }

        private Dictionary<string, object> GetPublicApiConfig()
        {
            var config = Config["PublicApi"] as Dictionary<string, object>;
            if (config == null)
            {
                config = new Dictionary<string, object>
                {
                    ["Enabled"] = true,
                    ["ApiVersion"] = PublicApiVersionCurrent,
                    ["EmitSuspicionEvents"] = true,
                    ["EmitPenaltyEvents"] = true
                };
                Config["PublicApi"] = config;
                SaveConfig();
            }

            return config;
        }

        private bool IsPublicApiEnabled()
        {
            var config = GetPublicApiConfig();
            if (!config.ContainsKey("Enabled")) return true;

            try
            {
                return Convert.ToBoolean(config["Enabled"]);
            }
            catch
            {
                return true;
            }
        }

        private bool ShouldEmitSuspicionEvents()
        {
            var config = GetPublicApiConfig();
            if (!config.ContainsKey("EmitSuspicionEvents")) return true;

            try
            {
                return Convert.ToBoolean(config["EmitSuspicionEvents"]);
            }
            catch
            {
                return true;
            }
        }

        private bool ShouldEmitPenaltyEvents()
        {
            var config = GetPublicApiConfig();
            if (!config.ContainsKey("EmitPenaltyEvents")) return true;

            try
            {
                return Convert.ToBoolean(config["EmitPenaltyEvents"]);
            }
            catch
            {
                return true;
            }
        }

        private string GetConfiguredApiVersion()
        {
            var config = GetPublicApiConfig();
            if (!config.ContainsKey("ApiVersion")) return PublicApiVersionCurrent;

            var value = config["ApiVersion"] != null ? config["ApiVersion"].ToString().Trim() : string.Empty;
            return string.IsNullOrWhiteSpace(value) ? PublicApiVersionCurrent : value;
        }

        private string GetMessageFromPack(Dictionary<string, string> pack, string key)
        {
            string value;
            return pack != null && pack.TryGetValue(key, out value) ? value : null;
        }

        private string GetConfiguredFallbackMessage(string key)
        {
            string cfgLang = GetConfiguredDefaultLanguage();
            if (cfgLang == "hu")
            {
                return GetMessageFromPack(MessagesHu, key) ?? GetMessageFromPack(MessagesEn, key);
            }

            return GetMessageFromPack(MessagesEn, key) ?? GetMessageFromPack(MessagesHu, key);
        }

        private string Msg(BasePlayer player, string key, params object[] args)
        {
            string message = null;

            if (player != null)
            {
                message = lang.GetMessage(key, this, player.UserIDString);
                if (string.IsNullOrEmpty(message) || message == key)
                {
                    message = null;
                }
            }

            if (string.IsNullOrEmpty(message))
            {
                message = GetConfiguredFallbackMessage(key);
            }

            if (string.IsNullOrEmpty(message))
            {
                message = "[MogyAC] Missing lang key: " + key;
            }

            if (args == null || args.Length == 0) return message;

            try
            {
                return string.Format(message, args);
            }
            catch
            {
                return message;
            }
        }

        void OnWeaponFired(BaseProjectile weapon, BasePlayer player)
        {
            if (player == null || weapon == null || player.IsNpc) return;

            string wName = weapon.ShortPrefabName.Replace(".entity", "");

            Dictionary<string, WeaponData> byWeapon;
            if (!_playerStats.TryGetValue(player.userID, out byWeapon))
            {
                byWeapon = new Dictionary<string, WeaponData>();
                _playerStats[player.userID] = byWeapon;
            }

            WeaponData weaponData;
            if (!byWeapon.TryGetValue(wName, out weaponData))
            {
                weaponData = new WeaponData();
                byWeapon[wName] = weaponData;
            }

            weaponData.AddMiss(0f);
        }

        void OnEntityTakeDamage(BaseEntity entity, HitInfo info)
        {
            if (info == null || info.InitiatorPlayer == null || entity == null) return;
            if (entity is BuildingBlock) return;

            BasePlayer targetPlayer = entity as BasePlayer;
            if (targetPlayer == null || targetPlayer.IsNpc || !targetPlayer.userID.IsSteamId()) return;

            BasePlayer attacker = info.InitiatorPlayer;
            if (attacker.IsNpc || !attacker.userID.IsSteamId()) return;

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
            float expiry = Config["MissExpirySeconds"] != null ? Convert.ToSingle(Config["MissExpirySeconds"]) : 20f;

            int limit = 40;
            var weaponsCfg = Config["Weapons"] as Dictionary<string, object>;
            if (weaponsCfg != null && weaponsCfg.ContainsKey(wName))
            {
                var entry = weaponsCfg[wName] as Dictionary<string, object>;
                if (entry != null && entry.ContainsKey("SampleCount"))
                {
                    limit = Convert.ToInt32(entry["SampleCount"]);
                }
            }

            Dictionary<string, WeaponData> byWeapon;
            if (!_playerStats.TryGetValue(attacker.userID, out byWeapon))
            {
                byWeapon = new Dictionary<string, WeaponData>();
                _playerStats[attacker.userID] = byWeapon;
            }

            WeaponData weaponData;
            if (!byWeapon.TryGetValue(wName, out weaponData))
            {
                weaponData = new WeaponData();
                byWeapon[wName] = weaponData;
            }

            weaponData.RegisterHit(dist, limit, expiry);

            var evaluation = EvaluateWeapon(wName, weaponData);
            ProcessSuspicionTransition(attacker, wName, evaluation);

            float globalNerf = GetLowestNerf(attacker.userID);
            if (!attacker.IsAdmin && globalNerf < 1.0f)
            {
                float originalDamage = info.damageTypes.Total();
                info.damageTypes.ScaleAll(globalNerf);
                float scaledDamage = info.damageTypes.Total();
                EmitPenaltyEvent(attacker, targetPlayer, wName, globalNerf, originalDamage, scaledDamage);
            }
        }

        private WeaponEvaluation EvaluateWeapon(string weaponName, WeaponData data)
        {
            var evaluation = new WeaponEvaluation
            {
                Accuracy = data.GetAccuracy(),
                SampleCount = data.History.Count,
                SuggestedNerf = 1f,
                HasEnoughData = data.History.Count >= 10
            };

            var weaponsCfg = Config["Weapons"] as Dictionary<string, object>;
            if (weaponsCfg == null || !weaponsCfg.ContainsKey(weaponName))
            {
                evaluation.MaxAccuracy = 1f;
                evaluation.SafeDistance = 1f;
                evaluation.WeightedScore = data.GetWeightedScore(1f);
                return evaluation;
            }

            var cfg = weaponsCfg[weaponName] as Dictionary<string, object>;
            if (cfg == null)
            {
                evaluation.MaxAccuracy = 1f;
                evaluation.SafeDistance = 1f;
                evaluation.WeightedScore = data.GetWeightedScore(1f);
                return evaluation;
            }

            evaluation.MaxAccuracy = Convert.ToSingle(cfg["MaxAccuracy"]);
            evaluation.SafeDistance = Convert.ToSingle(cfg["SafeDistance"]);
            evaluation.WeightedScore = data.GetWeightedScore(evaluation.SafeDistance);

            if (!evaluation.HasEnoughData || evaluation.Accuracy <= evaluation.MaxAccuracy)
            {
                evaluation.IsSuspicious = false;
                evaluation.SuggestedNerf = 1f;
                return evaluation;
            }

            float excess = (evaluation.Accuracy - evaluation.MaxAccuracy) / (1.0f - evaluation.MaxAccuracy);
            float penaltyFactor = excess * (evaluation.WeightedScore > 1.0f ? Mathf.Pow(evaluation.WeightedScore, 2f) : 1.0f);
            float currentNerf = 1.0f - penaltyFactor;

            if (evaluation.Accuracy > 0.95f && evaluation.WeightedScore > 1.2f) currentNerf = 0f;
            if (currentNerf < 0.30f) currentNerf = 0f;

            evaluation.SuggestedNerf = Mathf.Clamp(currentNerf, 0f, 1.0f);
            evaluation.IsSuspicious = true;
            return evaluation;
        }

        private void ProcessSuspicionTransition(BasePlayer attacker, string weaponName, WeaponEvaluation evaluation)
        {
            if (attacker == null || !IsPublicApiEnabled() || !ShouldEmitSuspicionEvents()) return;

            HashSet<string> suspiciousWeapons;
            if (!_activeSuspicionByWeapon.TryGetValue(attacker.userID, out suspiciousWeapons))
            {
                suspiciousWeapons = new HashSet<string>();
                _activeSuspicionByWeapon[attacker.userID] = suspiciousWeapons;
            }

            if (!evaluation.IsSuspicious)
            {
                suspiciousWeapons.Remove(weaponName);
                return;
            }

            if (suspiciousWeapons.Contains(weaponName)) return;

            suspiciousWeapons.Add(weaponName);
            EmitSuspicionEvent(attacker.userID, weaponName, evaluation);
        }

        private void EmitSuspicionEvent(ulong playerId, string weaponName, WeaponEvaluation evaluation)
        {
            var payload = new Dictionary<string, object>
            {
                ["apiVersion"] = GetConfiguredApiVersion(),
                ["playerId"] = playerId,
                ["weaponShortName"] = weaponName,
                ["accuracy"] = evaluation.Accuracy,
                ["maxAccuracy"] = evaluation.MaxAccuracy,
                ["weightedScore"] = evaluation.WeightedScore,
                ["suggestedNerf"] = evaluation.SuggestedNerf,
                ["sampleCount"] = evaluation.SampleCount,
                ["timestampUtc"] = DateTime.UtcNow.ToString("o")
            };

            Interface.CallHook("OnMogyAcSuspicion", payload);
        }

        private void EmitPenaltyEvent(BasePlayer attacker, BasePlayer target, string weaponName, float appliedMultiplier, float originalDamage, float scaledDamage)
        {
            if (attacker == null || !IsPublicApiEnabled() || !ShouldEmitPenaltyEvents()) return;

            var payload = new Dictionary<string, object>
            {
                ["apiVersion"] = GetConfiguredApiVersion(),
                ["attackerId"] = attacker.userID,
                ["targetId"] = target != null ? target.userID : 0UL,
                ["weaponShortName"] = weaponName,
                ["appliedMultiplier"] = appliedMultiplier,
                ["originalDamage"] = originalDamage,
                ["scaledDamage"] = scaledDamage,
                ["timestampUtc"] = DateTime.UtcNow.ToString("o")
            };

            Interface.CallHook("OnMogyAcPenaltyApplied", payload);
        }

        private float GetLowestNerf(ulong userId)
        {
            Dictionary<string, WeaponData> byWeapon;
            if (!_playerStats.TryGetValue(userId, out byWeapon)) return 1.0f;

            float lowestNerf = 1.0f;

            foreach (var weaponEntry in byWeapon)
            {
                var evaluation = EvaluateWeapon(weaponEntry.Key, weaponEntry.Value);
                if (evaluation.SuggestedNerf < lowestNerf) lowestNerf = evaluation.SuggestedNerf;
            }

            return Mathf.Clamp(lowestNerf, 0f, 1.0f);
        }

        object GetApiVersion()
        {
            return GetConfiguredApiVersion();
        }

        object GetPlayerAcState(ulong playerId)
        {
            Dictionary<string, WeaponData> byWeapon;
            if (!_playerStats.TryGetValue(playerId, out byWeapon))
            {
                return null;
            }

            var weapons = new List<Dictionary<string, object>>();
            foreach (var weaponEntry in byWeapon)
            {
                var evaluation = EvaluateWeapon(weaponEntry.Key, weaponEntry.Value);
                weapons.Add(new Dictionary<string, object>
                {
                    ["weaponShortName"] = weaponEntry.Key,
                    ["accuracy"] = evaluation.Accuracy,
                    ["sampleCount"] = evaluation.SampleCount,
                    ["weightedScore"] = evaluation.WeightedScore,
                    ["maxAccuracy"] = evaluation.MaxAccuracy,
                    ["safeDistance"] = evaluation.SafeDistance,
                    ["isSuspicious"] = evaluation.IsSuspicious,
                    ["suggestedNerf"] = evaluation.SuggestedNerf
                });
            }

            return new Dictionary<string, object>
            {
                ["apiVersion"] = GetConfiguredApiVersion(),
                ["playerId"] = playerId,
                ["globalNerf"] = GetLowestNerf(playerId),
                ["weapons"] = weapons,
                ["timestampUtc"] = DateTime.UtcNow.ToString("o")
            };
        }

        [ChatCommand("ac-check")]
        void CmdChatCheck(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                SendReply(player, Msg(player, "NoPermission"));
                return;
            }

            BasePlayer target = args.Length > 0 ? BasePlayer.Find(args[0]) : player;
            if (target == null)
            {
                SendReply(player, Msg(player, "PlayerNotFound"));
                return;
            }

            float globalDmg = GetLowestNerf(target.userID);
            string globalColor = globalDmg < 0.5f ? "#ff6666" : (globalDmg < 1.0f ? "#ffff55" : "#88ff88");

            string report = $"<color=#55ff55>{Msg(player, "StatsHeader", target.displayName)}</color>\n";
            report += $"<color=#ffffff>{Msg(player, "GlobalDamageLabel")}: </color><color={globalColor}>{globalDmg:P0}</color>\n";
            report += "----------------------------\n";

            Dictionary<string, WeaponData> byWeapon;
            if (_playerStats.TryGetValue(target.userID, out byWeapon))
            {
                foreach (var w in byWeapon)
                {
                    report += $"<color=#ffff55>{Msg(player, "WeaponLine", w.Key, w.Value.GetAccuracy(), w.Value.History.Count)}</color>\n";
                }
            }
            else
            {
                report += Msg(player, "NoData");
            }

            SendReply(player, report);
        }

        [ChatCommand("ac-list")]
        void CmdAcList(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                SendReply(player, Msg(player, "NoPermission"));
                return;
            }

            string report = $"<color=#55ff55>{Msg(player, "ActiveListHeader")}</color>\n";
            report += $"<color=#aaaaaa>{Msg(player, "ActiveListColumns")}</color>\n";

            foreach (var target in BasePlayer.activePlayerList)
            {
                float globalDmg = GetLowestNerf(target.userID);
                float totalAcc = 0f;
                int count = 0;

                Dictionary<string, WeaponData> byWeapon;
                if (_playerStats.TryGetValue(target.userID, out byWeapon) && byWeapon.Count > 0)
                {
                    foreach (var w in byWeapon)
                    {
                        totalAcc += w.Value.GetAccuracy();
                        count++;
                    }
                }

                float avgAcc = count > 0 ? (totalAcc / count) : 0f;
                string dmgColor = globalDmg < 1.0f ? "#ff6666" : "#88ff88";
                report += $"{target.displayName} | {avgAcc:P0} | <color={dmgColor}>{globalDmg:P0}</color>\n";
            }

            SendReply(player, report);
        }

        [ChatCommand("ac-reset")]
        void CmdAcReset(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                SendReply(player, Msg(player, "NoPermission"));
                return;
            }

            if (args.Length == 0)
            {
                SendReply(player, Msg(player, "ResetUsage"));
                return;
            }

            BasePlayer target = BasePlayer.Find(args[0]);
            if (target != null && _playerStats.Remove(target.userID))
            {
                _activeSuspicionByWeapon.Remove(target.userID);
                SendReply(player, $"<color=#55ff55>{Msg(player, "StatsResetSuccess", target.displayName)}</color>");
            }
            else
            {
                SendReply(player, Msg(player, "PlayerNotFound"));
            }
        }

        [ChatCommand("ac-lang")]
        void CmdAcLang(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin)
            {
                SendReply(player, Msg(player, "NoPermission"));
                return;
            }

            if (args.Length == 0)
            {
                SendReply(player, Msg(player, "LangUsage"));
                return;
            }

            string requested = NormalizeLanguageCode(args[0]);
            var supported = GetSupportedLanguageCodes();
            string supportedList = string.Join(", ", supported.ToArray());

            if (!IsSupportedLanguage(requested))
            {
                SendReply(player, Msg(player, "LangUnsupported", requested, supportedList));
                return;
            }

            string current = GetConfiguredDefaultLanguage();
            if (current == requested)
            {
                SendReply(player, Msg(player, "LangAlreadySet", requested));
                return;
            }

            Config["DefaultLanguage"] = requested;
            SaveConfig();
            SendReply(player, Msg(player, "LangUpdated", requested));
        }
    }
}


