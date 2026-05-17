using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Mogy AntiCheat", "Mogy", "1.9.6")]
    [Description("Tracks weapon accuracy trends and dynamically reduces suspicious player damage using configurable thresholds and localized admin commands.")]
    public class MogyAntiCheat : RustPlugin
    {
        private const string DefaultLanguageFallback = "en";
        private const string PublicApiVersionCurrent = "1.2.0";
        private const string DebugLogFileName = "MogyAntiCheat_Debug.log";
        private const float WebhookRateWindowSeconds = 1f;
        private const string PermissionAdmin = "mogyanticheat.admin";
        private const string PermissionBypass = "mogyanticheat.bypass";
        private const string RuntimeOxide = "Oxide/uMod";
        private const string RuntimeCarbon = "Carbon";
        private const int TelemetryQueueMaxSize = 5000;
        private const float TelemetryFlushIntervalSeconds = 300f;
        private const int PingBaselineSamples = 50;

        private DynamicConfigFile _storedData;
        private DynamicConfigFile _kdaData;
        private string _debugLogPath;
        private string _runtimeName = RuntimeOxide;
        private string _runtimeDataDirectory = string.Empty;
        private readonly Dictionary<ulong, Dictionary<string, WeaponData>> _playerStats = new Dictionary<ulong, Dictionary<string, WeaponData>>();
        private readonly Dictionary<ulong, float> _lastHitTime = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, HashSet<string>> _activeSuspicionByWeapon = new Dictionary<ulong, HashSet<string>>();
        private readonly Dictionary<ulong, PlayerPingStats> _playerPingStats = new Dictionary<ulong, PlayerPingStats>();
        private readonly Dictionary<ulong, PlayerKDAStats> _playerKDAStats = new Dictionary<ulong, PlayerKDAStats>();
        private readonly Dictionary<ulong, HashSet<ulong>> _damageContributors = new Dictionary<ulong, HashSet<ulong>>();
        private readonly Dictionary<ulong, List<LagSwitchIncident>> _lagswitchIncidents = new Dictionary<ulong, List<LagSwitchIncident>>();
        private readonly Dictionary<ulong, float> _lastDisconnectTime = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, int> _connectionDropCount = new Dictionary<ulong, int>();
        private readonly List<ShotTelemetryEvent> _telemetryQueue = new List<ShotTelemetryEvent>();
        private readonly Queue<WebhookEnvelope> _webhookQueue = new Queue<WebhookEnvelope>();
        private Timer _webhookPumpTimer;
        private Timer _telemetryFlushTimer;
        private bool _webhookRequestInFlight;
        private float _webhookWindowStart;
        private int _webhookSentInWindow;

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
            ["LangUnsupported"] = "Unsupported language: {0}. Supported: {1}",
            ["DebugUsage"] = "Usage: /ac-debug <on|off>",
            ["DebugStatus"] = "Debug mode is currently: {0}.",
            ["DebugUpdated"] = "Debug mode set to: {0}.",
            ["DebugAlreadySet"] = "Debug mode is already: {0}.",
            ["WeaponCfgUsage"] = "Usage: /ac-weapon <weaponShortName|active> <MaxAccuracy|SampleCount|SafeDistance> <value>",
            ["WeaponCfgNoActiveWeapon"] = "No active projectile weapon found.",
            ["WeaponCfgFieldInvalid"] = "Invalid field: {0}. Allowed: MaxAccuracy, SampleCount, SafeDistance.",
            ["WeaponCfgValueInvalid"] = "Invalid value for {0}: {1}.",
            ["WeaponCfgUpdated"] = "Weapon config updated: {0}.{1} = {2}.",
            ["WhyUsage"] = "Usage: /ac-why [weaponShortName|active]",
            ["WhyNoWeaponData"] = "No tracked data for weapon: {0}.",
            ["WhyNoConfig"] = "No config found for weapon: {0}. Add it with /ac-weapon.",
            ["WhySummary"] = "Weapon: {0} | Acc: {1:P1} | Shots: {2} | Max: {3:P1} | Weighted: {4:F2} | SuggestedNerf: {5:P0} | GlobalNerf: {6:P0}",
            ["WhyReasonNoData"] = "Reason: not enough samples yet (minimum 10).",
            ["WhyReasonBelowThreshold"] = "Reason: accuracy is within configured threshold.",
            ["DebugLogPath"] = "Debug log file: {0}",
            ["DebugLogCleared"] = "Debug log file cleared.",
            ["DebugLogClearFailed"] = "[MogyAC] Debug log clear failed: {0}",
            ["HelpHeader"] = "=== MogyAC COMMANDS ===",
            ["HelpCheck"] = "/ac-check [playerName] - Show detailed anti-cheat stats for a player.",
            ["HelpList"] = "/ac-list - List online players with average accuracy and damage multiplier.",
            ["HelpReset"] = "/ac-reset <playerName> - Clear tracked stats for a player.",
            ["HelpLang"] = "/ac-lang <languageCode> - Set default plugin language.",
            ["HelpDebug"] = "/ac-debug <on|off> - Toggle debug mode.",
            ["HelpWeapon"] = "/ac-weapon <weapon|active> <MaxAccuracy|SampleCount|SafeDistance> <value> - Update weapon config.",
            ["HelpDebugLog"] = "/ac-debug-log [clear] - Show or clear debug log file.",
            ["HelpWhy"] = "/ac-why [weapon|active] - Explain why nerf is or is not applied.",
            ["HelpStats"] = "/ac-stats [playerName] - Show K/D/A and ping stats for a player.",
            ["HelpHelp"] = "/ac-help - Show this command list.",
            ["StatsKDA"] = "K/D/A: {0}/{1}/{2} (KDR: {3:F2})",
            ["StatsPing"] = "Ping: avg={0:F0}ms  min={1}ms  max={2}ms  stddev={3:F1}ms",
            ["StatsNoPingData"] = "Ping: No baseline data yet.",
            ["StatsPingAnomaly"] = "Ping anomalies (24h): {0}",
            ["LsHeader"] = "=== MogyAC Lagswitch Audit: {0} ===",
            ["LsNoIncidents"] = "No lagswitch incidents recorded.",
            ["LsIncident"] = "[{0}] Victim: {1} | Weapon: {2} @ {3:F0}m | Confidence: {4:F2}",
            ["LsIncidentPing"] = "  Ping: {0}ms (baseline: {1:F0}ms, spike: +{2}ms)",
            ["LsIncidentKill"] = "  Accuracy: {0:P1} | Headshot: {1}",
            ["LsIncidentReconnect"] = "  Reconnect score: {0:F2}",
            ["LsSummary"] = "Summary: {0} total | 24h: {1} | Avg confidence: {2:F2}",
            ["LsPatternWarning"] = "WARNING: Lagswitch pattern detected!",
            ["HelpLagswitch"] = "/ac-lagswitch-audit [playerName] - Show lagswitch forensic timeline."
        };

        private static readonly Dictionary<string, string> MessagesHu = new Dictionary<string, string>
        {
            ["NoPermission"] = "Nincs jogosultságod ehhez a parancshoz.",
            ["PlayerNotFound"] = "Játékos nem található.",
            ["NoData"] = "Nincs adat.",
            ["StatsHeader"] = "=== MogyAC STAT: {0} ===",
            ["GlobalDamageLabel"] = "GLOBAL SEBZÉS",
            ["WeaponLine"] = "{0}: {1:P1} ({2} lövés)",
            ["ActiveListHeader"] = "=== MogyAC AKTIV LISTA ===",
            ["ActiveListColumns"] = "Játékos | Átlag Acc | Sebzés",
            ["StatsResetSuccess"] = "[MogyAC] {0} statisztikái törölve.",
            ["ResetUsage"] = "Használat: /ac-reset <játékosnév>",
            ["LangUsage"] = "Használat: /ac-lang <nyelvkód>",
            ["LangUpdated"] = "Alapértelmezett nyelv beállítva: {0}.",
            ["LangAlreadySet"] = "Az alapértelmezett nyelv már ez: {0}.",
            ["LangUnsupported"] = "Nem támogatott nyelv: {0}. Támogatott: {1}",
            ["DebugUsage"] = "Használat: /ac-debug <on|off>",
            ["DebugStatus"] = "A debug mód jelenleg: {0}.",
            ["DebugUpdated"] = "Debug mód beállítva: {0}.",
            ["DebugAlreadySet"] = "A debug mód már ez: {0}.",
            ["WeaponCfgUsage"] = "Használat: /ac-weapon <fegyverShortName|active> <MaxAccuracy|SampleCount|SafeDistance> <érték>",
            ["WeaponCfgNoActiveWeapon"] = "Nincs aktív lövedékes fegyver.",
            ["WeaponCfgFieldInvalid"] = "Érvénytelen mező: {0}. Engedélyezett: MaxAccuracy, SampleCount, SafeDistance.",
            ["WeaponCfgValueInvalid"] = "Érvénytelen érték ehhez: {0}: {1}.",
            ["WeaponCfgUpdated"] = "Fegyver konfiguráció frissítve: {0}.{1} = {2}.",
            ["WhyUsage"] = "Használat: /ac-why [weaponShortName|active]",
            ["WhyNoWeaponData"] = "Nincs tárolt adat ehhez a fegyverhez: {0}.",
            ["WhyNoConfig"] = "Nincs konfiguráció ehhez a fegyverhez: {0}. Hozzáadás: /ac-weapon.",
            ["WhySummary"] = "Fegyver: {0} | Acc: {1:P1} | Lövés: {2} | Max: {3:P1} | Súlyozott: {4:F2} | JavasoltNerf: {5:P0} | GlobálNerf: {6:P0}",
            ["WhyReasonNoData"] = "Ok: még nincs elég minta (minimum 10).",
            ["WhyReasonBelowThreshold"] = "Ok: a pontosság a beállított küszöbön belül van.",
            ["DebugLogPath"] = "Debug log fájl: {0}",
            ["DebugLogCleared"] = "Debug log fájl törölve.",
            ["DebugLogClearFailed"] = "[MogyAC] Debug log törlése sikertelen: {0}",
            ["HelpHeader"] = "=== MogyAC PARANCSOK ===",
            ["HelpCheck"] = "/ac-check [jatekosnev] - Részletes anti-cheat stat egy játékosról.",
            ["HelpList"] = "/ac-list - Online játékosok listázása átlag pontossággal és sebzés szorzóval.",
            ["HelpReset"] = "/ac-reset <jatekosnev> - Játékos követett statjainak törlése.",
            ["HelpLang"] = "/ac-lang <nyelvkod> - Alapértelmezett plugin nyelv beállítása.",
            ["HelpDebug"] = "/ac-debug <on|off> - Debug mód ki/be kapcsolása.",
            ["HelpWeapon"] = "/ac-weapon <fegyver|active> <MaxAccuracy|SampleCount|SafeDistance> <ertek> - Fegyver config frissítése.",
            ["HelpDebugLog"] = "/ac-debug-log [clear] - Debug log fájl útvonala vagy törlése.",
            ["HelpWhy"] = "/ac-why [weapon|active] - Megmutatja, miért (nem) aktív a nerf.",
            ["HelpStats"] = "/ac-stats [jatekosnev] - K/D/A és ping statisztika egy játékosról.",
            ["HelpHelp"] = "/ac-help - Ez a parancslista.",
            ["StatsKDA"] = "K/D/A: {0}/{1}/{2} (KDR: {3:F2})",
            ["StatsPing"] = "Ping: avg={0:F0}ms  min={1}ms  max={2}ms  stddev={3:F1}ms",
            ["StatsNoPingData"] = "Ping: Még nincs alap adat.",
            ["StatsPingAnomaly"] = "Ping anomáliák (24h): {0}",
            ["LsHeader"] = "=== MogyAC Lagswitch Audit: {0} ===",
            ["LsNoIncidents"] = "Nincs rögzített lagswitch incidens.",
            ["LsIncident"] = "[{0}] Áldozat: {1} | Fegyver: {2} @ {3:F0}m | Biztonság: {4:F2}",
            ["LsIncidentPing"] = "  Ping: {0}ms (alap: {1:F0}ms, tüske: +{2}ms)",
            ["LsIncidentKill"] = "  Pontosság: {0:P1} | Fejlövés: {1}",
            ["LsIncidentReconnect"] = "  Reconnect pontszám: {0:F2}",
            ["LsSummary"] = "Összefoglaló: {0} összesen | 24h: {1} | Átl. biztonság: {2:F2}",
            ["LsPatternWarning"] = "FIGYELEM: Lagswitch minta észlelve!",
            ["HelpLagswitch"] = "/ac-lagswitch-audit [jatekosnev] - Lagswitch törvényszéki idővonal."
        };

        private struct ShotResult
        {
            public bool IsHit;
            public float Distance;
            public int PingMs;
            public int DeltaPingMs;
        }

        private struct PendingShot
        {
            public float Realtime;
            public float Distance;
            public int PingMs;
            public int DeltaPingMs;
        }

        private class WeaponData
        {
            public readonly List<ShotResult> History = new List<ShotResult>();
            public readonly List<PendingShot> PendingMisses = new List<PendingShot>();

            public void AddMiss(float distance, int pingMs = 0, int deltaPingMs = 0)
            {
                PendingMisses.Add(new PendingShot
                {
                    Realtime = UnityEngine.Time.realtimeSinceStartup,
                    Distance = distance,
                    PingMs = pingMs,
                    DeltaPingMs = deltaPingMs
                });
                if (PendingMisses.Count > 100) PendingMisses.RemoveAt(0);
            }

            public void RegisterHit(float distance, int limit, float expiryTime, int pingMs = 0, int deltaPingMs = 0)
            {
                float now = UnityEngine.Time.realtimeSinceStartup;
                int lastIndex = -1;

                for (int i = PendingMisses.Count - 1; i >= 0; i--)
                {
                    if (now - PendingMisses[i].Realtime <= expiryTime)
                    {
                        lastIndex = i;
                        break;
                    }
                }

                if (lastIndex != -1)
                {
                    for (int i = 0; i < lastIndex; i++)
                    {
                        if (now - PendingMisses[i].Realtime <= expiryTime)
                        {
                            History.Add(new ShotResult
                            {
                                IsHit = false,
                                Distance = PendingMisses[i].Distance,
                                PingMs = PendingMisses[i].PingMs,
                                DeltaPingMs = PendingMisses[i].DeltaPingMs
                            });
                        }
                    }

                    History.Add(new ShotResult { IsHit = true, Distance = distance, PingMs = pingMs, DeltaPingMs = deltaPingMs });
                    PendingMisses.RemoveRange(0, lastIndex + 1);
                }
                else
                {
                    History.Add(new ShotResult { IsHit = true, Distance = distance, PingMs = pingMs, DeltaPingMs = deltaPingMs });
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

        private class WebhookEnvelope
        {
            public string EventName;
            public Dictionary<string, object> Payload;
            public int Attempt;
        }

        private class PlayerPingStats
        {
            public double EMA;
            public int Min = int.MaxValue;
            public int Max;
            public double Variance;
            public long SampleCount;
            public int LastPing;
            public int AnomalyCount;

            public double StdDev => SampleCount > 1 ? Math.Sqrt(Math.Abs(Variance)) : 0.0;
            public bool HasBaseline => SampleCount >= PingBaselineSamples;

            public bool IsAnomalous(int ping, double thresholdStdDev)
            {
                if (!HasBaseline) return false;
                double sd = StdDev;
                if (sd < 1.0) return false;
                return Math.Abs(ping - EMA) > sd * thresholdStdDev;
            }

            public void Update(int ping)
            {
                if (SampleCount == 0)
                {
                    EMA = ping;
                    Variance = 0;
                }
                else
                {
                    double prevEMA = EMA;
                    EMA = EMA * 0.9 + ping * 0.1;
                    // Welford online variance
                    double delta = ping - prevEMA;
                    double delta2 = ping - EMA;
                    Variance = ((Variance * (SampleCount - 1)) + delta * delta2) / SampleCount;
                }
                if (ping < Min) Min = ping;
                if (ping > Max) Max = ping;
                LastPing = ping;
                SampleCount++;
            }
        }

        private class PlayerKDAStats
        {
            public int Kills;
            public int Deaths;
            public int Assists;

            public float KDRatio => Deaths == 0 ? Kills : (float)Kills / Deaths;
        }

        private class ShotTelemetryEvent
        {
            public long TimestampMs;
            public ulong PlayerId;
            public string WeaponName;
            public float Distance;
            public bool Hit;
            public int PingMs;
            public int DeltaPingMs;
            public float AccuracyInWindow;
            public string EventType;
        }

        private class LagSwitchIncident
        {
            public long TimestampMs;
            public ulong VictimId;
            public string WeaponName;
            public float Distance;
            public float KillAccuracy;
            public bool WasHeadshot;
            public int PingAtKill;
            public double PingBaselineAvg;
            public double PingBaselineStdDev;
            public int PingSpike;
            public float PingSpikeScore;
            public float KillQualityScore;
            public float ReconnectScore;
            public float Confidence;
        }

        void Init()
        {
            _runtimeName = DetectRuntimeName();
            _runtimeDataDirectory = ResolveDataDirectory(_runtimeName);

            lang.RegisterMessages(MessagesEn, this, "en");
            lang.RegisterMessages(MessagesHu, this, "hu");
            permission.RegisterPermission(PermissionAdmin, this);
            permission.RegisterPermission(PermissionBypass, this);

            _storedData = Interface.Oxide.DataFileSystem.GetFile("MogyAntiCheat_Stats");
            _kdaData = Interface.Oxide.DataFileSystem.GetFile("MogyAntiCheat_KDA");
            _debugLogPath = Path.Combine(_runtimeDataDirectory, DebugLogFileName);
            LoadStats();
            LoadKDAStats();
            EnsureConfigDefaults();
            _webhookWindowStart = UnityEngine.Time.realtimeSinceStartup;
            _webhookPumpTimer = timer.Every(0.25f, PumpWebhookQueue);
            _telemetryFlushTimer = timer.Every(TelemetryFlushIntervalSeconds, FlushTelemetryQueue);

            Puts($"Runtime detected: {_runtimeName} | Data directory: {_runtimeDataDirectory}");
        }

        void OnServerSave()
        {
            SaveStats();
            SaveKDAStats();
        }

        void Unload()
        {
            _webhookPumpTimer?.Destroy();
            _telemetryFlushTimer?.Destroy();
            FlushTelemetryQueue();
            SaveStats();
            SaveKDAStats();
        }

        private bool HasAccess(BasePlayer player, string permissionName)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, permissionName));
        }

        private bool HasBypass(BasePlayer player)
        {
            return player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermissionBypass));
        }

        private string DetectRuntimeName()
        {
            try
            {
                var hasCarbonAssembly = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .Any(a => a.GetName().Name.IndexOf("Carbon", StringComparison.OrdinalIgnoreCase) >= 0);

                return hasCarbonAssembly ? RuntimeCarbon : RuntimeOxide;
            }
            catch
            {
                return RuntimeOxide;
            }
        }

        private string ResolveDataDirectory(string runtimeName)
        {
            string oxideDataDirectory = Interface.Oxide.DataDirectory;
            if (!string.Equals(runtimeName, RuntimeCarbon, StringComparison.OrdinalIgnoreCase))
            {
                return oxideDataDirectory;
            }

            string carbonDataDirectory = TryResolveCarbonDataDirectory(oxideDataDirectory);
            return string.IsNullOrWhiteSpace(carbonDataDirectory) ? oxideDataDirectory : carbonDataDirectory;
        }

        private string TryResolveCarbonDataDirectory(string oxideDataDirectory)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(oxideDataDirectory) || !Directory.Exists(oxideDataDirectory))
                {
                    return string.Empty;
                }

                var dataDir = new DirectoryInfo(oxideDataDirectory);
                if (dataDir.Parent == null || dataDir.Parent.Parent == null)
                {
                    return string.Empty;
                }

                string identityRoot = dataDir.Parent.Parent.FullName;
                string carbonData = Path.Combine(identityRoot, "carbon", "data");

                return Directory.Exists(carbonData) ? carbonData : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

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

        private void SaveKDAStats()
        {
            var data = new Dictionary<string, Dictionary<string, int>>();
            foreach (var entry in _playerKDAStats)
            {
                data[entry.Key.ToString()] = new Dictionary<string, int>
                {
                    ["Kills"] = entry.Value.Kills,
                    ["Deaths"] = entry.Value.Deaths,
                    ["Assists"] = entry.Value.Assists
                };
            }
            _kdaData.WriteObject(data);
        }

        private void LoadKDAStats()
        {
            var data = _kdaData.ReadObject<Dictionary<string, Dictionary<string, int>>>();
            if (data == null) return;

            foreach (var entry in data)
            {
                ulong userId;
                if (!ulong.TryParse(entry.Key, out userId)) continue;

                var kda = new PlayerKDAStats();
                int v;
                if (entry.Value.TryGetValue("Kills", out v)) kda.Kills = v;
                if (entry.Value.TryGetValue("Deaths", out v)) kda.Deaths = v;
                if (entry.Value.TryGetValue("Assists", out v)) kda.Assists = v;
                _playerKDAStats[userId] = kda;
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
            Config["DebugMode"] = false;
            Config["PublicApi"] = new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["ApiVersion"] = PublicApiVersionCurrent,
                ["EmitSuspicionEvents"] = true,
                ["EmitPenaltyEvents"] = true
            };
            Config["Webhook"] = new Dictionary<string, object>
            {
                ["Enabled"] = false,
                ["Endpoint"] = "",
                ["AuthToken"] = "",
                ["AuthHeader"] = "Authorization",
                ["MaxRetries"] = 3,
                ["BaseBackoffSeconds"] = 1.5,
                ["MaxBackoffSeconds"] = 20.0,
                ["RateLimitPerSecond"] = 2,
                ["QueueMaxSize"] = 500,
                ["EmitSuspicionEvents"] = true,
                ["EmitPenaltyEvents"] = true
            };
            Config["PingMonitoring"] = new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["AnomalyThresholdStdDev"] = 2.5
            };
            Config["EventLogging"] = new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["FlushIntervalSeconds"] = 300,
                ["QueueMaxSize"] = 5000
            };
            Config["KDATracking"] = new Dictionary<string, object>
            {
                ["Enabled"] = true
            };
            Config["LagswitchDetection"] = new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["Threshold"] = 0.70,
                ["PatternThreshold"] = 0.75,
                ["MinIncidentsForPattern"] = 3,
                ["PingSpikeMinimumMs"] = 50,
                ["PreKillWindowMs"] = 1000
            };
            SaveConfig();
        }

        private void EnsureConfigDefaults()
        {
            bool changed = false;

            if (Config["DefaultLanguage"] == null) { Config["DefaultLanguage"] = DefaultLanguageFallback; changed = true; }
            if (Config["DebugMode"] == null) { Config["DebugMode"] = false; changed = true; }

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
                if (!publicApi.ContainsKey("Enabled")) { publicApi["Enabled"] = true; changed = true; }
                if (!publicApi.ContainsKey("ApiVersion") || string.IsNullOrWhiteSpace(publicApi["ApiVersion"].ToString())) { publicApi["ApiVersion"] = PublicApiVersionCurrent; changed = true; }
                if (!publicApi.ContainsKey("EmitSuspicionEvents")) { publicApi["EmitSuspicionEvents"] = true; changed = true; }
                if (!publicApi.ContainsKey("EmitPenaltyEvents")) { publicApi["EmitPenaltyEvents"] = true; changed = true; }
            }

            if (EnsureWebhookConfigDefaults()) changed = true;

            var pingCfg = Config["PingMonitoring"] as Dictionary<string, object>;
            if (pingCfg == null)
            {
                Config["PingMonitoring"] = new Dictionary<string, object> { ["Enabled"] = true, ["AnomalyThresholdStdDev"] = 2.5 };
                changed = true;
            }
            else
            {
                if (!pingCfg.ContainsKey("Enabled")) { pingCfg["Enabled"] = true; changed = true; }
                if (!pingCfg.ContainsKey("AnomalyThresholdStdDev")) { pingCfg["AnomalyThresholdStdDev"] = 2.5; changed = true; }
            }

            var eventCfg = Config["EventLogging"] as Dictionary<string, object>;
            if (eventCfg == null)
            {
                Config["EventLogging"] = new Dictionary<string, object> { ["Enabled"] = true, ["FlushIntervalSeconds"] = 300, ["QueueMaxSize"] = 5000 };
                changed = true;
            }

            var kdaCfg = Config["KDATracking"] as Dictionary<string, object>;
            if (kdaCfg == null)
            {
                Config["KDATracking"] = new Dictionary<string, object> { ["Enabled"] = true };
                changed = true;
            }

            var lsCfg = Config["LagswitchDetection"] as Dictionary<string, object>;
            if (lsCfg == null)
            {
                Config["LagswitchDetection"] = new Dictionary<string, object>
                {
                    ["Enabled"] = true, ["Threshold"] = 0.70, ["PatternThreshold"] = 0.75,
                    ["MinIncidentsForPattern"] = 3, ["PingSpikeMinimumMs"] = 50, ["PreKillWindowMs"] = 1000
                };
                changed = true;
            }
            else
            {
                if (!lsCfg.ContainsKey("Enabled")) { lsCfg["Enabled"] = true; changed = true; }
                if (!lsCfg.ContainsKey("Threshold")) { lsCfg["Threshold"] = 0.70; changed = true; }
                if (!lsCfg.ContainsKey("PatternThreshold")) { lsCfg["PatternThreshold"] = 0.75; changed = true; }
                if (!lsCfg.ContainsKey("MinIncidentsForPattern")) { lsCfg["MinIncidentsForPattern"] = 3; changed = true; }
                if (!lsCfg.ContainsKey("PingSpikeMinimumMs")) { lsCfg["PingSpikeMinimumMs"] = 50; changed = true; }
                if (!lsCfg.ContainsKey("PreKillWindowMs")) { lsCfg["PreKillWindowMs"] = 1000; changed = true; }
            }

            if (changed) SaveConfig();
        }

        private bool IsPingMonitoringEnabled()
        {
            var cfg = Config["PingMonitoring"] as Dictionary<string, object>;
            if (cfg == null || !cfg.ContainsKey("Enabled")) return true;
            try { return Convert.ToBoolean(cfg["Enabled"]); } catch { return true; }
        }

        private double GetPingAnomalyThreshold()
        {
            var cfg = Config["PingMonitoring"] as Dictionary<string, object>;
            if (cfg == null || !cfg.ContainsKey("AnomalyThresholdStdDev")) return 2.5;
            try { return Convert.ToDouble(cfg["AnomalyThresholdStdDev"]); } catch { return 2.5; }
        }

        private bool IsKDATrackingEnabled()
        {
            var cfg = Config["KDATracking"] as Dictionary<string, object>;
            if (cfg == null || !cfg.ContainsKey("Enabled")) return true;
            try { return Convert.ToBoolean(cfg["Enabled"]); } catch { return true; }
        }

        private bool IsEventLoggingEnabled()
        {
            var cfg = Config["EventLogging"] as Dictionary<string, object>;
            if (cfg == null || !cfg.ContainsKey("Enabled")) return true;
            try { return Convert.ToBoolean(cfg["Enabled"]); } catch { return true; }
        }

        private bool IsLagswitchDetectionEnabled()
        {
            var cfg = Config["LagswitchDetection"] as Dictionary<string, object>;
            if (cfg == null || !cfg.ContainsKey("Enabled")) return true;
            try { return Convert.ToBoolean(cfg["Enabled"]); } catch { return true; }
        }

        private float GetLagswitchThreshold()
        {
            var cfg = Config["LagswitchDetection"] as Dictionary<string, object>;
            if (cfg == null || !cfg.ContainsKey("Threshold")) return 0.70f;
            try { return Convert.ToSingle(cfg["Threshold"]); } catch { return 0.70f; }
        }

        private float GetLagswitchPatternThreshold()
        {
            var cfg = Config["LagswitchDetection"] as Dictionary<string, object>;
            if (cfg == null || !cfg.ContainsKey("PatternThreshold")) return 0.75f;
            try { return Convert.ToSingle(cfg["PatternThreshold"]); } catch { return 0.75f; }
        }

        private int GetLagswitchMinIncidentsForPattern()
        {
            var cfg = Config["LagswitchDetection"] as Dictionary<string, object>;
            if (cfg == null || !cfg.ContainsKey("MinIncidentsForPattern")) return 3;
            try { return Convert.ToInt32(cfg["MinIncidentsForPattern"]); } catch { return 3; }
        }

        private float GetLagswitchPingSpikeMinMs()
        {
            var cfg = Config["LagswitchDetection"] as Dictionary<string, object>;
            if (cfg == null || !cfg.ContainsKey("PingSpikeMinimumMs")) return 50f;
            try { return Convert.ToSingle(cfg["PingSpikeMinimumMs"]); } catch { return 50f; }
        }

        private float GetLagswitchPreKillWindowSec()
        {
            var cfg = Config["LagswitchDetection"] as Dictionary<string, object>;
            if (cfg == null || !cfg.ContainsKey("PreKillWindowMs")) return 1f;
            try { return Convert.ToSingle(cfg["PreKillWindowMs"]) / 1000f; } catch { return 1f; }
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

        private string NormalizeBoolText(bool value)
        {
            return value ? "on" : "off";
        }

        private bool IsDebugEnabled()
        {
            if (Config["DebugMode"] == null) return false;
            try { return Convert.ToBoolean(Config["DebugMode"]); } catch { return false; }
        }

        private void DebugLog(string message)
        {
            if (!IsDebugEnabled()) return;

            string line = $"{DateTime.UtcNow:O} [DEBUG] {message}";
            Puts(line);
            try
            {
                File.AppendAllText(_debugLogPath, line + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Puts("[DEBUG] Failed to write debug log file: " + ex.Message);
            }
        }

        private bool TryParseDebugModeArg(string value, out bool enabled)
        {
            enabled = false;
            if (string.IsNullOrWhiteSpace(value)) return false;

            string normalized = value.Trim().ToLowerInvariant();
            if (normalized == "on" || normalized == "1" || normalized == "true") { enabled = true; return true; }
            if (normalized == "off" || normalized == "0" || normalized == "false") { enabled = false; return true; }
            return false;
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
            return GetSupportedLanguageCodes().Contains(NormalizeLanguageCode(code));
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
            try { return Convert.ToBoolean(config["Enabled"]); } catch { return true; }
        }

        private bool ShouldEmitSuspicionEvents()
        {
            var config = GetPublicApiConfig();
            if (!config.ContainsKey("EmitSuspicionEvents")) return true;
            try { return Convert.ToBoolean(config["EmitSuspicionEvents"]); } catch { return true; }
        }

        private bool ShouldEmitPenaltyEvents()
        {
            var config = GetPublicApiConfig();
            if (!config.ContainsKey("EmitPenaltyEvents")) return true;
            try { return Convert.ToBoolean(config["EmitPenaltyEvents"]); } catch { return true; }
        }

        private string GetConfiguredApiVersion()
        {
            var config = GetPublicApiConfig();
            if (!config.ContainsKey("ApiVersion")) return PublicApiVersionCurrent;
            var value = config["ApiVersion"] != null ? config["ApiVersion"].ToString().Trim() : string.Empty;
            return string.IsNullOrWhiteSpace(value) ? PublicApiVersionCurrent : value;
        }

        private bool EnsureWebhookConfigDefaults()
        {
            bool changed = false;
            var webhook = Config["Webhook"] as Dictionary<string, object>;
            if (webhook == null) { webhook = new Dictionary<string, object>(); Config["Webhook"] = webhook; changed = true; }

            if (!webhook.ContainsKey("Enabled")) { webhook["Enabled"] = false; changed = true; }
            if (!webhook.ContainsKey("Endpoint")) { webhook["Endpoint"] = string.Empty; changed = true; }
            if (!webhook.ContainsKey("AuthToken")) { webhook["AuthToken"] = string.Empty; changed = true; }
            if (!webhook.ContainsKey("AuthHeader")) { webhook["AuthHeader"] = "Authorization"; changed = true; }
            if (!webhook.ContainsKey("MaxRetries")) { webhook["MaxRetries"] = 3; changed = true; }
            if (!webhook.ContainsKey("BaseBackoffSeconds")) { webhook["BaseBackoffSeconds"] = 1.5; changed = true; }
            if (!webhook.ContainsKey("MaxBackoffSeconds")) { webhook["MaxBackoffSeconds"] = 20.0; changed = true; }
            if (!webhook.ContainsKey("RateLimitPerSecond")) { webhook["RateLimitPerSecond"] = 2; changed = true; }
            if (!webhook.ContainsKey("QueueMaxSize")) { webhook["QueueMaxSize"] = 500; changed = true; }
            if (!webhook.ContainsKey("EmitSuspicionEvents")) { webhook["EmitSuspicionEvents"] = true; changed = true; }
            if (!webhook.ContainsKey("EmitPenaltyEvents")) { webhook["EmitPenaltyEvents"] = true; changed = true; }

            return changed;
        }

        private Dictionary<string, object> GetWebhookConfig()
        {
            if (EnsureWebhookConfigDefaults()) SaveConfig();
            return Config["Webhook"] as Dictionary<string, object>;
        }

        private bool IsWebhookEnabled()
        {
            var cfg = GetWebhookConfig();
            try { return cfg != null && cfg.ContainsKey("Enabled") && Convert.ToBoolean(cfg["Enabled"]); } catch { return false; }
        }

        private bool ShouldEmitWebhookSuspicionEvents()
        {
            var cfg = GetWebhookConfig();
            if (cfg == null || !cfg.ContainsKey("EmitSuspicionEvents")) return true;
            try { return Convert.ToBoolean(cfg["EmitSuspicionEvents"]); } catch { return true; }
        }

        private bool ShouldEmitWebhookPenaltyEvents()
        {
            var cfg = GetWebhookConfig();
            if (cfg == null || !cfg.ContainsKey("EmitPenaltyEvents")) return true;
            try { return Convert.ToBoolean(cfg["EmitPenaltyEvents"]); } catch { return true; }
        }

        private string GetWebhookEndpoint()
        {
            var cfg = GetWebhookConfig();
            if (cfg == null || !cfg.ContainsKey("Endpoint") || cfg["Endpoint"] == null) return string.Empty;
            return cfg["Endpoint"].ToString().Trim();
        }

        private string GetWebhookAuthToken()
        {
            var cfg = GetWebhookConfig();
            if (cfg == null || !cfg.ContainsKey("AuthToken") || cfg["AuthToken"] == null) return string.Empty;
            return cfg["AuthToken"].ToString().Trim();
        }

        private string GetWebhookAuthHeader()
        {
            var cfg = GetWebhookConfig();
            if (cfg == null || !cfg.ContainsKey("AuthHeader") || cfg["AuthHeader"] == null) return "Authorization";
            var header = cfg["AuthHeader"].ToString().Trim();
            return string.IsNullOrWhiteSpace(header) ? "Authorization" : header;
        }

        private int GetWebhookMaxRetries()
        {
            var cfg = GetWebhookConfig();
            if (cfg == null || !cfg.ContainsKey("MaxRetries")) return 3;
            try { return Mathf.Clamp(Convert.ToInt32(cfg["MaxRetries"]), 0, 10); } catch { return 3; }
        }

        private float GetWebhookBaseBackoffSeconds()
        {
            var cfg = GetWebhookConfig();
            if (cfg == null || !cfg.ContainsKey("BaseBackoffSeconds")) return 1.5f;
            try { return Mathf.Clamp(Convert.ToSingle(cfg["BaseBackoffSeconds"]), 0.25f, 60f); } catch { return 1.5f; }
        }

        private float GetWebhookMaxBackoffSeconds()
        {
            var cfg = GetWebhookConfig();
            if (cfg == null || !cfg.ContainsKey("MaxBackoffSeconds")) return 20f;
            try { return Mathf.Clamp(Convert.ToSingle(cfg["MaxBackoffSeconds"]), 1f, 300f); } catch { return 20f; }
        }

        private int GetWebhookRateLimitPerSecond()
        {
            var cfg = GetWebhookConfig();
            if (cfg == null || !cfg.ContainsKey("RateLimitPerSecond")) return 2;
            try { return Mathf.Clamp(Convert.ToInt32(cfg["RateLimitPerSecond"]), 1, 100); } catch { return 2; }
        }

        private int GetWebhookQueueMaxSize()
        {
            var cfg = GetWebhookConfig();
            if (cfg == null || !cfg.ContainsKey("QueueMaxSize")) return 500;
            try { return Mathf.Clamp(Convert.ToInt32(cfg["QueueMaxSize"]), 10, 5000); } catch { return 500; }
        }

        private bool CanSendWebhookNow(out float delaySeconds)
        {
            delaySeconds = 0f;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now - _webhookWindowStart >= WebhookRateWindowSeconds)
            {
                _webhookWindowStart = now;
                _webhookSentInWindow = 0;
            }

            if (_webhookSentInWindow < GetWebhookRateLimitPerSecond()) return true;

            delaySeconds = WebhookRateWindowSeconds - (now - _webhookWindowStart);
            if (delaySeconds < 0f) delaySeconds = 0f;
            return false;
        }

        private void EnqueueWebhookEvent(string eventName, Dictionary<string, object> payload)
        {
            if (!IsWebhookEnabled()) return;
            if (eventName == "suspicion" && !ShouldEmitWebhookSuspicionEvents()) return;
            if (eventName == "penalty_applied" && !ShouldEmitWebhookPenaltyEvents()) return;

            string endpoint = GetWebhookEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint)) { DebugLog($"Webhook skipped: missing endpoint for event '{eventName}'."); return; }

            var envelopePayload = new Dictionary<string, object>(payload) { ["eventType"] = eventName };

            _webhookQueue.Enqueue(new WebhookEnvelope { EventName = eventName, Payload = envelopePayload, Attempt = 0 });

            int maxSize = GetWebhookQueueMaxSize();
            while (_webhookQueue.Count > maxSize) _webhookQueue.Dequeue();

            PumpWebhookQueue();
        }

        private void PumpWebhookQueue()
        {
            if (_webhookRequestInFlight) return;
            if (_webhookQueue.Count == 0) return;
            if (!IsWebhookEnabled()) return;

            float waitDelay;
            if (!CanSendWebhookNow(out waitDelay)) { timer.Once(waitDelay + 0.01f, PumpWebhookQueue); return; }

            var next = _webhookQueue.Dequeue();
            SendWebhook(next);
        }

        private void SendWebhook(WebhookEnvelope envelope)
        {
            string endpoint = GetWebhookEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint)) return;

            string payloadJson;
            try { payloadJson = BuildWebhookRequestJson(envelope, endpoint); }
            catch (Exception ex) { DebugLog($"Webhook serialization failed for {envelope.EventName}: {ex.Message}"); return; }

            var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
            string token = GetWebhookAuthToken();
            if (!string.IsNullOrWhiteSpace(token)) headers[GetWebhookAuthHeader()] = token;

            _webhookRequestInFlight = true;
            _webhookSentInWindow++;
            try
            {
                webrequest.Enqueue(endpoint, payloadJson, (code, response) =>
                {
                    _webhookRequestInFlight = false;
                    if (code >= 200 && code < 300) { DebugLog($"Webhook sent: event={envelope.EventName}, code={code}"); PumpWebhookQueue(); return; }
                    HandleWebhookFailure(envelope, code, response);
                    PumpWebhookQueue();
                }, this, RequestMethod.POST, headers);
            }
            catch (Exception ex)
            {
                _webhookRequestInFlight = false;
                DebugLog($"Webhook request enqueue failed for {envelope.EventName}: {ex.Message}");
                HandleWebhookFailure(envelope, -1, ex.Message);
            }
        }

        private void HandleWebhookFailure(WebhookEnvelope envelope, int code, string response)
        {
            int maxRetries = GetWebhookMaxRetries();
            if (envelope.Attempt >= maxRetries) { DebugLog($"Webhook dropped after retries: event={envelope.EventName}, code={code}"); return; }

            float baseDelay = GetWebhookBaseBackoffSeconds();
            float maxDelay = Mathf.Max(baseDelay, GetWebhookMaxBackoffSeconds());
            float delay = Mathf.Min(maxDelay, baseDelay * Mathf.Pow(2f, envelope.Attempt));

            envelope.Attempt++;
            DebugLog($"Webhook retry scheduled: event={envelope.EventName}, attempt={envelope.Attempt}, delay={delay:F2}s");

            timer.Once(delay, () => { _webhookQueue.Enqueue(envelope); PumpWebhookQueue(); });
        }

        private string BuildWebhookRequestJson(WebhookEnvelope envelope, string endpoint)
        {
            if (IsDiscordWebhookEndpoint(endpoint)) return BuildDiscordWebhookJson(envelope);
            return JsonConvert.SerializeObject(envelope.Payload);
        }

        private bool IsDiscordWebhookEndpoint(string endpoint)
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return false;
            return endpoint.IndexOf("discord.com/api/webhooks", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string BuildDiscordWebhookJson(WebhookEnvelope envelope)
        {
            string content;
            if (string.Equals(envelope.EventName, "suspicion", StringComparison.OrdinalIgnoreCase))
            {
                content = string.Format(
                    "[MogyAC] Suspicion | player={0} weapon={1} acc={2:P1} nerf={3:P0} samples={4}",
                    GetPayloadValue(envelope.Payload, "playerId"),
                    GetPayloadValue(envelope.Payload, "weaponShortName"),
                    GetPayloadFloat(envelope.Payload, "accuracy"),
                    GetPayloadFloat(envelope.Payload, "suggestedNerf"),
                    GetPayloadValue(envelope.Payload, "sampleCount"));
            }
            else if (string.Equals(envelope.EventName, "penalty_applied", StringComparison.OrdinalIgnoreCase))
            {
                content = string.Format(
                    "[MogyAC] Penalty | attacker={0} target={1} weapon={2} mult={3:F2} dmg={4:F1}->{5:F1}",
                    GetPayloadValue(envelope.Payload, "attackerId"),
                    GetPayloadValue(envelope.Payload, "targetId"),
                    GetPayloadValue(envelope.Payload, "weaponShortName"),
                    GetPayloadFloat(envelope.Payload, "appliedMultiplier"),
                    GetPayloadFloat(envelope.Payload, "originalDamage"),
                    GetPayloadFloat(envelope.Payload, "scaledDamage"));
            }
            else
            {
                content = "[MogyAC] Event: " + envelope.EventName;
            }

            return JsonConvert.SerializeObject(new Dictionary<string, object> { ["username"] = "MogyAntiCheat", ["content"] = content });
        }

        private object GetPayloadValue(Dictionary<string, object> payload, string key)
        {
            if (payload == null || !payload.ContainsKey(key) || payload[key] == null) return "n/a";
            return payload[key];
        }

        private float GetPayloadFloat(Dictionary<string, object> payload, string key)
        {
            if (payload == null || !payload.ContainsKey(key) || payload[key] == null) return 0f;
            try { return Convert.ToSingle(payload[key]); } catch { return 0f; }
        }

        private string GetMessageFromPack(Dictionary<string, string> pack, string key)
        {
            string value;
            return pack != null && pack.TryGetValue(key, out value) ? value : null;
        }

        private string GetMessageForLanguage(string languageCode, string key)
        {
            string normalized = NormalizeLanguageCode(languageCode);
            if (string.IsNullOrEmpty(normalized)) return null;

            try
            {
                var messages = lang.GetMessages(normalized, this);
                string fromLangApi;
                if (messages != null && messages.TryGetValue(key, out fromLangApi) && !string.IsNullOrEmpty(fromLangApi))
                    return fromLangApi;
            }
            catch { }

            if (normalized == "hu") return GetMessageFromPack(MessagesHu, key);
            if (normalized == "en") return GetMessageFromPack(MessagesEn, key);
            return null;
        }

        private string GetConfiguredFallbackMessage(string key)
        {
            string cfgLang = GetConfiguredDefaultLanguage();
            string configured = GetMessageForLanguage(cfgLang, key);
            if (!string.IsNullOrEmpty(configured)) return configured;
            if (cfgLang == "hu") return GetMessageForLanguage("en", key);
            return GetMessageForLanguage("hu", key);
        }

        private string Msg(BasePlayer player, string key, params object[] args)
        {
            string message = GetConfiguredFallbackMessage(key);
            if (string.IsNullOrEmpty(message)) message = "[MogyAC] Missing lang key: " + key;
            if (args == null || args.Length == 0) return message;
            try { return string.Format(message, args); } catch { return message; }
        }

        private int GetPlayerPing(BasePlayer player)
        {
            try { return player?.net?.connection?.averagePing ?? 0; } catch { return 0; }
        }

        private PlayerPingStats GetOrCreatePingStats(ulong playerId)
        {
            PlayerPingStats ps;
            if (!_playerPingStats.TryGetValue(playerId, out ps))
            {
                ps = new PlayerPingStats();
                _playerPingStats[playerId] = ps;
            }
            return ps;
        }

        private PlayerKDAStats GetOrCreateKDA(ulong playerId)
        {
            PlayerKDAStats kda;
            if (!_playerKDAStats.TryGetValue(playerId, out kda))
            {
                kda = new PlayerKDAStats();
                _playerKDAStats[playerId] = kda;
            }
            return kda;
        }

        private void EnqueueTelemetry(ShotTelemetryEvent ev)
        {
            if (!IsEventLoggingEnabled()) return;
            _telemetryQueue.Add(ev);
            if (_telemetryQueue.Count >= TelemetryQueueMaxSize) FlushTelemetryQueue();
        }

        private void FlushTelemetryQueue()
        {
            if (_telemetryQueue.Count == 0) return;

            var batch = _telemetryQueue.ToList();
            _telemetryQueue.Clear();

            try
            {
                string logFile = Path.Combine(_runtimeDataDirectory, $"MogyAntiCheat_Events_{DateTime.UtcNow:yyyyMMdd}.log");
                var batchPayload = new Dictionary<string, object>
                {
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["count"] = batch.Count,
                    ["events"] = batch
                };
                File.AppendAllText(logFile, JsonConvert.SerializeObject(batchPayload) + Environment.NewLine);
                DebugLog($"Telemetry flushed: {batch.Count} events");
            }
            catch (Exception ex)
            {
                DebugLog($"Telemetry flush failed: {ex.Message}");
            }
        }

        void OnWeaponFired(BaseProjectile weapon, BasePlayer player)
        {
            if (player == null || weapon == null || player.IsNpc) return;

            string wName = weapon.ShortPrefabName.Replace(".entity", "");

            int ping = GetPlayerPing(player);
            PlayerPingStats pingStats = GetOrCreatePingStats(player.userID);
            int deltaPing = pingStats.SampleCount > 0 ? ping - pingStats.LastPing : 0;

            bool hadBaseline = pingStats.HasBaseline;
            if (IsPingMonitoringEnabled())
            {
                bool wasAnomaly = pingStats.IsAnomalous(ping, GetPingAnomalyThreshold());
                pingStats.Update(ping);
                if (wasAnomaly)
                {
                    pingStats.AnomalyCount++;
                    DebugLog($"Ping anomaly: player={player.displayName} ({player.userID}), ping={ping}ms, baseline={pingStats.EMA:F0}ms ±{pingStats.StdDev:F1}ms");
                }
            }
            else
            {
                pingStats.Update(ping);
            }

            if (!hadBaseline && pingStats.HasBaseline)
                EmitPingBaselineEvent(player.userID, pingStats);

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

            weaponData.AddMiss(0f, ping, deltaPing);

            EnqueueTelemetry(new ShotTelemetryEvent
            {
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PlayerId = player.userID,
                WeaponName = wName,
                Distance = 0f,
                Hit = false,
                PingMs = ping,
                DeltaPingMs = deltaPing,
                AccuracyInWindow = weaponData.GetAccuracy(),
                EventType = "shot"
            });
        }

        void OnEntityTakeDamage(BaseEntity entity, HitInfo info)
        {
            if (info == null || info.InitiatorPlayer == null || entity == null) return;
            if (entity is BuildingBlock) return;

            bool debugMode = IsDebugEnabled();
            BasePlayer targetPlayer = entity as BasePlayer;
            bool isValidRealPlayerTarget = targetPlayer != null && !targetPlayer.IsNpc && targetPlayer.userID.IsSteamId();
            bool isValidDebugTarget = debugMode && (entity is BaseCombatEntity);
            if (!isValidRealPlayerTarget && !isValidDebugTarget) return;

            BasePlayer attacker = info.InitiatorPlayer;
            if (attacker.IsNpc || !attacker.userID.IsSteamId()) return;

            if (isValidRealPlayerTarget && IsKDATrackingEnabled())
            {
                HashSet<ulong> contributors;
                if (!_damageContributors.TryGetValue(targetPlayer.userID, out contributors))
                {
                    contributors = new HashSet<ulong>();
                    _damageContributors[targetPlayer.userID] = contributors;
                }
                contributors.Add(attacker.userID);
            }

            float lastHit;
            if (_lastHitTime.TryGetValue(attacker.userID, out lastHit))
            {
                if (UnityEngine.Time.realtimeSinceStartup - lastHit < 0.05f) return;
            }
            _lastHitTime[attacker.userID] = UnityEngine.Time.realtimeSinceStartup;

            var weapon = attacker.GetActiveItem()?.GetHeldEntity() as BaseProjectile;
            if (weapon == null) return;

            string wName = weapon.ShortPrefabName.Replace(".entity", "");
            float dist = Vector3.Distance(info.HitPositionWorld, info.PointStart);
            float expiry = Config["MissExpirySeconds"] != null ? Convert.ToSingle(Config["MissExpirySeconds"]) : 20f;

            int limit = 40;
            var weaponsCfg = Config["Weapons"] as Dictionary<string, object>;
            string cfgKey = ResolveWeaponConfigKey(weaponsCfg, wName);
            if (cfgKey != null)
            {
                var entry = weaponsCfg[cfgKey] as Dictionary<string, object>;
                if (entry != null && entry.ContainsKey("SampleCount")) limit = Convert.ToInt32(entry["SampleCount"]);
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

            int ping = GetPlayerPing(attacker);
            PlayerPingStats pingStats = GetOrCreatePingStats(attacker.userID);
            int deltaPing = pingStats.SampleCount > 0 ? ping - pingStats.LastPing : 0;

            weaponData.RegisterHit(dist, limit, expiry, ping, deltaPing);

            EnqueueTelemetry(new ShotTelemetryEvent
            {
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PlayerId = attacker.userID,
                WeaponName = wName,
                Distance = dist,
                Hit = true,
                PingMs = ping,
                DeltaPingMs = deltaPing,
                AccuracyInWindow = weaponData.GetAccuracy(),
                EventType = "hit"
            });

            var evaluation = EvaluateWeapon(wName, weaponData);
            ProcessSuspicionTransition(attacker, wName, evaluation);

            float globalNerf = GetLowestNerf(attacker.userID);
            bool shouldApplyNerfToAttacker = !HasBypass(attacker) || debugMode;
            if (debugMode)
            {
                DebugLog($"Damage check: attacker={attacker.displayName} ({attacker.userID}), weapon={wName}, acc={evaluation.Accuracy:P2}, max={evaluation.MaxAccuracy:P2}, ping={ping}ms, globalNerf={globalNerf:P2}");
            }

            if (shouldApplyNerfToAttacker && globalNerf < 1.0f)
            {
                float originalDamage = info.damageTypes.Total();
                info.damageTypes.ScaleAll(globalNerf);
                float scaledDamage = info.damageTypes.Total();
                EmitPenaltyEvent(attacker, targetPlayer, wName, globalNerf, originalDamage, scaledDamage, ping);
            }
            else if (debugMode)
            {
                if (!shouldApplyNerfToAttacker) DebugLog("Nerf skipped: attacker exemption active.");
                else DebugLog("Nerf skipped: global nerf is 100%.");
            }
        }

        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null || player.IsNpc || !player.userID.IsSteamId()) return;
            _lastDisconnectTime[player.userID] = UnityEngine.Time.realtimeSinceStartup;
            int count;
            _connectionDropCount.TryGetValue(player.userID, out count);
            _connectionDropCount[player.userID] = count + 1;
        }

        void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (!IsKDATrackingEnabled()) return;
            if (entity == null) return;

            BasePlayer victim = entity as BasePlayer;
            if (victim == null || victim.IsNpc || !victim.userID.IsSteamId()) return;

            GetOrCreateKDA(victim.userID).Deaths++;

            EnqueueTelemetry(new ShotTelemetryEvent
            {
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PlayerId = victim.userID,
                EventType = "death"
            });

            HashSet<ulong> contributors;
            bool hasContributors = _damageContributors.TryGetValue(victim.userID, out contributors);

            BasePlayer attacker = info?.InitiatorPlayer;
            bool validKill = attacker != null && !attacker.IsNpc && attacker.userID.IsSteamId() && attacker.userID != victim.userID;

            if (!validKill)
            {
                if (hasContributors) _damageContributors.Remove(victim.userID);
                return;
            }

            GetOrCreateKDA(attacker.userID).Kills++;

            if (hasContributors)
            {
                foreach (ulong contributorId in contributors)
                {
                    if (contributorId != attacker.userID && contributorId != victim.userID)
                        GetOrCreateKDA(contributorId).Assists++;
                }
                _damageContributors.Remove(victim.userID);
            }

            string wName = string.Empty;
            var w = attacker.GetActiveItem()?.GetHeldEntity() as BaseProjectile;
            if (w != null) wName = w.ShortPrefabName.Replace(".entity", "");

            float killDist = Vector3.Distance(attacker.transform.position, victim.transform.position);
            int killPing = GetPlayerPing(attacker);
            bool wasHeadshot = info?.isHeadshot ?? false;

            EnqueueTelemetry(new ShotTelemetryEvent
            {
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PlayerId = attacker.userID,
                WeaponName = wName,
                Distance = killDist,
                Hit = true,
                PingMs = killPing,
                EventType = "kill"
            });

            EvaluateLagswitch(attacker, victim, wName, killDist, wasHeadshot, killPing);
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
            string cfgKey = ResolveWeaponConfigKey(weaponsCfg, weaponName);
            if (cfgKey == null)
            {
                evaluation.MaxAccuracy = 1f;
                evaluation.SafeDistance = 1f;
                evaluation.WeightedScore = data.GetWeightedScore(1f);
                return evaluation;
            }

            var cfg = weaponsCfg[cfgKey] as Dictionary<string, object>;
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
            if (attacker == null) return;

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
            DebugLog($"Suspicion entered: player={attacker.displayName} ({attacker.userID}), weapon={weaponName}, accuracy={evaluation.Accuracy:P2}");
            EmitSuspicionEvent(attacker.userID, weaponName, evaluation);
        }

        private void EmitSuspicionEvent(ulong playerId, string weaponName, WeaponEvaluation evaluation)
        {
            PlayerPingStats pingStats;
            _playerPingStats.TryGetValue(playerId, out pingStats);

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
                ["pingBaselineAvg"] = pingStats != null ? pingStats.EMA : 0.0,
                ["pingBaselineStdDev"] = pingStats != null ? pingStats.StdDev : 0.0,
                ["timestampUtc"] = DateTime.UtcNow.ToString("o")
            };

            if (IsPublicApiEnabled() && ShouldEmitSuspicionEvents())
                Interface.CallHook("OnMogyAcSuspicion", payload);

            EnqueueWebhookEvent("suspicion", payload);
        }

        private void EmitPenaltyEvent(BasePlayer attacker, BasePlayer target, string weaponName, float appliedMultiplier, float originalDamage, float scaledDamage, int pingAtEvent = 0)
        {
            if (attacker == null) return;

            PlayerPingStats pingStats;
            _playerPingStats.TryGetValue(attacker.userID, out pingStats);

            var payload = new Dictionary<string, object>
            {
                ["apiVersion"] = GetConfiguredApiVersion(),
                ["attackerId"] = attacker.userID,
                ["targetId"] = target != null ? target.userID : 0UL,
                ["weaponShortName"] = weaponName,
                ["appliedMultiplier"] = appliedMultiplier,
                ["originalDamage"] = originalDamage,
                ["scaledDamage"] = scaledDamage,
                ["pingAtEvent"] = pingAtEvent,
                ["pingBaselineAvg"] = pingStats != null ? pingStats.EMA : 0.0,
                ["pingAnomaly"] = pingStats != null && pingStats.IsAnomalous(pingAtEvent, GetPingAnomalyThreshold()),
                ["timestampUtc"] = DateTime.UtcNow.ToString("o")
            };

            DebugLog($"Penalty applied: attacker={attacker.displayName} ({attacker.userID}), weapon={weaponName}, mult={appliedMultiplier:F2}, dmg={originalDamage:F1}->{scaledDamage:F1}, ping={pingAtEvent}ms");

            if (IsPublicApiEnabled() && ShouldEmitPenaltyEvents())
                Interface.CallHook("OnMogyAcPenaltyApplied", payload);

            EnqueueWebhookEvent("penalty_applied", payload);
        }

        private void EmitPingBaselineEvent(ulong playerId, PlayerPingStats ps)
        {
            if (!IsPublicApiEnabled()) return;
            var payload = new Dictionary<string, object>
            {
                ["apiVersion"] = GetConfiguredApiVersion(),
                ["playerId"] = playerId,
                ["avg"] = ps.EMA,
                ["min"] = ps.Min == int.MaxValue ? 0 : ps.Min,
                ["max"] = ps.Max,
                ["stddev"] = ps.StdDev,
                ["sampleCount"] = ps.SampleCount,
                ["timestampUtc"] = DateTime.UtcNow.ToString("o")
            };
            Interface.CallHook("OnMogyAcPingBaselineUpdate", payload);
        }

        private void EvaluateLagswitch(BasePlayer attacker, BasePlayer victim, string wName, float distance, bool wasHeadshot, int pingAtKill)
        {
            if (!IsLagswitchDetectionEnabled()) return;
            if (attacker == null || victim == null) return;

            PlayerPingStats pingStats;
            if (!_playerPingStats.TryGetValue(attacker.userID, out pingStats) || !pingStats.HasBaseline) return;

            double baseline = pingStats.EMA;
            int spike = pingAtKill - (int)baseline;
            float spikeMin = GetLagswitchPingSpikeMinMs();
            float pingSpikeScore;
            if (spike < spikeMin) pingSpikeScore = 0f;
            else if (spike > 150) pingSpikeScore = 1f;
            else pingSpikeScore = (float)((spike - spikeMin) / (150.0 - spikeMin));

            float killAccuracy = 0f;
            Dictionary<string, WeaponData> byWeapon;
            if (_playerStats.TryGetValue(attacker.userID, out byWeapon))
            {
                WeaponData wd;
                if (byWeapon.TryGetValue(wName, out wd)) killAccuracy = wd.GetAccuracy();
            }
            float killQualityScore = Mathf.Clamp01(killAccuracy);
            if (wasHeadshot) killQualityScore = Mathf.Min(killQualityScore * 1.05f, 1f);

            float reconnectScore = 0f;
            float lastDisc;
            if (_lastDisconnectTime.TryGetValue(attacker.userID, out lastDisc))
            {
                float elapsed = UnityEngine.Time.realtimeSinceStartup - lastDisc;
                float window = GetLagswitchPreKillWindowSec();
                if (elapsed <= window) reconnectScore = 1f - (elapsed / window);
            }

            float confidence = (pingSpikeScore * 0.40f) + (killQualityScore * 0.35f) + (reconnectScore * 0.25f);
            if (confidence < GetLagswitchThreshold()) return;

            var incident = new LagSwitchIncident
            {
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                VictimId = victim.userID,
                WeaponName = wName,
                Distance = distance,
                KillAccuracy = killAccuracy,
                WasHeadshot = wasHeadshot,
                PingAtKill = pingAtKill,
                PingBaselineAvg = baseline,
                PingBaselineStdDev = pingStats.StdDev,
                PingSpike = spike,
                PingSpikeScore = pingSpikeScore,
                KillQualityScore = killQualityScore,
                ReconnectScore = reconnectScore,
                Confidence = confidence
            };

            List<LagSwitchIncident> incidents;
            if (!_lagswitchIncidents.TryGetValue(attacker.userID, out incidents))
            {
                incidents = new List<LagSwitchIncident>();
                _lagswitchIncidents[attacker.userID] = incidents;
            }
            incidents.Add(incident);

            DebugLog($"Lagswitch detected: player={attacker.displayName} ({attacker.userID}), confidence={confidence:F2}, pingSpike={spike}ms, acc={killAccuracy:P1}");
            EmitLagswitchEvent(attacker, victim, wName, incident);
        }

        private void EmitLagswitchEvent(BasePlayer attacker, BasePlayer victim, string weaponName, LagSwitchIncident incident)
        {
            var payload = new Dictionary<string, object>
            {
                ["apiVersion"] = GetConfiguredApiVersion(),
                ["playerId"] = attacker.userID,
                ["victimId"] = victim.userID,
                ["weaponShortName"] = weaponName,
                ["confidence"] = incident.Confidence,
                ["pingAtKill"] = incident.PingAtKill,
                ["pingBaselineAvg"] = incident.PingBaselineAvg,
                ["pingSpike"] = incident.PingSpike,
                ["killAccuracy"] = incident.KillAccuracy,
                ["wasHeadshot"] = incident.WasHeadshot,
                ["distance"] = incident.Distance,
                ["reconnectScore"] = incident.ReconnectScore,
                ["timestampUtc"] = DateTime.UtcNow.ToString("o")
            };

            if (IsPublicApiEnabled())
                Interface.CallHook("OnMogyAcLagswitchDetected", payload);

            EnqueueWebhookEvent("lagswitch_detected", payload);
        }

        private string ResolveWeaponNameFromArgument(BasePlayer player, string weaponArg)
        {
            if (!string.Equals(weaponArg, "active", StringComparison.OrdinalIgnoreCase))
                return weaponArg.Trim();

            var activeWeapon = player.GetActiveItem()?.GetHeldEntity() as BaseProjectile;
            return activeWeapon == null ? string.Empty : activeWeapon.ShortPrefabName.Replace(".entity", "");
        }

        // Carbon omits the category prefix from ShortPrefabName (e.g. "m39" instead of Oxide's "rifle.m39").
        // Try exact match first, then fall back to matching the segment after the last dot.
        private string ResolveWeaponConfigKey(Dictionary<string, object> weaponsCfg, string wName)
        {
            if (weaponsCfg == null || string.IsNullOrEmpty(wName)) return null;
            if (weaponsCfg.ContainsKey(wName)) return wName;
            foreach (var key in weaponsCfg.Keys)
            {
                int dot = key.LastIndexOf('.');
                if (dot >= 0 && string.Equals(key.Substring(dot + 1), wName, StringComparison.OrdinalIgnoreCase))
                    return key;
            }
            return null;
        }

        private bool TrySetWeaponConfigValue(string weaponName, string fieldArg, string valueArg, out string canonicalField, out string normalizedValue)
        {
            canonicalField = null;
            normalizedValue = null;

            if (string.IsNullOrWhiteSpace(weaponName) || string.IsNullOrWhiteSpace(fieldArg) || string.IsNullOrWhiteSpace(valueArg))
                return false;

            string field = fieldArg.Trim().ToLowerInvariant();
            var weaponsCfg = Config["Weapons"] as Dictionary<string, object>;
            if (weaponsCfg == null) { weaponsCfg = new Dictionary<string, object>(); Config["Weapons"] = weaponsCfg; }

            Dictionary<string, object> weaponCfg;
            string existingKey = ResolveWeaponConfigKey(weaponsCfg, weaponName);
            if (existingKey != null)
            {
                weaponCfg = weaponsCfg[existingKey] as Dictionary<string, object>;
                if (weaponCfg == null) { weaponCfg = new Dictionary<string, object>(); weaponsCfg[existingKey] = weaponCfg; }
            }
            else
            {
                weaponCfg = new Dictionary<string, object>();
                weaponsCfg[weaponName] = weaponCfg;
            }

            if (field == "maxaccuracy")
            {
                float parsed;
                if (!TryParseFloatValue(valueArg, out parsed)) return false;
                if (parsed < 0f || parsed > 1f) return false;
                weaponCfg["MaxAccuracy"] = Math.Round(parsed, 3);
                canonicalField = "MaxAccuracy";
                normalizedValue = parsed.ToString("0.###");
                EnsureWeaponConfigEntryDefaults(weaponCfg);
                return true;
            }

            if (field == "samplecount")
            {
                int parsed;
                if (!int.TryParse(valueArg, out parsed)) return false;
                if (parsed < 1) return false;
                weaponCfg["SampleCount"] = parsed;
                canonicalField = "SampleCount";
                normalizedValue = parsed.ToString();
                EnsureWeaponConfigEntryDefaults(weaponCfg);
                return true;
            }

            if (field == "safedistance")
            {
                float parsed;
                if (!TryParseFloatValue(valueArg, out parsed)) return false;
                if (parsed <= 0f) return false;
                weaponCfg["SafeDistance"] = Math.Round(parsed, 2);
                canonicalField = "SafeDistance";
                normalizedValue = parsed.ToString("0.##");
                EnsureWeaponConfigEntryDefaults(weaponCfg);
                return true;
            }

            return false;
        }

        private bool TryParseFloatValue(string raw, out float value)
        {
            if (float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
            return float.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        }

        private void EnsureWeaponConfigEntryDefaults(Dictionary<string, object> weaponCfg)
        {
            if (!weaponCfg.ContainsKey("MaxAccuracy")) weaponCfg["MaxAccuracy"] = 0.40;
            if (!weaponCfg.ContainsKey("SampleCount")) weaponCfg["SampleCount"] = 40;
            if (!weaponCfg.ContainsKey("SafeDistance")) weaponCfg["SafeDistance"] = 25.0;
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

        object GetApiVersion() => GetConfiguredApiVersion();

        object GetPlayerAcState(ulong playerId)
        {
            Dictionary<string, WeaponData> byWeapon;
            if (!_playerStats.TryGetValue(playerId, out byWeapon)) return null;

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

            PlayerPingStats pingStats;
            _playerPingStats.TryGetValue(playerId, out pingStats);

            PlayerKDAStats kda;
            _playerKDAStats.TryGetValue(playerId, out kda);

            return new Dictionary<string, object>
            {
                ["apiVersion"] = GetConfiguredApiVersion(),
                ["playerId"] = playerId,
                ["globalNerf"] = GetLowestNerf(playerId),
                ["weapons"] = weapons,
                ["pingAvg"] = pingStats != null ? pingStats.EMA : 0.0,
                ["pingStdDev"] = pingStats != null ? pingStats.StdDev : 0.0,
                ["pingAnomalyCount"] = pingStats != null ? pingStats.AnomalyCount : 0,
                ["kills"] = kda != null ? kda.Kills : 0,
                ["deaths"] = kda != null ? kda.Deaths : 0,
                ["assists"] = kda != null ? kda.Assists : 0,
                ["timestampUtc"] = DateTime.UtcNow.ToString("o")
            };
        }

        object GetPlayerPingStats(ulong playerId)
        {
            PlayerPingStats ps;
            if (!_playerPingStats.TryGetValue(playerId, out ps) || ps.SampleCount == 0) return null;

            return new Dictionary<string, object>
            {
                ["avg"] = ps.EMA,
                ["min"] = ps.Min == int.MaxValue ? 0 : ps.Min,
                ["max"] = ps.Max,
                ["stddev"] = ps.StdDev,
                ["sampleCount"] = ps.SampleCount,
                ["hasBaseline"] = ps.HasBaseline,
                ["anomalyCount"] = ps.AnomalyCount
            };
        }

        object GetPlayerKDAStats(ulong playerId)
        {
            PlayerKDAStats kda;
            if (!_playerKDAStats.TryGetValue(playerId, out kda)) return null;

            return new Dictionary<string, object>
            {
                ["kills"] = kda.Kills,
                ["deaths"] = kda.Deaths,
                ["assists"] = kda.Assists,
                ["kdaRatio"] = kda.KDRatio
            };
        }

        object GetLagswitchStats(ulong playerId)
        {
            List<LagSwitchIncident> incidents;
            if (!_lagswitchIncidents.TryGetValue(playerId, out incidents) || incidents.Count == 0) return null;

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long ms24h = 24L * 60 * 60 * 1000;
            long ms7d = 7L * 24 * 60 * 60 * 1000;
            int count24h = 0, count7d = 0;
            float sumConf = 0f;
            foreach (var inc in incidents)
            {
                long age = nowMs - inc.TimestampMs;
                if (age <= ms24h) count24h++;
                if (age <= ms7d) count7d++;
                sumConf += inc.Confidence;
            }
            float avgConf = sumConf / incidents.Count;
            bool patternDetected = count24h >= GetLagswitchMinIncidentsForPattern() && avgConf >= GetLagswitchPatternThreshold();

            return new Dictionary<string, object>
            {
                ["incidentCount24h"] = count24h,
                ["incidentCount7d"] = count7d,
                ["incidentCountTotal"] = incidents.Count,
                ["avgConfidence"] = avgConf,
                ["patternDetected"] = patternDetected
            };
        }

        [ChatCommand("ac-check")]
        void CmdChatCheck(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }

            BasePlayer target = args.Length > 0 ? BasePlayer.Find(args[0]) : player;
            if (target == null) { SendReply(player, Msg(player, "PlayerNotFound")); return; }

            float globalDmg = GetLowestNerf(target.userID);
            string globalColor = globalDmg < 0.5f ? "#ff6666" : (globalDmg < 1.0f ? "#ffff55" : "#88ff88");

            string report = $"<color=#55ff55>{Msg(player, "StatsHeader", target.displayName)}</color>\n";
            report += $"<color=#ffffff>{Msg(player, "GlobalDamageLabel")}: </color><color={globalColor}>{globalDmg:P0}</color>\n";
            report += "----------------------------\n";

            Dictionary<string, WeaponData> byWeapon;
            if (_playerStats.TryGetValue(target.userID, out byWeapon))
            {
                foreach (var w in byWeapon)
                    report += $"<color=#ffff55>{Msg(player, "WeaponLine", w.Key, w.Value.GetAccuracy(), w.Value.History.Count)}</color>\n";
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
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }

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
                    foreach (var w in byWeapon) { totalAcc += w.Value.GetAccuracy(); count++; }
                }

                float avgAcc = count > 0 ? (totalAcc / count) : 0f;
                string dmgColor = globalDmg < 1.0f ? "#ff6666" : "#88ff88";
                report += $"{target.displayName} | {avgAcc:P0} | <color={dmgColor}>{globalDmg:P0}</color>\n";
            }

            SendReply(player, report);
        }

        [ChatCommand("ac-stats")]
        void CmdAcStats(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }

            BasePlayer target = args.Length > 0 ? BasePlayer.Find(args[0]) : player;
            if (target == null) { SendReply(player, Msg(player, "PlayerNotFound")); return; }

            ulong targetId = target.userID;

            PlayerKDAStats kda;
            _playerKDAStats.TryGetValue(targetId, out kda);
            int kills = kda != null ? kda.Kills : 0;
            int deaths = kda != null ? kda.Deaths : 0;
            int assists = kda != null ? kda.Assists : 0;
            float kdr = kda != null ? kda.KDRatio : 0f;

            PlayerPingStats pingStats;
            _playerPingStats.TryGetValue(targetId, out pingStats);

            string report = $"<color=#55ff55>{Msg(player, "StatsHeader", target.displayName)}</color>\n";
            report += Msg(player, "StatsKDA", kills, deaths, assists, kdr) + "\n";

            if (pingStats != null && pingStats.SampleCount > 0)
            {
                int minPing = pingStats.Min == int.MaxValue ? 0 : pingStats.Min;
                report += Msg(player, "StatsPing", pingStats.EMA, minPing, pingStats.Max, pingStats.StdDev) + "\n";
                report += Msg(player, "StatsPingAnomaly", pingStats.AnomalyCount) + "\n";
            }
            else
            {
                report += Msg(player, "StatsNoPingData") + "\n";
            }

            Dictionary<string, WeaponData> byWeapon;
            if (_playerStats.TryGetValue(targetId, out byWeapon) && byWeapon.Count > 0)
            {
                foreach (var w in byWeapon)
                    report += $"  {w.Key}: {w.Value.GetAccuracy():P1} ({w.Value.History.Count} shots)\n";
            }

            SendReply(player, report);
        }

        [ChatCommand("ac-reset")]
        void CmdAcReset(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }
            if (args.Length == 0) { SendReply(player, Msg(player, "ResetUsage")); return; }

            BasePlayer target = BasePlayer.Find(args[0]);
            if (target != null && _playerStats.Remove(target.userID))
            {
                _activeSuspicionByWeapon.Remove(target.userID);
                _playerPingStats.Remove(target.userID);
                _playerKDAStats.Remove(target.userID);
                _damageContributors.Remove(target.userID);
                _lagswitchIncidents.Remove(target.userID);
                _lastDisconnectTime.Remove(target.userID);
                _connectionDropCount.Remove(target.userID);
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
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }
            if (args.Length == 0) { SendReply(player, Msg(player, "LangUsage")); return; }

            string requested = NormalizeLanguageCode(args[0]);
            var supported = GetSupportedLanguageCodes();
            string supportedList = string.Join(", ", supported.ToArray());

            if (!IsSupportedLanguage(requested)) { SendReply(player, Msg(player, "LangUnsupported", requested, supportedList)); return; }

            string current = GetConfiguredDefaultLanguage();
            if (current == requested) { SendReply(player, Msg(player, "LangAlreadySet", requested)); return; }

            Config["DefaultLanguage"] = requested;
            SaveConfig();
            SendReply(player, Msg(player, "LangUpdated", requested));
        }

        [ChatCommand("ac-debug")]
        void CmdAcDebug(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }

            bool current = IsDebugEnabled();
            if (args.Length == 0) { SendReply(player, Msg(player, "DebugStatus", NormalizeBoolText(current))); return; }

            bool requested;
            if (!TryParseDebugModeArg(args[0], out requested)) { SendReply(player, Msg(player, "DebugUsage")); return; }
            if (requested == current) { SendReply(player, Msg(player, "DebugAlreadySet", NormalizeBoolText(requested))); return; }

            Config["DebugMode"] = requested;
            SaveConfig();
            SendReply(player, Msg(player, "DebugUpdated", NormalizeBoolText(requested)));
            DebugLog("Debug mode changed via chat command.");
        }

        [ChatCommand("ac-weapon")]
        void CmdAcWeapon(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }
            if (args.Length < 3) { SendReply(player, Msg(player, "WeaponCfgUsage")); return; }

            string weaponName = ResolveWeaponNameFromArgument(player, args[0]);
            if (string.IsNullOrWhiteSpace(weaponName)) { SendReply(player, Msg(player, "WeaponCfgNoActiveWeapon")); return; }

            string canonicalField;
            string normalizedValue;
            if (!TrySetWeaponConfigValue(weaponName, args[1], args[2], out canonicalField, out normalizedValue))
            {
                string loweredField = args[1].Trim().ToLowerInvariant();
                bool knownField = loweredField == "maxaccuracy" || loweredField == "samplecount" || loweredField == "safedistance";
                SendReply(player, knownField ? Msg(player, "WeaponCfgValueInvalid", args[1], args[2]) : Msg(player, "WeaponCfgFieldInvalid", args[1]));
                return;
            }

            SaveConfig();
            SendReply(player, Msg(player, "WeaponCfgUpdated", weaponName, canonicalField, normalizedValue));
            DebugLog($"Weapon config changed by {player.displayName} ({player.userID}): {weaponName}.{canonicalField}={normalizedValue}");
        }

        [ChatCommand("ac-debug-log")]
        void CmdAcDebugLog(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }

            if (args.Length > 0 && string.Equals(args[0], "clear", StringComparison.OrdinalIgnoreCase))
            {
                try { File.WriteAllText(_debugLogPath, string.Empty); SendReply(player, Msg(player, "DebugLogCleared")); }
                catch (Exception ex) { SendReply(player, Msg(player, "DebugLogClearFailed", ex.Message)); }
                return;
            }

            SendReply(player, Msg(player, "DebugLogPath", _debugLogPath));
        }

        [ChatCommand("ac-why")]
        void CmdAcWhy(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }

            string weaponArg = args.Length > 0 ? args[0] : "active";
            string weaponName = ResolveWeaponNameFromArgument(player, weaponArg);
            if (string.IsNullOrWhiteSpace(weaponName)) { SendReply(player, Msg(player, "WhyUsage")); return; }

            Dictionary<string, WeaponData> byWeapon;
            if (!_playerStats.TryGetValue(player.userID, out byWeapon)) { SendReply(player, Msg(player, "NoData")); return; }

            WeaponData weaponData;
            if (!byWeapon.TryGetValue(weaponName, out weaponData)) { SendReply(player, Msg(player, "WhyNoWeaponData", weaponName)); return; }

            var eval = EvaluateWeapon(weaponName, weaponData);
            float globalNerf = GetLowestNerf(player.userID);
            SendReply(player, Msg(player, "WhySummary", weaponName, eval.Accuracy, eval.SampleCount, eval.MaxAccuracy, eval.WeightedScore, eval.SuggestedNerf, globalNerf));

            if (!eval.HasEnoughData) { SendReply(player, Msg(player, "WhyReasonNoData")); return; }
            if (!eval.IsSuspicious) { SendReply(player, Msg(player, "WhyReasonBelowThreshold")); return; }

            var weaponsCfg = Config["Weapons"] as Dictionary<string, object>;
            if (ResolveWeaponConfigKey(weaponsCfg, weaponName) == null)
                SendReply(player, Msg(player, "WhyNoConfig", weaponName));
        }

        [ChatCommand("ac-lagswitch-audit")]
        void CmdAcLagswitchAudit(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }

            BasePlayer target = args.Length > 0 ? BasePlayer.Find(args[0]) : player;
            if (target == null) { SendReply(player, Msg(player, "PlayerNotFound")); return; }

            string report = $"<color=#55ff55>{Msg(player, "LsHeader", target.displayName)}</color>\n";

            List<LagSwitchIncident> incidents;
            if (!_lagswitchIncidents.TryGetValue(target.userID, out incidents) || incidents.Count == 0)
            {
                report += Msg(player, "LsNoIncidents");
                SendReply(player, report);
                return;
            }

            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long ms24h = 24L * 60 * 60 * 1000;
            int count24h = 0;
            float sumConf = 0f;

            var sorted = new List<LagSwitchIncident>(incidents);
            sorted.Sort((a, b) => b.TimestampMs.CompareTo(a.TimestampMs));

            foreach (var inc in sorted)
            {
                long age = nowMs - inc.TimestampMs;
                if (age <= ms24h) count24h++;
                sumConf += inc.Confidence;

                string ts = DateTimeOffset.FromUnixTimeMilliseconds(inc.TimestampMs).ToString("yyyy-MM-dd HH:mm:ss");
                var victimPlayer = BasePlayer.FindByID(inc.VictimId);
                string victimName = victimPlayer != null ? victimPlayer.displayName : inc.VictimId.ToString();

                report += Msg(player, "LsIncident", ts, victimName, inc.WeaponName, inc.Distance, inc.Confidence) + "\n";
                report += Msg(player, "LsIncidentPing", inc.PingAtKill, inc.PingBaselineAvg, inc.PingSpike) + "\n";
                report += Msg(player, "LsIncidentKill", inc.KillAccuracy, inc.WasHeadshot) + "\n";
                if (inc.ReconnectScore > 0f)
                    report += Msg(player, "LsIncidentReconnect", inc.ReconnectScore) + "\n";
            }

            float avgConf = sumConf / incidents.Count;
            report += Msg(player, "LsSummary", incidents.Count, count24h, avgConf) + "\n";

            bool patternDetected = count24h >= GetLagswitchMinIncidentsForPattern() && avgConf >= GetLagswitchPatternThreshold();
            if (patternDetected)
                report += $"<color=#ff4444>{Msg(player, "LsPatternWarning")}</color>";

            SendReply(player, report);
        }

        [ChatCommand("ac-help")]
        void CmdAcHelp(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }

            string report = $"<color=#55ff55>{Msg(player, "HelpHeader")}</color>\n";
            report += Msg(player, "HelpCheck") + "\n";
            report += Msg(player, "HelpList") + "\n";
            report += Msg(player, "HelpReset") + "\n";
            report += Msg(player, "HelpStats") + "\n";
            report += Msg(player, "HelpLang") + "\n";
            report += Msg(player, "HelpDebug") + "\n";
            report += Msg(player, "HelpWeapon") + "\n";
            report += Msg(player, "HelpDebugLog") + "\n";
            report += Msg(player, "HelpWhy") + "\n";
            report += Msg(player, "HelpLagswitch") + "\n";
            report += Msg(player, "HelpHelp");
            SendReply(player, report);
        }
    }
}
