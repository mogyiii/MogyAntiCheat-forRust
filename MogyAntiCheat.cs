using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Core.Libraries;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Mogy AntiCheat", "Mogy", "1.10.0")]
    [Description("Tracks weapon accuracy trends and dynamically reduces suspicious player damage using configurable thresholds and localized admin commands.")]
    public class MogyAntiCheat : RustPlugin
    {
        private const string DefaultLanguageFallback = "en";
        private const string PublicApiVersionCurrent = "1.3.0";
        private const string DebugLogFileName = "MogyAntiCheat_Debug.log";
        private const float WebhookRateWindowSeconds = 1f;
        private const string PermissionAdmin = "mogyanticheat.admin";
        private const string PermissionBypass = "mogyanticheat.bypass";
        private const string RuntimeOxide = "Oxide/uMod";
        private const string RuntimeCarbon = "Carbon";
        private const int TelemetryQueueMaxSize = 5000;
        private const float TelemetryFlushIntervalSeconds = 300f;
        private const int PingBaselineSamples = 50;
        private const int DefaultWeaponSampleCount = 40;
        private const float DefaultMaxHitDistance = 500f;

        // --- Weekly telemetry report (opt-in, see docs/DATA_COLLECTION.md) ---
        // The public source ships with an unresolved sentinel, so source/.cs deployments have NO default
        // webhook and send nothing. The official release DLL is built with build-release.ps1, which
        // replaces the sentinel with the developer's real webhook (kept out of the repo). Any server can
        // still set WeeklyReport.DiscordWebhookUrl in its config; nothing is sent unless Accepted = true.
        private const string DefaultWeeklyReportWebhook = "__WEEKLY_WEBHOOK__";
        private const string SaltDataFileName = "MogyAntiCheat_Salt";
        private const string WeeklyReportDataFileName = "MogyAntiCheat_WeeklyReport";
        private const string DailyReportDataFileName = "MogyAntiCheat_DailyReport";

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
        private readonly Dictionary<ulong, Dictionary<string, MLSuggestionCacheEntry>> _mlSuggestionCache = new Dictionary<ulong, Dictionary<string, MLSuggestionCacheEntry>>();
        private readonly Dictionary<ulong, List<LagSwitchIncident>> _lagswitchIncidents = new Dictionary<ulong, List<LagSwitchIncident>>();
        private readonly Dictionary<ulong, float> _lastDisconnectTime = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, int> _connectionDropCount = new Dictionary<ulong, int>();
        private readonly List<ShotTelemetryEvent> _telemetryQueue = new List<ShotTelemetryEvent>();
        private readonly Dictionary<ulong, float> _manualOverrides = new Dictionary<ulong, float>();
        private readonly List<OverrideAuditEntry> _overrideAuditLog = new List<OverrideAuditEntry>();
        private readonly Queue<WebhookEnvelope> _webhookQueue = new Queue<WebhookEnvelope>();
        private Timer _webhookPumpTimer;
        private Timer _telemetryFlushTimer;
        private Timer _weeklyReportTimer;
        private Timer _dailyReportTimer;
        private DynamicConfigFile _weeklyReportData;
        private DynamicConfigFile _dailyReportData;
        private string _telemetrySalt = string.Empty;
        private bool _webhookRequestInFlight;
        private float _webhookWindowStart;
        private int _webhookSentInWindow;
        private static System.Reflection.PropertyInfo _pingPropertyInfo;
        private static bool _pingPropertyResolved;
        // Weapon lookup happens on every shot, so the resolved settings are cached per prefab name.
        // Cleared whenever weapon config changes (see InvalidateWeaponTuningCache).
        private readonly Dictionary<string, WeaponTuning> _weaponTuningCache = new Dictionary<string, WeaponTuning>();
        private readonly HashSet<string> _reportedUnresolvedWeapons = new HashSet<string>();
        // Short rolling trail of view directions per player, for the pre-shot aim analysis.
        private readonly Dictionary<ulong, List<AimSample>> _aimTrails = new Dictionary<ulong, List<AimSample>>();
        private readonly Dictionary<ulong, Vector3> _lastShotAim = new Dictionary<ulong, Vector3>();
        private Timer _aimSampleTimer;

        private static readonly Dictionary<string, string> MessagesEn = new Dictionary<string, string>
        {
            ["NoPermission"] = "You do not have permission to use this command.",
            ["PlayerNotFound"] = "Player not found.",
            ["NoData"] = "No data.",
            ["WeeklyDisclosure1"] = "[MogyAC] This plugin can send an ANONYMOUS weekly summary to the developer to improve detection configs.",
            ["WeeklyDisclosure2"] = "[MogyAC] SteamIDs are irreversibly hashed; no names or IPs are collected. See docs/DATA_COLLECTION.md.",
            ["WeeklyDisclosureActive"] = "[MogyAC] Weekly report: ENABLED (WeeklyReport.Accepted = true). Set it to false to opt out.",
            ["WeeklyDisclosureInactive"] = "[MogyAC] Weekly report: OFF. Set WeeklyReport.Accepted = true (and a webhook URL) to opt in.",
            ["WeeklyNotAccepted"] = "[MogyAC] Weekly report is not accepted. Set WeeklyReport.Accepted = true first.",
            ["WeeklyNoUrl"] = "[MogyAC] No weekly report webhook URL is configured.",
            ["WeeklySentNow"] = "[MogyAC] Weekly report sent.",
            ["DailyNoUrl"] = "[MogyAC] No daily report webhook URL is configured (DailyReport.DiscordWebhookUrl).",
            ["DailySentNow"] = "[MogyAC] Daily report sent to the configured webhook.",
            ["HelpDailyNow"] = "/ac-daily-now - Send the daily suspicion report to your webhook now.",
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
            ["WhyTuningSource"] = "Thresholds for {0} come from: {1}",
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
            ["HelpLagswitch"] = "/ac-lagswitch-audit [playerName] - Show lagswitch forensic timeline.",
            ["MLFeedbackUsage"] = "Usage: /ac-ml-feedback <playerName> <confirmed_cheater|false_positive|uncertain>",
            ["MLFeedbackSent"] = "[MogyAC] ML feedback sent for {0}: {1}.",
            ["MLFeedbackFailed"] = "[MogyAC] ML feedback send failed: service unavailable or not configured.",
            ["MLServiceDisabled"] = "[MogyAC] ML service is not enabled or configured.",
            ["HelpMLFeedback"] = "/ac-ml-feedback <player> <confirmed_cheater|false_positive|uncertain> - Submit feedback to ML service.",
            ["DashboardHeader"] = "=== MogyAC DASHBOARD ===",
            ["DashboardNoPlayers"] = "No tracked players.",
            ["DashboardRow"] = "{0} | Nerf: {1:P0} | Ping: {2:F0}ms | LS: {3} | K/D/A: {4}/{5}/{6} | Override: {7}",
            ["OverrideUsage"] = "Usage: /ac-override <playerName> <0-100|off>",
            ["OverrideSet"] = "[MogyAC] Override set for {0}: {1}% damage reduction.",
            ["OverrideCleared"] = "[MogyAC] Override cleared for {0}.",
            ["OverrideInvalidValue"] = "Invalid value: '{0}'. Use 0-100 (percent reduction) or 'off'.",
            ["ChartUsage"] = "Usage: /ac-chart <playerName> <accuracy|ping|kda>",
            ["ChartHeader"] = "=== {0} | {1} ===",
            ["ChartNoData"] = "No chart data available for {0}.",
            ["ExportUsage"] = "Usage: /ac-export csv",
            ["ExportDone"] = "[MogyAC] Export written: {0} ({1} rows)",
            ["ExportEmpty"] = "[MogyAC] No data to export.",
            ["ConfigTuneUsage"] = "Usage: /ac-config-tune <MissExpirySeconds|LagswitchDetection.Threshold|PingMonitoring.AnomalyThresholdStdDev> <value>",
            ["ConfigTuneUpdated"] = "[MogyAC] Config updated: {0} = {1} (was: {2}).",
            ["ConfigTuneInvalidParam"] = "Unknown parameter: '{0}'.",
            ["ConfigTuneInvalidValue"] = "Invalid value for {0}: '{1}'.",
            ["SuggestHeader"] = "=== ML Config Recommendations ===",
            ["SuggestNoService"] = "[MogyAC] ML service is not configured or unavailable.",
            ["SuggestRow"] = "  {0}: {1} → {2} (confidence: {3:P0})",
            ["SuggestNoChanges"] = "No config changes recommended.",
            ["SuggestFetching"] = "[MogyAC] Fetching ML recommendations...",
            ["HelpDashboard"] = "/ac-dashboard - Live view of all tracked players.",
            ["HelpOverride"] = "/ac-override <player> <0-100|off> - Set manual damage reduction for a player.",
            ["HelpChart"] = "/ac-chart <player> <accuracy|ping|kda> - ASCII chart of player metric.",
            ["HelpExport"] = "/ac-export csv - Export all player stats to CSV file.",
            ["HelpConfigTune"] = "/ac-config-tune <param> <value> - Adjust a config parameter live.",
            ["HelpSuggest"] = "/ac-suggest - Query ML service for config recommendations."
        };

        private static readonly Dictionary<string, string> MessagesHu = new Dictionary<string, string>
        {
            ["NoPermission"] = "Nincs jogosultságod ehhez a parancshoz.",
            ["PlayerNotFound"] = "Játékos nem található.",
            ["NoData"] = "Nincs adat.",
            ["WeeklyDisclosure1"] = "[MogyAC] Ez a plugin NÉVTELEN heti összesítőt küldhet a fejlesztőnek a detektálási configok javításához.",
            ["WeeklyDisclosure2"] = "[MogyAC] A SteamID-k visszafejthetetlenül hashelve; nevet és IP-t nem gyűjtünk. Lásd: docs/DATA_COLLECTION.md.",
            ["WeeklyDisclosureActive"] = "[MogyAC] Heti riport: BEKAPCSOLVA (WeeklyReport.Accepted = true). Kikapcsolás: állítsd false-ra.",
            ["WeeklyDisclosureInactive"] = "[MogyAC] Heti riport: KI. Bekapcsolás: WeeklyReport.Accepted = true (és egy webhook URL).",
            ["WeeklyNotAccepted"] = "[MogyAC] A heti riport nincs elfogadva. Előbb állítsd: WeeklyReport.Accepted = true.",
            ["WeeklyNoUrl"] = "[MogyAC] Nincs beállítva heti riport webhook URL.",
            ["WeeklySentNow"] = "[MogyAC] Heti riport elküldve.",
            ["DailyNoUrl"] = "[MogyAC] Nincs beállítva napi riport webhook URL (DailyReport.DiscordWebhookUrl).",
            ["DailySentNow"] = "[MogyAC] Napi riport elküldve a beállított webhookra.",
            ["HelpDailyNow"] = "/ac-daily-now - Napi gyanú-riport azonnali küldése a webhookodra.",
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
            ["WhyTuningSource"] = "A {0} küszöbei innen jönnek: {1}",
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
            ["HelpLagswitch"] = "/ac-lagswitch-audit [jatekosnev] - Lagswitch törvényszéki idővonal.",
            ["MLFeedbackUsage"] = "Használat: /ac-ml-feedback <jatekosnev> <confirmed_cheater|false_positive|uncertain>",
            ["MLFeedbackSent"] = "[MogyAC] ML visszajelzés elküldve: {0} → {1}.",
            ["MLFeedbackFailed"] = "[MogyAC] ML visszajelzés sikertelen: a szolgáltatás nem elérhető vagy nincs konfigurálva.",
            ["MLServiceDisabled"] = "[MogyAC] Az ML szolgáltatás nincs engedélyezve vagy konfigurálva.",
            ["HelpMLFeedback"] = "/ac-ml-feedback <jatekos> <confirmed_cheater|false_positive|uncertain> - Visszajelzés küldése az ML szolgáltatásnak.",
            ["DashboardHeader"] = "=== MogyAC IRÁNYÍTÓPULT ===",
            ["DashboardNoPlayers"] = "Nincs követett játékos.",
            ["DashboardRow"] = "{0} | Nerf: {1:P0} | Ping: {2:F0}ms | LS: {3} | K/D/A: {4}/{5}/{6} | Felülbírálat: {7}",
            ["OverrideUsage"] = "Használat: /ac-override <játékosnév> <0-100|off>",
            ["OverrideSet"] = "[MogyAC] Felülbírálat beállítva {0}-nak: {1}% sebzéscsökkentés.",
            ["OverrideCleared"] = "[MogyAC] Felülbírálat törölve: {0}.",
            ["OverrideInvalidValue"] = "Érvénytelen érték: '{0}'. Használj 0-100 számot (%-os csökkentés) vagy 'off'-ot.",
            ["ChartUsage"] = "Használat: /ac-chart <játékosnév> <accuracy|ping|kda>",
            ["ChartHeader"] = "=== {0} | {1} ===",
            ["ChartNoData"] = "Nincs diagram adat: {0}.",
            ["ExportUsage"] = "Használat: /ac-export csv",
            ["ExportDone"] = "[MogyAC] Exportálva: {0} ({1} sor)",
            ["ExportEmpty"] = "[MogyAC] Nincs exportálható adat.",
            ["ConfigTuneUsage"] = "Használat: /ac-config-tune <MissExpirySeconds|LagswitchDetection.Threshold|PingMonitoring.AnomalyThresholdStdDev> <érték>",
            ["ConfigTuneUpdated"] = "[MogyAC] Konfiguráció frissítve: {0} = {1} (volt: {2}).",
            ["ConfigTuneInvalidParam"] = "Ismeretlen paraméter: '{0}'.",
            ["ConfigTuneInvalidValue"] = "Érvénytelen érték ehhez: {0}: '{1}'.",
            ["SuggestHeader"] = "=== ML Konfigurációs Javaslatok ===",
            ["SuggestNoService"] = "[MogyAC] Az ML szolgáltatás nincs konfigurálva vagy nem elérhető.",
            ["SuggestRow"] = "  {0}: {1} → {2} (megbízhatóság: {3:P0})",
            ["SuggestNoChanges"] = "Nincs konfigurációs változtatási javaslat.",
            ["SuggestFetching"] = "[MogyAC] ML javaslatok lekérése...",
            ["HelpDashboard"] = "/ac-dashboard - Élő nézet az összes követett játékosról.",
            ["HelpOverride"] = "/ac-override <jatekos> <0-100|off> - Manuális sebzéscsökkentés beállítása.",
            ["HelpChart"] = "/ac-chart <jatekos> <accuracy|ping|kda> - ASCII diagram egy játékos metrikájáról.",
            ["HelpExport"] = "/ac-export csv - Összes játékos stat exportálása CSV fájlba.",
            ["HelpConfigTune"] = "/ac-config-tune <param> <ertek> - Konfiguráció élő módosítása.",
            ["HelpSuggest"] = "/ac-suggest - ML szerviz konfigurációs javaslatok lekérése."
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
            // Config key, "family:<name>", or "unconfigured" — surfaced by /ac-why and debug logs.
            public string TuningSource;
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
            // Irreversible per-server HMAC of the player's SteamID (never the raw ID).
            // Consistent within a server so behaviour can be attributed, but not linkable to a real person.
            public string PlayerHash;
            public string WeaponName;
            public float Distance;
            public bool Hit;
            public int PingMs;
            public int DeltaPingMs;
            public float AccuracyInWindow;
            public string EventType;
            public string HitArea;      // "head", "chest", "arm", stb. (null lövésnél/missnél)
            public float GameTimeHour;  // 0–24, -1 ha nem elérhető

            // --- Aim kinematics (AimTracking; -1 when unavailable) ---
            // Accuracy alone cannot separate an aimbot from a good player: both just hit a lot.
            // What differs is *how the view arrives on target*. An assisted shot is preceded by a
            // large angular step that stops dead and fires within a few tens of milliseconds; a
            // human decelerates onto the target and the delay varies shot to shot.
            public float AimDeltaDeg;    // angle between this shot's view direction and the previous shot's
            public float SnapDeg;        // largest single angular step in the sampled window before this shot
            public float SnapSettleMs;   // ms between that step and pulling the trigger
        }

        private struct AimSample
        {
            public float Realtime;
            public Vector3 Forward;
        }

        private class MLSuggestionCacheEntry
        {
            public long FetchedAtMs;
            public float Confidence;
            public int SuggestedNerfPct;
            public string AnomalyType;
            public string Reason;

            public bool IsExpired(int cacheSeconds)
                => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - FetchedAtMs > cacheSeconds * 1000L;
        }

        private class OverrideAuditEntry
        {
            public string TimestampUtc;
            public ulong AdminId;
            public string AdminName;
            public ulong TargetId;
            public string TargetName;
            public string OldValue;
            public string NewValue;
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
            _weeklyReportData = Interface.Oxide.DataFileSystem.GetFile(WeeklyReportDataFileName);
            _dailyReportData = Interface.Oxide.DataFileSystem.GetFile(DailyReportDataFileName);
            _debugLogPath = Path.Combine(_runtimeDataDirectory, DebugLogFileName);
            LoadStats();
            LoadKDAStats();
            EnsureConfigDefaults();
            EnsureTelemetrySalt();
            _webhookWindowStart = UnityEngine.Time.realtimeSinceStartup;
            _webhookPumpTimer = timer.Every(0.25f, PumpWebhookQueue);
            _telemetryFlushTimer = timer.Every(TelemetryFlushIntervalSeconds, FlushTelemetryQueue);
            StartAimSampling();
            _weeklyReportTimer = timer.Every(3600f, WeeklyReportTick);
            // Ticks every 15 minutes so a 1-hour IntervalHours setting is actually honoured;
            // the tick itself does nothing until the interval has elapsed.
            _dailyReportTimer = timer.Every(900f, DailyReportTick);

            Puts($"Runtime detected: {_runtimeName} | Data directory: {_runtimeDataDirectory}");
            LogDataCollectionDisclosure();
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
            _weeklyReportTimer?.Destroy();
            _dailyReportTimer?.Destroy();
            _aimSampleTimer?.Destroy();
            _aimTrails.Clear();
            _lastShotAim.Clear();
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

        // Applied to any weapon the Weapons block does not name, by family. The thresholds are
        // intentionally lenient: they are cross-server guesses meant to catch blatant outliers on
        // modded or newly added weapons, not to fine-tune. Run `ml-service/train.py` on the server's
        // own event logs to replace them with measured values (docs/ML_TRAINING.md).
        private static Dictionary<string, object> BuildDefaultWeaponFallbackConfig()
        {
            return new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["Families"] = new Dictionary<string, object>
                {
                    ["auto_rifle"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.85, ["SampleCount"] = 40, ["SafeDistance"] = 45.0 },
                    ["smg"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.95, ["SampleCount"] = 40, ["SafeDistance"] = 15.0 },
                    ["lmg"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.85, ["SampleCount"] = 50, ["SafeDistance"] = 30.0 },
                    ["semi_rifle"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.75, ["SampleCount"] = 30, ["SafeDistance"] = 40.0 },
                    ["sniper"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.93, ["SampleCount"] = 15, ["SafeDistance"] = 60.0 },
                    ["shotgun"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.95, ["SampleCount"] = 15, ["SafeDistance"] = 12.0 },
                    ["pistol"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.88, ["SampleCount"] = 20, ["SafeDistance"] = 15.0 },
                    ["projectile"] = new Dictionary<string, object> { ["MaxAccuracy"] = 0.90, ["SampleCount"] = 12, ["SafeDistance"] = 25.0 },
                    // A rocket or grenade registers a hit on virtually every shot, so hit ratio
                    // carries no signal. 1.0 leaves the family unpenalised by design.
                    ["explosive"] = new Dictionary<string, object> { ["MaxAccuracy"] = 1.0, ["SampleCount"] = 20, ["SafeDistance"] = 25.0 }
                }
            };
        }

        // Sampling the view direction is the only way to tell "aimed there" from "was placed there".
        // 20 Hz resolves a snap (a snap completes well inside 100 ms) without meaningful cost:
        // only players holding a ranged weapon are sampled, and the trail is 400 ms long.
        private static Dictionary<string, object> BuildDefaultAimTrackingConfig()
        {
            return new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["SampleHz"] = 20.0,
                ["WindowMs"] = 400.0
            };
        }

        // The operator's own daily digest, delivered to their own Discord webhook. Distinct from
        // WeeklyReport in every way that matters: that one is an opt-in, anonymized summary sent to
        // the plugin developer, this one is the server owner's own data about their own players, so
        // it defaults to real names — they can already see them in game and in /ac-dashboard.
        private static Dictionary<string, object> BuildDefaultDailyReportConfig()
        {
            return new Dictionary<string, object>
            {
                ["Enabled"] = false,
                ["DiscordWebhookUrl"] = "",
                ["IntervalHours"] = 24,
                ["TopCount"] = 10,
                // Turn off if the webhook lands in a channel more people than the staff can read.
                ["IncludeNames"] = true,
                ["IncludeSteamIds"] = true,
                ["IncludeLagswitch"] = true,
                ["IncludeKDA"] = true,
                // Players below this suspicion score are left out entirely, so a quiet day sends a
                // short "nothing to report" rather than a list of ordinary players.
                ["MinSuspicionScore"] = 0.35
            };
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

            Config["WeaponFallback"] = BuildDefaultWeaponFallbackConfig();
            Config["AimTracking"] = BuildDefaultAimTrackingConfig();
            Config["DailyReport"] = BuildDefaultDailyReportConfig();
            Config["MissExpirySeconds"] = 20.0;
            Config["MaxHitDistance"] = (double)DefaultMaxHitDistance;
            Config["DefaultLanguage"] = DefaultLanguageFallback;
            Config["DebugMode"] = false;
            Config["DamageReductionEnabled"] = true;
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
            Config["MLService"] = new Dictionary<string, object>
            {
                ["Enabled"] = false,
                ["Endpoint"] = "",
                ["AuthToken"] = "",
                ["TimeoutSeconds"] = 5,
                ["CacheSuggestionsSeconds"] = 60,
                ["FallbackToLocalScoring"] = true
            };
            Config["WeeklyReport"] = BuildDefaultWeeklyReportConfig();
            SaveConfig();
        }

        private Dictionary<string, object> BuildDefaultWeeklyReportConfig()
        {
            // Anonymous weekly telemetry summary sent to the plugin developer.
            // Disabled until the server operator sets Accepted = true (see docs/DATA_COLLECTION.md).
            return new Dictionary<string, object>
            {
                ["Enabled"] = true,
                ["Accepted"] = false,
                ["DiscordWebhookUrl"] = ResolveDefaultWebhook(),
                ["IntervalDays"] = 7,
                ["IncludeKDA"] = true,
                ["IncludeLagswitch"] = true
            };
        }

        // Returns the compiled-in default webhook, or empty if it was never resolved (i.e. the public
        // source sentinel is still in place, or the value is not an http(s) URL).
        private static string ResolveDefaultWebhook()
        {
            string w = DefaultWeeklyReportWebhook;
            if (string.IsNullOrWhiteSpace(w) || !w.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            return w;
        }

        private void EnsureConfigDefaults()
        {
            bool changed = false;

            if (Config["DefaultLanguage"] == null) { Config["DefaultLanguage"] = DefaultLanguageFallback; changed = true; }
            if (Config["DebugMode"] == null) { Config["DebugMode"] = false; changed = true; }
            if (Config["DamageReductionEnabled"] == null) { Config["DamageReductionEnabled"] = true; changed = true; }

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

            var mlCfg = Config["MLService"] as Dictionary<string, object>;
            if (mlCfg == null)
            {
                Config["MLService"] = new Dictionary<string, object>
                {
                    ["Enabled"] = false, ["Endpoint"] = "", ["AuthToken"] = "",
                    ["TimeoutSeconds"] = 5, ["CacheSuggestionsSeconds"] = 60, ["FallbackToLocalScoring"] = true
                };
                changed = true;
            }
            else
            {
                if (!mlCfg.ContainsKey("Enabled")) { mlCfg["Enabled"] = false; changed = true; }
                if (!mlCfg.ContainsKey("Endpoint")) { mlCfg["Endpoint"] = ""; changed = true; }
                if (!mlCfg.ContainsKey("AuthToken")) { mlCfg["AuthToken"] = ""; changed = true; }
                if (!mlCfg.ContainsKey("TimeoutSeconds")) { mlCfg["TimeoutSeconds"] = 5; changed = true; }
                if (!mlCfg.ContainsKey("CacheSuggestionsSeconds")) { mlCfg["CacheSuggestionsSeconds"] = 60; changed = true; }
                if (!mlCfg.ContainsKey("FallbackToLocalScoring")) { mlCfg["FallbackToLocalScoring"] = true; changed = true; }
            }

            var weeklyCfg = Config["WeeklyReport"] as Dictionary<string, object>;
            if (weeklyCfg == null)
            {
                Config["WeeklyReport"] = BuildDefaultWeeklyReportConfig();
                changed = true;
            }
            else
            {
                if (!weeklyCfg.ContainsKey("Enabled")) { weeklyCfg["Enabled"] = true; changed = true; }
                if (!weeklyCfg.ContainsKey("Accepted")) { weeklyCfg["Accepted"] = false; changed = true; }
                if (!weeklyCfg.ContainsKey("DiscordWebhookUrl")) { weeklyCfg["DiscordWebhookUrl"] = ResolveDefaultWebhook(); changed = true; }
                if (!weeklyCfg.ContainsKey("IntervalDays")) { weeklyCfg["IntervalDays"] = 7; changed = true; }
                if (!weeklyCfg.ContainsKey("IncludeKDA")) { weeklyCfg["IncludeKDA"] = true; changed = true; }
                if (!weeklyCfg.ContainsKey("IncludeLagswitch")) { weeklyCfg["IncludeLagswitch"] = true; changed = true; }
            }

            if (Config["MaxHitDistance"] == null)
            {
                Config["MaxHitDistance"] = (double)DefaultMaxHitDistance;
                changed = true;
            }

            var fallbackCfg = Config["WeaponFallback"] as Dictionary<string, object>;
            if (fallbackCfg == null)
            {
                Config["WeaponFallback"] = BuildDefaultWeaponFallbackConfig();
                changed = true;
            }
            else
            {
                if (!fallbackCfg.ContainsKey("Enabled")) { fallbackCfg["Enabled"] = true; changed = true; }
                var families = fallbackCfg["Families"] as Dictionary<string, object>;
                if (families == null)
                {
                    fallbackCfg["Families"] = (BuildDefaultWeaponFallbackConfig()["Families"] as Dictionary<string, object>);
                    changed = true;
                }
                else
                {
                    // Add families introduced by newer plugin versions without touching tuned ones.
                    var defaults = BuildDefaultWeaponFallbackConfig()["Families"] as Dictionary<string, object>;
                    foreach (var entry in defaults)
                        if (!families.ContainsKey(entry.Key)) { families[entry.Key] = entry.Value; changed = true; }
                }
            }

            var dailyCfg = Config["DailyReport"] as Dictionary<string, object>;
            if (dailyCfg == null)
            {
                Config["DailyReport"] = BuildDefaultDailyReportConfig();
                changed = true;
            }
            else
            {
                foreach (var entry in BuildDefaultDailyReportConfig())
                    if (!dailyCfg.ContainsKey(entry.Key)) { dailyCfg[entry.Key] = entry.Value; changed = true; }
            }

            var aimCfg = Config["AimTracking"] as Dictionary<string, object>;
            if (aimCfg == null)
            {
                Config["AimTracking"] = BuildDefaultAimTrackingConfig();
                changed = true;
            }
            else
            {
                if (!aimCfg.ContainsKey("Enabled")) { aimCfg["Enabled"] = true; changed = true; }
                if (!aimCfg.ContainsKey("SampleHz")) { aimCfg["SampleHz"] = 20.0; changed = true; }
                if (!aimCfg.ContainsKey("WindowMs")) { aimCfg["WindowMs"] = 400.0; changed = true; }
            }

            if (changed) SaveConfig();
            InvalidateWeaponTuningCache();
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

        private bool IsMLServiceEnabled()
        {
            var cfg = Config["MLService"] as Dictionary<string, object>;
            if (cfg == null || !cfg.ContainsKey("Enabled")) return false;
            try { return Convert.ToBoolean(cfg["Enabled"]); } catch { return false; }
        }

        private string GetMLServiceEndpoint()
        {
            var cfg = Config["MLService"] as Dictionary<string, object>;
            if (cfg == null || !cfg.ContainsKey("Endpoint")) return string.Empty;
            return cfg["Endpoint"] != null ? cfg["Endpoint"].ToString().TrimEnd('/') : string.Empty;
        }

        private string GetMLServiceAuthToken()
        {
            var cfg = Config["MLService"] as Dictionary<string, object>;
            if (cfg == null || !cfg.ContainsKey("AuthToken")) return string.Empty;
            return cfg["AuthToken"] != null ? cfg["AuthToken"].ToString() : string.Empty;
        }

        private int GetMLServiceCacheSuggestionsSeconds()
        {
            var cfg = Config["MLService"] as Dictionary<string, object>;
            if (cfg == null || !cfg.ContainsKey("CacheSuggestionsSeconds")) return 60;
            try { return Convert.ToInt32(cfg["CacheSuggestionsSeconds"]); } catch { return 60; }
        }

        private bool IsFallbackToLocalScoringEnabled()
        {
            var cfg = Config["MLService"] as Dictionary<string, object>;
            if (cfg == null || !cfg.ContainsKey("FallbackToLocalScoring")) return true;
            try { return Convert.ToBoolean(cfg["FallbackToLocalScoring"]); } catch { return true; }
        }

        private bool TryGetCachedMLNerf(ulong userId, string weaponName, out float mlNerf)
        {
            mlNerf = 1.0f;
            Dictionary<string, MLSuggestionCacheEntry> playerCache;
            if (!_mlSuggestionCache.TryGetValue(userId, out playerCache)) return false;
            MLSuggestionCacheEntry entry;
            if (!playerCache.TryGetValue(weaponName, out entry)) return false;
            if (entry.IsExpired(GetMLServiceCacheSuggestionsSeconds())) return false;
            mlNerf = Mathf.Clamp(entry.SuggestedNerfPct / 100f, 0f, 1.0f);
            return true;
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

        private bool IsDamageReductionEnabled()
        {
            if (Config["DamageReductionEnabled"] == null) return true;
            try { return Convert.ToBoolean(Config["DamageReductionEnabled"]); } catch { return true; }
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
            if (player == null) return 0;
            // Oxide/Carbon IPlayer.Ping a legmegbízhatóbb forrás
            try
            {
                int ip = player.IPlayer?.Ping ?? -1;
                if (ip >= 0) return ip;
            }
            catch { }
            // Reflection fallback régebbi build-ekhez
            if (player.net?.connection == null) return 0;
            try
            {
                if (!_pingPropertyResolved)
                {
                    var t = player.net.connection.GetType();
                    _pingPropertyInfo = t.GetProperty("averagePing") ?? t.GetProperty("ping");
                    _pingPropertyResolved = true;
                }
                if (_pingPropertyInfo == null) return 0;
                return Convert.ToInt32(_pingPropertyInfo.GetValue(player.net.connection));
            }
            catch { return 0; }
        }

        private string GetHitAreaName(HitInfo info)
        {
            if (info == null) return "unknown";
            if (info.isHeadshot) return "head";
            try { return info.boneArea.ToString().ToLower(); }
            catch { return "unknown"; }
        }

        private float GetGameTimeHour()
        {
            try { return TOD_Sky.Instance?.Cycle?.Hour ?? -1f; }
            catch { return -1f; }
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
                // JSON Lines: one event object per line, no batch wrapper.
                string logFile = Path.Combine(_runtimeDataDirectory, $"MogyAntiCheat_Events_{DateTime.UtcNow:yyyyMMdd}.log");
                var sb = new StringBuilder();
                foreach (var ev in batch)
                    sb.Append(JsonConvert.SerializeObject(ev)).Append(Environment.NewLine);
                File.AppendAllText(logFile, sb.ToString());
                DebugLog($"Telemetry flushed: {batch.Count} events");
            }
            catch (Exception ex)
            {
                DebugLog($"Telemetry flush failed: {ex.Message}");
            }

            if (IsMLServiceEnabled())
                PostTelemetryToMLService(batch);
        }

        private void PostTelemetryToMLService(List<ShotTelemetryEvent> batch)
        {
            string endpoint = GetMLServiceEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint)) return;

            // Events only — no batch wrapper. Body is a bare JSON array of event objects.
            string url = $"{endpoint}/ingest";
            string body = JsonConvert.SerializeObject(batch);
            var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
            string token = GetMLServiceAuthToken();
            if (!string.IsNullOrWhiteSpace(token)) headers["Authorization"] = $"Bearer {token}";

            try
            {
                webrequest.Enqueue(url, body, (code, response) =>
                {
                    if (code < 200 || code >= 300)
                        DebugLog($"ML service ingest failed: code={code}");
                    else
                        DebugLog($"ML service ingest accepted: code={code}");
                }, this, RequestMethod.POST, headers);
            }
            catch (Exception ex)
            {
                DebugLog($"ML service ingest request error: {ex.Message}");
            }
        }

        // ===================== Telemetry anonymization =====================

        // Loads (or generates once) a random per-server salt used to irreversibly hash SteamIDs.
        // The salt is stored locally in MogyAntiCheat_Salt.json and is NEVER transmitted, which is
        // what makes the outgoing player hashes impossible to reverse — even for the plugin author.
        private void EnsureTelemetrySalt()
        {
            try
            {
                var file = Interface.Oxide.DataFileSystem.GetFile(SaltDataFileName);
                var data = file.ReadObject<Dictionary<string, string>>();
                string salt = null;
                if (data != null) data.TryGetValue("salt", out salt);

                if (string.IsNullOrEmpty(salt))
                {
                    salt = GenerateRandomSalt();
                    file.WriteObject(new Dictionary<string, string> { ["salt"] = salt });
                    DebugLog("Generated a new per-server telemetry salt.");
                }

                _telemetrySalt = salt;
            }
            catch (Exception ex)
            {
                // Fail-safe: if the salt cannot be persisted, use a volatile session salt so we
                // still never emit raw SteamIDs (hashes just won't be stable across restarts).
                _telemetrySalt = GenerateRandomSalt();
                DebugLog($"Telemetry salt load failed, using volatile session salt: {ex.Message}");
            }
        }

        private string GenerateRandomSalt()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        // HMAC-SHA256(SteamID, per-server salt) truncated to 16 bytes (32 hex chars).
        // Stable within a server, irreversible without the local salt.
        private string HashPlayerId(ulong playerId)
        {
            try
            {
                var keyBytes = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(_telemetrySalt) ? "mogyac" : _telemetrySalt);
                using (var hmac = new HMACSHA256(keyBytes))
                {
                    var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(playerId.ToString()));
                    var sb = new StringBuilder(32);
                    for (int i = 0; i < 16 && i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return "anon";
            }
        }

        // ===================== Weekly report (opt-in telemetry) =====================

        private Dictionary<string, object> GetWeeklyReportConfig()
            => Config["WeeklyReport"] as Dictionary<string, object>;

        private bool GetWeeklyReportEnabled()
        {
            var cfg = GetWeeklyReportConfig();
            if (cfg == null || !cfg.ContainsKey("Enabled")) return true;
            try { return Convert.ToBoolean(cfg["Enabled"]); } catch { return true; }
        }

        private bool GetWeeklyReportAccepted()
        {
            var cfg = GetWeeklyReportConfig();
            if (cfg == null || !cfg.ContainsKey("Accepted")) return false;
            try { return Convert.ToBoolean(cfg["Accepted"]); } catch { return false; }
        }

        private string GetWeeklyReportWebhookUrl()
        {
            var cfg = GetWeeklyReportConfig();
            if (cfg == null || !cfg.ContainsKey("DiscordWebhookUrl") || cfg["DiscordWebhookUrl"] == null) return string.Empty;
            return cfg["DiscordWebhookUrl"].ToString();
        }

        private int GetWeeklyReportIntervalDays()
        {
            var cfg = GetWeeklyReportConfig();
            if (cfg == null || !cfg.ContainsKey("IntervalDays")) return 7;
            try { int v = Convert.ToInt32(cfg["IntervalDays"]); return v < 1 ? 1 : v; } catch { return 7; }
        }

        private bool GetWeeklyReportIncludeKDA()
        {
            var cfg = GetWeeklyReportConfig();
            if (cfg == null || !cfg.ContainsKey("IncludeKDA")) return true;
            try { return Convert.ToBoolean(cfg["IncludeKDA"]); } catch { return true; }
        }

        private bool GetWeeklyReportIncludeLagswitch()
        {
            var cfg = GetWeeklyReportConfig();
            if (cfg == null || !cfg.ContainsKey("IncludeLagswitch")) return true;
            try { return Convert.ToBoolean(cfg["IncludeLagswitch"]); } catch { return true; }
        }

        // One-line notice printed to the server console on load so operators are always aware
        // the plugin can send an anonymous weekly summary and how to opt in/out.
        private void LogDataCollectionDisclosure()
        {
            bool active = GetWeeklyReportEnabled() && GetWeeklyReportAccepted() && !string.IsNullOrWhiteSpace(GetWeeklyReportWebhookUrl());
            Puts("------------------------------------------------------------");
            Puts(Msg(null, "WeeklyDisclosure1"));
            Puts(Msg(null, "WeeklyDisclosure2"));
            Puts(active ? Msg(null, "WeeklyDisclosureActive") : Msg(null, "WeeklyDisclosureInactive"));
            Puts("------------------------------------------------------------");
        }

        // Runs hourly; sends the report only when at least IntervalDays have passed since the last send.
        // ====================================================================================
        // Daily operator report
        // ====================================================================================

        private Dictionary<string, object> GetDailyReportConfig()
        {
            return Config["DailyReport"] as Dictionary<string, object>;
        }

        private bool GetDailyReportBool(string key, bool fallback)
        {
            var cfg = GetDailyReportConfig();
            if (cfg == null || !cfg.ContainsKey(key)) return fallback;
            try { return Convert.ToBoolean(cfg[key]); } catch { return fallback; }
        }

        private int GetDailyReportInt(string key, int fallback, int min, int max)
        {
            var cfg = GetDailyReportConfig();
            if (cfg == null || !cfg.ContainsKey(key)) return fallback;
            try { return Mathf.Clamp(Convert.ToInt32(cfg[key]), min, max); } catch { return fallback; }
        }

        private float GetDailyReportFloat(string key, float fallback)
        {
            var cfg = GetDailyReportConfig();
            if (cfg == null || !cfg.ContainsKey(key)) return fallback;
            try { return Convert.ToSingle(cfg[key]); } catch { return fallback; }
        }

        private string GetDailyReportWebhookUrl()
        {
            var cfg = GetDailyReportConfig();
            if (cfg == null || !cfg.ContainsKey("DiscordWebhookUrl")) return string.Empty;
            return cfg["DiscordWebhookUrl"] as string ?? string.Empty;
        }

        // One player's standing in the digest.
        private class DailyReportRow
        {
            public ulong PlayerId;
            public string Name;
            public string WorstWeapon;
            public float WorstAccuracy;
            public int Samples;
            public float Nerf = 1f;
            public int LagswitchIncidents;
            public int Kills;
            public int Deaths;
            public float Score;
        }

        // Wilson score lower bound of a hit ratio at ~95% confidence.
        //
        // Ranking on raw accuracy puts eleven-for-eleven above forty-of-forty-five, which is
        // backwards: the short window is mostly luck, and the plugin's own metric produces plenty
        // of them (RegisterHit drops misses older than MissExpirySeconds, so a slow-firing player
        // reads as perfect). The lower bound asks "how high is this player's true rate, pessimistically"
        // and small samples answer conservatively on their own, with no special-casing.
        private static float WilsonLowerBound(float ratio, int samples)
        {
            if (samples <= 0) return 0f;
            const float z = 1.96f;
            float p = Mathf.Clamp01(ratio);
            float n = samples;
            float zz = z * z;
            float denominator = 1f + zz / n;
            float centre = (p + zz / (2f * n)) / denominator;
            float margin = z * Mathf.Sqrt(p * (1f - p) / n + zz / (4f * n * n)) / denominator;
            return Mathf.Clamp01(centre - margin);
        }

        // A single 0-1 number so the digest can be ordered by "look at this one first".
        // Accuracy over the weapon's own threshold is the backbone; the damage penalty the plugin
        // already decided on and lagswitch incidents add to it. Deliberately simple and local: the
        // ML service may not be running, and a report that only works with it would mostly not work.
        private float ComputeSuspicionScore(DailyReportRow row, float maxAccuracy)
        {
            float score = 0f;
            if (row.Samples >= 10 && maxAccuracy < 1f)
            {
                float confident = WilsonLowerBound(row.WorstAccuracy, row.Samples);
                if (confident > maxAccuracy)
                {
                    float headroom = Mathf.Max(0.01f, 1f - maxAccuracy);
                    score += 0.6f * Mathf.Clamp01((confident - maxAccuracy) / headroom);
                }
            }
            // Nerf is 1.0 for untouched players and 0 for fully nulled ones.
            score += 0.25f * Mathf.Clamp01(1f - row.Nerf);
            score += 0.15f * Mathf.Clamp01(row.LagswitchIncidents / 5f);
            return Mathf.Clamp01(score);
        }

        private List<DailyReportRow> BuildDailyReportRows(long sinceMs)
        {
            var rows = new List<DailyReportRow>();

            foreach (var playerEntry in _playerStats)
            {
                var row = new DailyReportRow { PlayerId = playerEntry.Key };
                float worstMaxAccuracy = 1f;
                float bestOver = float.MinValue;

                foreach (var weaponEntry in playerEntry.Value)
                {
                    var data = weaponEntry.Value;
                    if (data == null || data.History.Count < 10) continue;

                    var evaluation = EvaluateWeapon(weaponEntry.Key, data);
                    // "Worst" means furthest over its own threshold, not simply highest accuracy —
                    // 70% with a sniper is ordinary, 70% with an LMG is not. A weapon with no
                    // threshold (unconfigured or explosive) can never be the reason to look.
                    float over = evaluation.MaxAccuracy < 1f
                        ? evaluation.Accuracy - evaluation.MaxAccuracy
                        : float.MinValue;
                    if (row.WorstWeapon == null || over > bestOver)
                    {
                        bestOver = over;
                        row.WorstWeapon = weaponEntry.Key;
                        row.WorstAccuracy = evaluation.Accuracy;
                        row.Samples = evaluation.SampleCount;
                        worstMaxAccuracy = evaluation.MaxAccuracy;
                    }
                }

                if (row.WorstWeapon == null) continue;

                row.Nerf = GetLowestNerf(playerEntry.Key);

                // Incidents carry a timestamp, so unlike the accuracy figures these really can be
                // limited to the reporting period.
                List<LagSwitchIncident> incidents;
                if (_lagswitchIncidents.TryGetValue(playerEntry.Key, out incidents) && incidents != null)
                    for (int i = 0; i < incidents.Count; i++)
                        if (incidents[i].TimestampMs >= sinceMs) row.LagswitchIncidents++;

                PlayerKDAStats kda;
                if (_playerKDAStats.TryGetValue(playerEntry.Key, out kda) && kda != null)
                {
                    row.Kills = kda.Kills;
                    row.Deaths = kda.Deaths;
                }

                row.Score = ComputeSuspicionScore(row, worstMaxAccuracy);
                row.Name = ResolvePlayerName(playerEntry.Key);
                rows.Add(row);
            }

            rows.Sort((a, b) => b.Score.CompareTo(a.Score));
            return rows;
        }

        private string ResolvePlayerName(ulong playerId)
        {
            try
            {
                var online = BasePlayer.FindByID(playerId);
                if (online != null && !string.IsNullOrEmpty(online.displayName)) return online.displayName;
                var sleeper = BasePlayer.FindSleeping(playerId);
                if (sleeper != null && !string.IsNullOrEmpty(sleeper.displayName)) return sleeper.displayName;
            }
            catch { }
            return null;
        }

        // Discord caps a message at 2000 characters, so the digest is built to be trimmed: the
        // headline totals first, then as many player rows as fit.
        private string BuildDailyReportContent()
        {
            string server = ConVar.Server.hostname ?? "unknown";
            int hours = GetDailyReportInt("IntervalHours", 24, 1, 168);
            int topCount = GetDailyReportInt("TopCount", 10, 1, 25);
            float minScore = GetDailyReportFloat("MinSuspicionScore", 0.35f);
            bool includeNames = GetDailyReportBool("IncludeNames", true);
            bool includeIds = GetDailyReportBool("IncludeSteamIds", true);

            long sinceMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)hours * 3600000L;
            var rows = BuildDailyReportRows(sinceMs);
            var flagged = rows.FindAll(r => r.Score >= minScore);

            int totalShots = 0, totalHits = 0, nerfedPlayers = 0;
            foreach (var playerEntry in _playerStats)
            {
                foreach (var weaponEntry in playerEntry.Value)
                {
                    var history = weaponEntry.Value?.History;
                    if (history == null) continue;
                    totalShots += history.Count;
                    for (int i = 0; i < history.Count; i++) if (history[i].IsHit) totalHits++;
                }
            }
            foreach (var row in rows) if (row.Nerf < 1f) nerfedPlayers++;

            float overallAccuracy = totalShots > 0 ? (float)totalHits / totalShots : 0f;

            var sb = new StringBuilder();
            sb.Append("**[MogyAC] Daily report** — `").Append(server).Append("`\n");
            sb.Append($"Every {hours}h | Tracked players: {rows.Count} | Damage-reduced now: {nerfedPlayers}\n");
            // Accuracy comes from each weapon's rolling window (the last SampleCount shots), which
            // is the same number the plugin acts on. It is current state, not a total for the
            // period, and saying otherwise would misrepresent it.
            sb.Append($"Rolling windows: {totalShots} shots, {totalHits} hits ({overallAccuracy:P0})\n");

            if (GetDailyReportBool("IncludeLagswitch", true))
            {
                int incidentPlayers = 0, incidentTotal = 0;
                foreach (var entry in _lagswitchIncidents)
                {
                    if (entry.Value == null) continue;
                    int recent = 0;
                    for (int i = 0; i < entry.Value.Count; i++)
                        if (entry.Value[i].TimestampMs >= sinceMs) recent++;
                    if (recent == 0) continue;
                    incidentPlayers++;
                    incidentTotal += recent;
                }
                sb.Append($"Lagswitch incidents (last {hours}h): {incidentTotal} ({incidentPlayers} players)\n");
            }

            if (flagged.Count == 0)
            {
                sb.Append($"\nNo player scored above {minScore:F2}. Nothing to review.");
                return sb.ToString();
            }

            sb.Append($"\n**Most suspicious ({Math.Min(topCount, flagged.Count)} of {flagged.Count} above {minScore:F2}):**\n```\n");
            int shown = 0;
            foreach (var row in flagged)
            {
                if (shown >= topCount) break;

                string who;
                if (includeNames && !string.IsNullOrEmpty(row.Name))
                    who = includeIds ? $"{row.Name} ({row.PlayerId})" : row.Name;
                else if (includeIds)
                    who = row.PlayerId.ToString();
                else
                    who = HashPlayerId(row.PlayerId);

                var line = new StringBuilder();
                line.Append($"{row.Score:F2} | {who} | {row.WorstWeapon} ")
                    .Append($"acc={row.WorstAccuracy:P0} n={row.Samples}");
                if (row.Nerf < 1f) line.Append($" | dmg={row.Nerf:P0}");
                if (row.LagswitchIncidents > 0) line.Append($" | lag={row.LagswitchIncidents}");
                if (GetDailyReportBool("IncludeKDA", true) && (row.Kills > 0 || row.Deaths > 0))
                    line.Append($" | K/D={row.Kills}/{row.Deaths}");

                // Stop before overrunning Discord's limit rather than getting truncated mid-row.
                if (sb.Length + line.Length + 16 > 1900) break;
                sb.Append(line).Append('\n');
                shown++;
            }
            sb.Append("```");
            sb.Append("\nScore combines accuracy over the weapon's own threshold, applied damage ")
              .Append("reduction and lagswitch incidents. It ranks who to look at — it is not proof.");

            return sb.ToString();
        }

        private long ReadDailyReportLastSent()
        {
            try
            {
                var data = _dailyReportData.ReadObject<Dictionary<string, long>>();
                long value;
                if (data != null && data.TryGetValue("lastSentMs", out value)) return value;
            }
            catch { }
            return 0;
        }

        private void WriteDailyReportLastSent(long ms)
        {
            try { _dailyReportData.WriteObject(new Dictionary<string, long> { ["lastSentMs"] = ms }); }
            catch (Exception ex) { DebugLog($"Daily report state save failed: {ex.Message}"); }
        }

        private void DailyReportTick()
        {
            try
            {
                if (!GetDailyReportBool("Enabled", false)) return;
                string url = GetDailyReportWebhookUrl();
                if (string.IsNullOrWhiteSpace(url)) return;

                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long lastSent = ReadDailyReportLastSent();

                // First activation seeds the clock, so enabling the feature does not immediately
                // fire a report covering whatever happened to be in memory.
                if (lastSent == 0) { WriteDailyReportLastSent(nowMs); return; }

                long intervalMs = (long)GetDailyReportInt("IntervalHours", 24, 1, 168) * 3600000L;
                if (nowMs - lastSent < intervalMs) return;

                SendDiscordReport(url, BuildDailyReportContent(), "daily report");
                WriteDailyReportLastSent(nowMs);
            }
            catch (Exception ex)
            {
                DebugLog($"Daily report tick failed: {ex.Message}");
            }
        }

        private void WeeklyReportTick()
        {
            try
            {
                if (!GetWeeklyReportEnabled() || !GetWeeklyReportAccepted()) return;
                string url = GetWeeklyReportWebhookUrl();
                if (string.IsNullOrWhiteSpace(url)) return;

                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long lastSent = ReadWeeklyReportLastSent();

                // First activation: seed the timestamp and wait a full interval before the first send.
                if (lastSent == 0) { WriteWeeklyReportLastSent(nowMs); return; }

                long intervalMs = (long)GetWeeklyReportIntervalDays() * 86400000L;
                if (nowMs - lastSent < intervalMs) return;

                SendWeeklyReport(url, BuildWeeklyReportContent());
                WriteWeeklyReportLastSent(nowMs);
            }
            catch (Exception ex)
            {
                DebugLog($"Weekly report tick failed: {ex.Message}");
            }
        }

        private long ReadWeeklyReportLastSent()
        {
            try
            {
                var data = _weeklyReportData.ReadObject<Dictionary<string, long>>();
                long v;
                if (data != null && data.TryGetValue("lastSentMs", out v)) return v;
            }
            catch { }
            return 0;
        }

        private void WriteWeeklyReportLastSent(long ms)
        {
            try { _weeklyReportData.WriteObject(new Dictionary<string, long> { ["lastSentMs"] = ms }); }
            catch (Exception ex) { DebugLog($"Weekly report state save failed: {ex.Message}"); }
        }

        // Builds an aggregated, anonymized summary. Only hashed player IDs and operational metrics
        // are included — never names, IPs or raw SteamIDs.
        private string BuildWeeklyReportContent()
        {
            string server = ConVar.Server.hostname ?? "unknown";
            int days = GetWeeklyReportIntervalDays();

            int trackedPlayers = _playerStats.Count;
            int totalShots = 0, totalHits = 0;
            var flagged = new List<KeyValuePair<float, string>>();

            foreach (var pEntry in _playerStats)
            {
                string bestWeapon = null;
                float bestAcc = -1f;
                int bestSamples = 0;

                foreach (var wEntry in pEntry.Value)
                {
                    var hist = wEntry.Value?.History;
                    if (hist == null || hist.Count == 0) continue;

                    int hits = 0;
                    for (int i = 0; i < hist.Count; i++) if (hist[i].IsHit) hits++;
                    totalShots += hist.Count;
                    totalHits += hits;

                    if (hist.Count >= 10)
                    {
                        float acc = (float)hits / hist.Count;
                        if (acc > bestAcc) { bestAcc = acc; bestWeapon = wEntry.Key; bestSamples = hist.Count; }
                    }
                }

                if (bestWeapon != null && bestAcc >= 0.5f)
                    flagged.Add(new KeyValuePair<float, string>(bestAcc,
                        $"{HashPlayerId(pEntry.Key)} | {bestWeapon} | acc={bestAcc:P0} | n={bestSamples}"));
            }

            flagged.Sort((a, b) => b.Key.CompareTo(a.Key));

            float overallAcc = totalShots > 0 ? (float)totalHits / totalShots : 0f;

            var sb = new StringBuilder();
            sb.Append("**[MogyAC] Weekly report** — `").Append(server).Append("`\n");
            sb.Append($"Window: ~{days}d | Players: {trackedPlayers} | Shots: {totalShots} | Hits: {totalHits} ({overallAcc:P0})\n");

            if (GetWeeklyReportIncludeLagswitch())
            {
                int lsPlayers = 0, lsTotal = 0;
                foreach (var e in _lagswitchIncidents)
                {
                    if (e.Value == null || e.Value.Count == 0) continue;
                    lsPlayers++;
                    lsTotal += e.Value.Count;
                }
                sb.Append($"Lagswitch incidents: {lsTotal} ({lsPlayers} players)\n");
            }

            if (GetWeeklyReportIncludeKDA())
            {
                int kills = 0, deaths = 0;
                foreach (var e in _playerKDAStats) { kills += e.Value.Kills; deaths += e.Value.Deaths; }
                sb.Append($"K/D total: {kills}/{deaths}\n");
            }

            sb.Append("Top suspicious (hashed):\n```\n");
            int shown = 0;
            foreach (var f in flagged)
            {
                if (shown >= 10) break;
                sb.Append(f.Value).Append('\n');
                shown++;
            }
            if (shown == 0) sb.Append("(none above threshold)\n");
            sb.Append("```");

            return sb.ToString();
        }

        private void SendWeeklyReport(string url, string content)
        {
            SendDiscordReport(url, content, "weekly report");
        }

        // Shared Discord delivery for both digests. `label` only appears in debug logs.
        private void SendDiscordReport(string url, string content, string label)
        {
            if (string.IsNullOrEmpty(content)) return;
            if (content.Length > 1900) content = content.Substring(0, 1900) + "…";

            string body = JsonConvert.SerializeObject(new Dictionary<string, object>
            {
                ["username"] = "MogyAntiCheat",
                ["content"] = content
            });
            var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };

            try
            {
                webrequest.Enqueue(url, body, (code, response) =>
                {
                    if (code >= 200 && code < 300) DebugLog($"{label} sent.");
                    else DebugLog($"{label} send failed: code={code}");
                }, this, RequestMethod.POST, headers);
            }
            catch (Exception ex)
            {
                DebugLog($"{label} request error: {ex.Message}");
            }
        }

        [ChatCommand("ac-daily-now")]
        void CmdDailyNow(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }

            string url = GetDailyReportWebhookUrl();
            if (string.IsNullOrWhiteSpace(url)) { SendReply(player, Msg(player, "DailyNoUrl")); return; }

            // Deliberately does not require Enabled: this is how an operator tests the webhook
            // before switching the schedule on. It does not advance the schedule either.
            SendDiscordReport(url, BuildDailyReportContent(), "daily report");
            SendReply(player, Msg(player, "DailySentNow"));
        }

        [ChatCommand("ac-weekly-now")]
        void CmdWeeklyNow(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }

            if (!GetWeeklyReportAccepted()) { SendReply(player, Msg(player, "WeeklyNotAccepted")); return; }
            string url = GetWeeklyReportWebhookUrl();
            if (string.IsNullOrWhiteSpace(url)) { SendReply(player, Msg(player, "WeeklyNoUrl")); return; }

            SendWeeklyReport(url, BuildWeeklyReportContent());
            WriteWeeklyReportLastSent(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            SendReply(player, Msg(player, "WeeklySentNow"));
        }

        private void FetchMLSuggestion(ulong playerId)
        {
            if (!IsMLServiceEnabled()) return;
            string endpoint = GetMLServiceEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint)) return;

            string url = $"{endpoint}/penalty-suggestion?player_id={playerId}";
            var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
            string token = GetMLServiceAuthToken();
            if (!string.IsNullOrWhiteSpace(token)) headers["Authorization"] = $"Bearer {token}";

            try
            {
                webrequest.Enqueue(url, null, (code, response) =>
                {
                    if (code < 200 || code >= 300 || string.IsNullOrWhiteSpace(response)) return;
                    try
                    {
                        var jObj = JObject.Parse(response);
                        var weaponsToken = jObj["weapons"] as JObject;
                        if (weaponsToken == null) return;

                        Dictionary<string, MLSuggestionCacheEntry> playerCache;
                        if (!_mlSuggestionCache.TryGetValue(playerId, out playerCache))
                        {
                            playerCache = new Dictionary<string, MLSuggestionCacheEntry>();
                            _mlSuggestionCache[playerId] = playerCache;
                        }

                        foreach (var prop in weaponsToken.Properties())
                        {
                            var wData = prop.Value as JObject;
                            if (wData == null) continue;
                            playerCache[prop.Name] = new MLSuggestionCacheEntry
                            {
                                FetchedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                                Confidence = wData["ml_confidence"]?.Value<float>() ?? 0f,
                                SuggestedNerfPct = wData["suggested_nerf_pct"]?.Value<int>() ?? 0,
                                AnomalyType = wData["anomaly_type"]?.Value<string>(),
                                Reason = wData["explanation"]?.Value<string>()
                            };
                        }
                        DebugLog($"ML suggestion cached for player {playerId}");
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"ML suggestion parse error: {ex.Message}");
                    }
                }, this, RequestMethod.GET, headers);
            }
            catch (Exception ex)
            {
                DebugLog($"ML suggestion fetch error: {ex.Message}");
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

            float aimDeltaDeg, snapDeg, snapSettleMs;
            MeasureAimKinematics(player, out aimDeltaDeg, out snapDeg, out snapSettleMs);

            EnqueueTelemetry(new ShotTelemetryEvent
            {
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PlayerHash = HashPlayerId(player.userID),
                WeaponName = wName,
                Distance = 0f,
                Hit = false,
                PingMs = ping,
                DeltaPingMs = deltaPing,
                AccuracyInWindow = weaponData.GetAccuracy(),
                EventType = "shot",
                GameTimeHour = GetGameTimeHour(),
                AimDeltaDeg = aimDeltaDeg,
                SnapDeg = snapDeg,
                SnapSettleMs = snapSettleMs
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
            float rawDist = Vector3.Distance(info.HitPositionWorld, info.PointStart);
            float dist = SanitizeHitDistance(rawDist, wName);
            float expiry = Config["MissExpirySeconds"] != null ? Convert.ToSingle(Config["MissExpirySeconds"]) : 20f;

            int limit = GetWeaponTuning(wName).SampleCount;

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
            string hitArea = GetHitAreaName(info);
            float gameTimeHour = GetGameTimeHour();

            weaponData.RegisterHit(dist, limit, expiry, ping, deltaPing);

            EnqueueTelemetry(new ShotTelemetryEvent
            {
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PlayerHash = HashPlayerId(attacker.userID),
                WeaponName = wName,
                // The raw measurement is logged even when it was rejected above, so the trainer can
                // still see how often the reading is broken. Detection uses the sanitized value.
                Distance = rawDist,
                Hit = true,
                PingMs = ping,
                DeltaPingMs = deltaPing,
                AccuracyInWindow = weaponData.GetAccuracy(),
                EventType = "hit",
                HitArea = hitArea,
                GameTimeHour = gameTimeHour,
                // The shot's own aim kinematics were recorded on OnWeaponFired; a hit event
                // describes the impact, not the aim, so these stay unset here.
                AimDeltaDeg = -1f,
                SnapDeg = -1f,
                SnapSettleMs = -1f
            });

            var evaluation = EvaluateWeapon(wName, weaponData);
            ProcessSuspicionTransition(attacker, wName, evaluation);

            float globalNerf = GetLowestNerf(attacker.userID);

            if (IsMLServiceEnabled())
            {
                float mlNerf;
                if (TryGetCachedMLNerf(attacker.userID, wName, out mlNerf))
                {
                    if (debugMode)
                        DebugLog($"ML nerf applied: player={attacker.displayName} ({attacker.userID}), weapon={wName}, mlNerf={mlNerf:P2}, localNerf={globalNerf:P2}");
                    globalNerf = Math.Min(globalNerf, mlNerf);
                }
                else if (!IsFallbackToLocalScoringEnabled())
                {
                    globalNerf = 1.0f;
                    if (debugMode)
                        DebugLog($"ML nerf: no cached suggestion for {attacker.displayName}/{wName}, fallback disabled → no nerf");
                }
            }

            float manualOverrideMultiplier;
            bool hasManualOverride = _manualOverrides.TryGetValue(attacker.userID, out manualOverrideMultiplier);
            if (hasManualOverride)
                globalNerf = Math.Min(globalNerf, manualOverrideMultiplier);
            bool shouldApplyNerfToAttacker = hasManualOverride || !HasBypass(attacker) || debugMode;
            if (debugMode)
            {
                DebugLog($"Damage check: attacker={attacker.displayName} ({attacker.userID}), weapon={wName}, acc={evaluation.Accuracy:P2}, max={evaluation.MaxAccuracy:P2}, ping={ping}ms, globalNerf={globalNerf:P2}");
            }

            if (shouldApplyNerfToAttacker && globalNerf < 1.0f && IsDamageReductionEnabled())
            {
                float originalDamage = info.damageTypes.Total();
                info.damageTypes.ScaleAll(globalNerf);
                float scaledDamage = info.damageTypes.Total();
                EmitPenaltyEvent(attacker, targetPlayer, wName, globalNerf, originalDamage, scaledDamage, ping, hitArea, gameTimeHour);
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
                PlayerHash = HashPlayerId(victim.userID),
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
            string killHitArea = wasHeadshot ? "head" : GetHitAreaName(info);

            EnqueueTelemetry(new ShotTelemetryEvent
            {
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                PlayerHash = HashPlayerId(attacker.userID),
                WeaponName = wName,
                Distance = killDist,
                Hit = true,
                PingMs = killPing,
                EventType = "kill",
                HitArea = killHitArea,
                GameTimeHour = GetGameTimeHour()
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

            // Falls back to the weapon's family when the config does not name it, and only then to
            // MaxAccuracy = 1.0 (never flagged). See GetWeaponTuning.
            var tuning = GetWeaponTuning(weaponName);
            evaluation.MaxAccuracy = tuning.MaxAccuracy;
            evaluation.SafeDistance = tuning.SafeDistance;
            evaluation.TuningSource = tuning.Source;
            evaluation.WeightedScore = data.GetWeightedScore(evaluation.SafeDistance);

            if (!tuning.AppliesPenalty) return evaluation;

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
            FetchMLSuggestion(playerId);
        }

        private void EmitPenaltyEvent(BasePlayer attacker, BasePlayer target, string weaponName, float appliedMultiplier, float originalDamage, float scaledDamage, int pingAtEvent = 0, string hitArea = null, float gameTimeHour = -1f)
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
                ["hitArea"] = hitArea ?? "unknown",
                ["gameTimeHour"] = gameTimeHour,
                ["timestampUtc"] = DateTime.UtcNow.ToString("o")
            };

            MLSuggestionCacheEntry mlEntry = null;
            Dictionary<string, MLSuggestionCacheEntry> playerCache;
            if (_mlSuggestionCache.TryGetValue(attacker.userID, out playerCache))
            {
                MLSuggestionCacheEntry entry;
                if (playerCache.TryGetValue(weaponName, out entry) && !entry.IsExpired(GetMLServiceCacheSuggestionsSeconds()))
                    mlEntry = entry;
            }
            if (mlEntry != null)
            {
                payload["mlConfidence"] = mlEntry.Confidence;
                payload["mlSuggestedNerfPct"] = mlEntry.SuggestedNerfPct;
                payload["mlAnomalyType"] = mlEntry.AnomalyType ?? string.Empty;
                payload["mlApplied"] = true;
            }

            DebugLog($"Penalty applied: attacker={attacker.displayName} ({attacker.userID}), weapon={weaponName}, mult={appliedMultiplier:F2}, dmg={originalDamage:F1}->{scaledDamage:F1}, ping={pingAtEvent}ms, hitArea={hitArea ?? "unknown"}, gameTime={gameTimeHour:F1}h");

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

        private static readonly char[] WeaponNameSeparators = { '.', '_', '-' };

        // Prefab short names that no amount of pattern matching can bridge to the config's item
        // shortnames. Rust reports "smg" for the Custom SMG whose item shortname is "smg.2".
        private static readonly Dictionary<string, string> WeaponKeyAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["smg"] = "smg.2",
            ["semi_auto_rifle"] = "rifle.semiauto",
            ["semi_auto_pistol"] = "pistol.semiauto",
            ["hunting_bow"] = "bow.hunting"
        };

        // Name fragments used to place an unconfigured weapon into a family. Order matters: the
        // first match wins, so more specific fragments come first.
        private static readonly List<KeyValuePair<string, string[]>> WeaponFamilyPatterns = new List<KeyValuePair<string, string[]>>
        {
            new KeyValuePair<string, string[]>("explosive", new[] { "rocket_launcher", "rpg", "mgl", "grenade", "launcher", "flamethrower" }),
            new KeyValuePair<string, string[]>("lmg", new[] { "m249", "hmlmg", "minigun", "lmg" }),
            new KeyValuePair<string, string[]>("sniper", new[] { "l96", "bolt", "sniper" }),
            new KeyValuePair<string, string[]>("semi_rifle", new[] { "semi_auto_rifle", "semiauto_rifle", "sks", "m39" }),
            new KeyValuePair<string, string[]>("auto_rifle", new[] { "ak47", "ak47u", "lr300", "m16", "assault", "custom_smg" }),
            new KeyValuePair<string, string[]>("smg", new[] { "smg", "mp5", "thompson" }),
            new KeyValuePair<string, string[]>("shotgun", new[] { "shotgun", "spas12", "blunderbuss" }),
            new KeyValuePair<string, string[]>("pistol", new[] { "pistol", "python", "revolver", "glock", "m92", "nailgun" }),
            new KeyValuePair<string, string[]>("projectile", new[] { "bow", "crossbow", "speargun", "compound" })
        };

        // Resolved per-weapon detection settings, plus where they came from (for /ac-why and debug logs).
        private class WeaponTuning
        {
            public float MaxAccuracy = 1f;
            public int SampleCount = DefaultWeaponSampleCount;
            public float SafeDistance = 1f;
            public string Source = "unconfigured";
            // Whether settings were found at all. Distinct from whether they penalise: the explosive
            // family resolves successfully to MaxAccuracy = 1.0 on purpose.
            public bool Resolved;
            public bool AppliesPenalty { get { return Resolved && MaxAccuracy < 1f; } }
        }

        // Sorted, separator-insensitive token signature: "shotgun_pump" and "shotgun.pump" both
        // become "pump.shotgun", and "bolt_rifle" matches "rifle.bolt".
        private static string WeaponTokenSignature(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var parts = name.ToLowerInvariant().Split(WeaponNameSeparators, StringSplitOptions.RemoveEmptyEntries);
            Array.Sort(parts, StringComparer.Ordinal);
            return string.Join(".", parts);
        }

        internal static string ClassifyWeaponFamily(string weaponName)
        {
            if (string.IsNullOrEmpty(weaponName)) return "other";
            string name = weaponName.ToLowerInvariant();
            foreach (var entry in WeaponFamilyPatterns)
                foreach (var fragment in entry.Value)
                    if (name.Contains(fragment)) return entry.Key;
            return "other";
        }

        // Carbon omits the category prefix from ShortPrefabName (e.g. "m39" instead of Oxide's
        // "rifle.m39"), and modded servers report prefab names with underscores where the config uses
        // dots. Match in order of decreasing certainty: exact, alias, last segment, token signature.
        private string ResolveWeaponConfigKey(Dictionary<string, object> weaponsCfg, string wName)
        {
            if (weaponsCfg == null || string.IsNullOrEmpty(wName)) return null;
            if (weaponsCfg.ContainsKey(wName)) return wName;

            foreach (var key in weaponsCfg.Keys)
                if (string.Equals(key, wName, StringComparison.OrdinalIgnoreCase)) return key;

            string alias;
            if (WeaponKeyAliases.TryGetValue(wName, out alias) && weaponsCfg.ContainsKey(alias))
                return alias;

            foreach (var key in weaponsCfg.Keys)
            {
                int dot = key.LastIndexOf('.');
                if (dot >= 0 && string.Equals(key.Substring(dot + 1), wName, StringComparison.OrdinalIgnoreCase))
                    return key;
            }

            string signature = WeaponTokenSignature(wName);
            if (signature.Length > 0)
                foreach (var key in weaponsCfg.Keys)
                    if (WeaponTokenSignature(key) == signature) return key;

            return null;
        }

        // Every unconfigured weapon used to fall through to MaxAccuracy = 1.0, i.e. no checking at
        // all. On a modded server that silently disabled detection for a third of all shots, so a
        // family fallback now covers weapons the config does not name. Deliberately lenient — it
        // exists to catch the blatant cases, and `ml-service/train.py` replaces these with values
        // measured from the server's own telemetry.
        private WeaponTuning GetWeaponTuning(string weaponName)
        {
            if (string.IsNullOrEmpty(weaponName)) return new WeaponTuning();

            WeaponTuning cached;
            if (_weaponTuningCache.TryGetValue(weaponName, out cached)) return cached;

            var tuning = new WeaponTuning();
            var weaponsCfg = Config["Weapons"] as Dictionary<string, object>;
            string key = ResolveWeaponConfigKey(weaponsCfg, weaponName);
            var entry = key != null ? weaponsCfg[key] as Dictionary<string, object> : null;

            if (entry != null && entry.ContainsKey("MaxAccuracy"))
            {
                try
                {
                    tuning.MaxAccuracy = Convert.ToSingle(entry["MaxAccuracy"]);
                    tuning.SafeDistance = entry.ContainsKey("SafeDistance") ? Convert.ToSingle(entry["SafeDistance"]) : 1f;
                    tuning.SampleCount = entry.ContainsKey("SampleCount") ? Convert.ToInt32(entry["SampleCount"]) : DefaultWeaponSampleCount;
                    tuning.Source = key;
                    tuning.Resolved = true;
                }
                catch (Exception ex)
                {
                    DebugLog($"Invalid weapon config for '{key}': {ex.Message}");
                    tuning = new WeaponTuning();
                }
            }
            else
            {
                var family = ClassifyWeaponFamily(weaponName);
                var familyCfg = GetWeaponFamilyDefaults(family);
                if (familyCfg != null)
                {
                    try
                    {
                        tuning.MaxAccuracy = Convert.ToSingle(familyCfg["MaxAccuracy"]);
                        tuning.SampleCount = Convert.ToInt32(familyCfg["SampleCount"]);
                        tuning.SafeDistance = Convert.ToSingle(familyCfg["SafeDistance"]);
                        tuning.Source = "family:" + family;
                        tuning.Resolved = true;
                    }
                    catch (Exception ex)
                    {
                        DebugLog($"Invalid weapon family default for '{family}': {ex.Message}");
                        tuning = new WeaponTuning();
                    }
                }
                if (!tuning.Resolved) NoteUnresolvedWeapon(weaponName, family);
            }

            if (tuning.SampleCount < 1) tuning.SampleCount = DefaultWeaponSampleCount;
            if (tuning.SafeDistance <= 0f) tuning.SafeDistance = 1f;
            _weaponTuningCache[weaponName] = tuning;
            return tuning;
        }

        private Dictionary<string, object> GetWeaponFamilyDefaults(string family)
        {
            var cfg = Config["WeaponFallback"] as Dictionary<string, object>;
            if (cfg == null) return null;
            if (cfg.ContainsKey("Enabled"))
            {
                try { if (!Convert.ToBoolean(cfg["Enabled"])) return null; } catch { }
            }
            if (!cfg.ContainsKey("Families")) return null;
            var families = cfg["Families"] as Dictionary<string, object>;
            if (families == null || !families.ContainsKey(family)) return null;
            return families[family] as Dictionary<string, object>;
        }

        // Told once per weapon name, so an operator finds out a weapon is unchecked instead of
        // assuming it is covered.
        private void NoteUnresolvedWeapon(string weaponName, string family)
        {
            if (!_reportedUnresolvedWeapons.Add(weaponName)) return;
            PrintWarning($"[MogyAC] Weapon '{weaponName}' (family: {family}) has no config entry and no family fallback " +
                         "- it will never be flagged. Add it to the Weapons config block, or enable WeaponFallback.");
        }

        private void InvalidateWeaponTuningCache()
        {
            _weaponTuningCache.Clear();
        }

        private float GetMaxHitDistance()
        {
            if (Config["MaxHitDistance"] == null) return DefaultMaxHitDistance;
            try { return Convert.ToSingle(Config["MaxHitDistance"]); } catch { return DefaultMaxHitDistance; }
        }

        // Vector3.Distance(info.HitPositionWorld, info.PointStart) degenerates into a distance from
        // the world origin when PointStart is unset, producing readings of 1000-2000 m on a 4k map.
        // The weighted score is squared in the penalty term, so one such reading is enough to null a
        // player's damage. The hit is real and still counts; only the distance is discarded.
        private float SanitizeHitDistance(float distance, string weaponName)
        {
            float max = GetMaxHitDistance();
            if (max <= 0f || distance <= max) return distance;
            DebugLog($"Implausible hit distance {distance:F0}m for weapon={weaponName} (max {max:F0}m) - treating as unknown.");
            return 0f;
        }

        // -- Aim kinematics ------------------------------------------------------------------
        //
        // Hit ratio says how often a player lands shots; it cannot say whether a human aimed them.
        // These samples exist to describe the *approach to the target*: an assisted shot is
        // preceded by a large angular step that stops dead and fires within a few tens of
        // milliseconds, repeatably. A human decelerates onto the target and the settle time
        // scatters. Collected here, analysed offline by ml-service.

        private Dictionary<string, object> GetAimTrackingConfig()
        {
            return Config["AimTracking"] as Dictionary<string, object>;
        }

        private bool IsAimTrackingEnabled()
        {
            var cfg = GetAimTrackingConfig();
            if (cfg == null || !cfg.ContainsKey("Enabled")) return true;
            try { return Convert.ToBoolean(cfg["Enabled"]); } catch { return true; }
        }

        private float GetAimConfigFloat(string key, float fallback)
        {
            var cfg = GetAimTrackingConfig();
            if (cfg == null || !cfg.ContainsKey(key)) return fallback;
            try { return Convert.ToSingle(cfg[key]); } catch { return fallback; }
        }

        private void StartAimSampling()
        {
            _aimSampleTimer?.Destroy();
            if (!IsAimTrackingEnabled()) return;
            float hz = Mathf.Clamp(GetAimConfigFloat("SampleHz", 20f), 5f, 50f);
            _aimSampleTimer = timer.Every(1f / hz, SampleAimTrails);
        }

        // Only players actually holding a ranged weapon are sampled, so the cost scales with the
        // number of people in a fight rather than with the server population.
        private void SampleAimTrails()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            float windowSeconds = Mathf.Clamp(GetAimConfigFloat("WindowMs", 400f), 100f, 2000f) / 1000f;

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsNpc || !player.userID.IsSteamId()) continue;
                if (!(player.GetActiveItem()?.GetHeldEntity() is BaseProjectile)) continue;

                Vector3 forward;
                try { forward = player.eyes != null ? player.eyes.HeadForward() : Vector3.zero; }
                catch { continue; }
                if (forward == Vector3.zero) continue;

                List<AimSample> trail;
                if (!_aimTrails.TryGetValue(player.userID, out trail))
                {
                    trail = new List<AimSample>();
                    _aimTrails[player.userID] = trail;
                }
                trail.Add(new AimSample { Realtime = now, Forward = forward });
                // Keep only the analysis window; anything older cannot describe this shot.
                int drop = 0;
                while (drop < trail.Count && now - trail[drop].Realtime > windowSeconds) drop++;
                if (drop > 0) trail.RemoveRange(0, drop);
            }

            if (_aimTrails.Count > 0 && _aimTrails.Count > BasePlayer.activePlayerList.Count * 2)
                PruneAimTrails(now, windowSeconds);
        }

        private void PruneAimTrails(float now, float windowSeconds)
        {
            var stale = new List<ulong>();
            foreach (var entry in _aimTrails)
                if (entry.Value.Count == 0 || now - entry.Value[entry.Value.Count - 1].Realtime > windowSeconds * 4f)
                    stale.Add(entry.Key);
            foreach (var id in stale)
            {
                _aimTrails.Remove(id);
                _lastShotAim.Remove(id);
            }
        }

        // Returns (aimDeltaDeg, snapDeg, snapSettleMs); -1 for anything not measurable this shot.
        private void MeasureAimKinematics(BasePlayer player, out float aimDeltaDeg, out float snapDeg,
                                          out float snapSettleMs)
        {
            aimDeltaDeg = -1f;
            snapDeg = -1f;
            snapSettleMs = -1f;
            if (!IsAimTrackingEnabled() || player == null) return;

            Vector3 forward;
            try { forward = player.eyes != null ? player.eyes.HeadForward() : Vector3.zero; }
            catch { return; }
            if (forward == Vector3.zero) return;

            Vector3 previousShotAim;
            if (_lastShotAim.TryGetValue(player.userID, out previousShotAim))
                aimDeltaDeg = Vector3.Angle(previousShotAim, forward);
            _lastShotAim[player.userID] = forward;

            List<AimSample> trail;
            if (!_aimTrails.TryGetValue(player.userID, out trail) || trail.Count < 3) return;

            // Largest single step between consecutive samples, and how long ago it happened.
            float now = UnityEngine.Time.realtimeSinceStartup;
            float largest = 0f;
            float largestAt = -1f;
            for (int i = 1; i < trail.Count; i++)
            {
                float step = Vector3.Angle(trail[i - 1].Forward, trail[i].Forward);
                if (step > largest)
                {
                    largest = step;
                    largestAt = trail[i].Realtime;
                }
            }
            snapDeg = largest;
            if (largestAt >= 0f) snapSettleMs = Mathf.Max(0f, (now - largestAt) * 1000f);
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
            // Resolved settings are cached per prefab name; a config edit has to drop the cache.
            InvalidateWeaponTuningCache();
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

        object GetMLPenaltySuggestion(ulong playerId, string weapon)
        {
            Dictionary<string, MLSuggestionCacheEntry> playerCache;
            if (!_mlSuggestionCache.TryGetValue(playerId, out playerCache)) return null;

            string wKey = string.IsNullOrWhiteSpace(weapon) ? null : weapon;
            if (wKey != null)
            {
                MLSuggestionCacheEntry entry;
                if (!playerCache.TryGetValue(wKey, out entry) || entry.IsExpired(GetMLServiceCacheSuggestionsSeconds())) return null;
                return new Dictionary<string, object>
                {
                    ["confidence"] = entry.Confidence,
                    ["suggestedNerfPct"] = entry.SuggestedNerfPct,
                    ["anomalyType"] = entry.AnomalyType ?? string.Empty,
                    ["reason"] = entry.Reason ?? string.Empty
                };
            }

            int cacheSeconds = GetMLServiceCacheSuggestionsSeconds();
            var all = new Dictionary<string, object>();
            foreach (var kv in playerCache)
            {
                if (!kv.Value.IsExpired(cacheSeconds))
                    all[kv.Key] = new Dictionary<string, object>
                    {
                        ["confidence"] = kv.Value.Confidence,
                        ["suggestedNerfPct"] = kv.Value.SuggestedNerfPct,
                        ["anomalyType"] = kv.Value.AnomalyType ?? string.Empty,
                        ["reason"] = kv.Value.Reason ?? string.Empty
                    };
            }
            return all.Count > 0 ? (object)all : null;
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
                _mlSuggestionCache.Remove(target.userID);
                _manualOverrides.Remove(target.userID);
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

            // Where the thresholds came from matters as much as the numbers: a family fallback is a
            // guess, an entry in the Weapons block is a decision, and "unconfigured" means no checking.
            if (string.IsNullOrEmpty(eval.TuningSource) || eval.TuningSource == "unconfigured")
                SendReply(player, Msg(player, "WhyNoConfig", weaponName));
            else
                SendReply(player, Msg(player, "WhyTuningSource", weaponName, eval.TuningSource));

            if (!eval.HasEnoughData) { SendReply(player, Msg(player, "WhyReasonNoData")); return; }
            if (!eval.IsSuspicious) { SendReply(player, Msg(player, "WhyReasonBelowThreshold")); return; }
        }

        [ChatCommand("ac-ml-feedback")]
        void CmdAcMLFeedback(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }
            if (args.Length < 2) { SendReply(player, Msg(player, "MLFeedbackUsage")); return; }

            if (!IsMLServiceEnabled() || string.IsNullOrWhiteSpace(GetMLServiceEndpoint()))
            {
                SendReply(player, Msg(player, "MLServiceDisabled"));
                return;
            }

            BasePlayer target = BasePlayer.Find(args[0]);
            if (target == null) { SendReply(player, Msg(player, "PlayerNotFound")); return; }

            string outcome = args[1].ToLowerInvariant();
            if (outcome != "confirmed_cheater" && outcome != "false_positive" && outcome != "uncertain")
            {
                SendReply(player, Msg(player, "MLFeedbackUsage"));
                return;
            }

            string endpoint = $"{GetMLServiceEndpoint()}/feedback";
            var feedbackPayload = new Dictionary<string, object>
            {
                ["player_id"] = target.userID.ToString(),
                ["outcome"] = outcome,
                ["feedback_timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["admin_comment"] = $"Submitted by {player.displayName}"
            };
            string body = JsonConvert.SerializeObject(feedbackPayload);
            var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" };
            string token = GetMLServiceAuthToken();
            if (!string.IsNullOrWhiteSpace(token)) headers["Authorization"] = $"Bearer {token}";

            try
            {
                webrequest.Enqueue(endpoint, body, (code, response) =>
                {
                    if (code >= 200 && code < 300)
                        SendReply(player, Msg(player, "MLFeedbackSent", target.displayName, outcome));
                    else
                        SendReply(player, Msg(player, "MLFeedbackFailed"));
                }, this, RequestMethod.POST, headers);
            }
            catch
            {
                SendReply(player, Msg(player, "MLFeedbackFailed"));
            }
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

        [ChatCommand("ac-dashboard")]
        void CmdAcDashboard(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }

            var rows = new System.Text.StringBuilder();
            rows.AppendLine($"<color=#55ff55>{Msg(player, "DashboardHeader")}</color>");

            bool any = false;
            foreach (var kv in _playerStats)
            {
                ulong pid = kv.Key;
                float nerf = GetLowestNerf(pid);

                PlayerPingStats ps;
                _playerPingStats.TryGetValue(pid, out ps);
                double pingAvg = ps != null ? ps.EMA : 0.0;

                PlayerKDAStats kda;
                _playerKDAStats.TryGetValue(pid, out kda);
                int kills = kda != null ? kda.Kills : 0;
                int deaths = kda != null ? kda.Deaths : 0;
                int assists = kda != null ? kda.Assists : 0;

                List<LagSwitchIncident> incidents;
                _lagswitchIncidents.TryGetValue(pid, out incidents);
                int lsCount = incidents != null ? incidents.Count : 0;

                float manualMult;
                string overrideStr = _manualOverrides.TryGetValue(pid, out manualMult)
                    ? $"{(1f - manualMult) * 100f:F0}%"
                    : "-";

                BasePlayer online = BasePlayer.FindByID(pid);
                string name = online != null ? online.displayName : pid.ToString();

                rows.AppendLine(Msg(player, "DashboardRow", name, nerf, pingAvg, lsCount, kills, deaths, assists, overrideStr));
                any = true;
            }

            if (!any) rows.AppendLine(Msg(player, "DashboardNoPlayers"));
            SendReply(player, rows.ToString());
        }

        [ChatCommand("ac-override")]
        void CmdAcOverride(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }
            if (args.Length < 2) { SendReply(player, Msg(player, "OverrideUsage")); return; }

            BasePlayer target = BasePlayer.Find(args[0]);
            if (target == null) { SendReply(player, Msg(player, "PlayerNotFound")); return; }

            string valArg = args[1].Trim().ToLowerInvariant();
            string oldValue;
            float existingMult;
            oldValue = _manualOverrides.TryGetValue(target.userID, out existingMult)
                ? $"{(1f - existingMult) * 100f:F0}%"
                : "auto";

            if (valArg == "off")
            {
                _manualOverrides.Remove(target.userID);
                _overrideAuditLog.Add(new OverrideAuditEntry
                {
                    TimestampUtc = DateTime.UtcNow.ToString("O"),
                    AdminId = player.userID,
                    AdminName = player.displayName,
                    TargetId = target.userID,
                    TargetName = target.displayName,
                    OldValue = oldValue,
                    NewValue = "auto"
                });
                SendReply(player, Msg(player, "OverrideCleared", target.displayName));
                return;
            }

            int pct;
            if (!int.TryParse(valArg, out pct) || pct < 0 || pct > 100)
            {
                SendReply(player, Msg(player, "OverrideInvalidValue", args[1]));
                return;
            }

            float multiplier = 1f - (pct / 100f);
            _manualOverrides[target.userID] = multiplier;
            _overrideAuditLog.Add(new OverrideAuditEntry
            {
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                AdminId = player.userID,
                AdminName = player.displayName,
                TargetId = target.userID,
                TargetName = target.displayName,
                OldValue = oldValue,
                NewValue = $"{pct}%"
            });
            SendReply(player, Msg(player, "OverrideSet", target.displayName, pct));
        }

        [ChatCommand("ac-chart")]
        void CmdAcChart(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }
            if (args.Length < 2) { SendReply(player, Msg(player, "ChartUsage")); return; }

            BasePlayer target = BasePlayer.Find(args[0]);
            if (target == null) { SendReply(player, Msg(player, "PlayerNotFound")); return; }

            string metric = args[1].Trim().ToLowerInvariant();
            string header = Msg(player, "ChartHeader", target.displayName, metric);

            if (metric == "accuracy")
            {
                Dictionary<string, WeaponData> weapons;
                if (!_playerStats.TryGetValue(target.userID, out weapons) || weapons.Count == 0)
                {
                    SendReply(player, $"{header}\n{Msg(player, "ChartNoData", target.displayName)}");
                    return;
                }

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"<color=#55ff55>{header}</color>");
                foreach (var wkv in weapons)
                {
                    var hist = wkv.Value.History;
                    if (hist.Count == 0) continue;

                    int segments = Math.Min(10, hist.Count);
                    int segSize = hist.Count / segments;
                    var bars = new List<float>();
                    for (int i = 0; i < segments; i++)
                    {
                        int start = i * segSize;
                        int end = (i == segments - 1) ? hist.Count : start + segSize;
                        int hits = 0;
                        for (int j = start; j < end; j++) if (hist[j].IsHit) hits++;
                        bars.Add((float)hits / (end - start));
                    }

                    sb.Append($"  {wkv.Key}: ");
                    foreach (float v in bars)
                    {
                        if (v >= 0.75f) sb.Append("█");
                        else if (v >= 0.50f) sb.Append("▓");
                        else if (v >= 0.25f) sb.Append("▒");
                        else sb.Append("░");
                    }
                    sb.AppendLine($" {wkv.Value.GetAccuracy():P0} ({hist.Count} shots)");
                }
                SendReply(player, sb.ToString());
            }
            else if (metric == "ping")
            {
                PlayerPingStats ps;
                if (!_playerPingStats.TryGetValue(target.userID, out ps) || ps.SampleCount == 0)
                {
                    SendReply(player, $"{header}\n{Msg(player, "ChartNoData", target.displayName)}");
                    return;
                }

                int barWidth = 20;
                int range = ps.Max - ps.Min;
                if (range <= 0) range = 1;
                int avgPos = (int)((ps.EMA - ps.Min) / range * barWidth);
                avgPos = Math.Max(0, Math.Min(barWidth - 1, avgPos));

                var bar = new char[barWidth];
                for (int i = 0; i < barWidth; i++) bar[i] = '─';
                bar[0] = '[';
                bar[barWidth - 1] = ']';
                if (avgPos > 0 && avgPos < barWidth - 1) bar[avgPos] = '▲';

                string sb2 = $"<color=#55ff55>{header}</color>\n"
                    + $"  Min: {ps.Min}ms  Avg: {ps.EMA:F0}ms  Max: {ps.Max}ms  StdDev: {ps.StdDev:F1}ms\n"
                    + $"  {new string(bar)}\n"
                    + $"  Samples: {ps.SampleCount}  Anomalies: {ps.AnomalyCount}";
                SendReply(player, sb2);
            }
            else if (metric == "kda")
            {
                PlayerKDAStats kda;
                if (!_playerKDAStats.TryGetValue(target.userID, out kda))
                {
                    SendReply(player, $"{header}\n{Msg(player, "ChartNoData", target.displayName)}");
                    return;
                }

                int maxVal = Math.Max(1, Math.Max(kda.Kills, Math.Max(kda.Deaths, kda.Assists)));
                int barMax = 15;
                string kBar = new string('█', (int)((float)kda.Kills / maxVal * barMax));
                string dBar = new string('█', (int)((float)kda.Deaths / maxVal * barMax));
                string aBar = new string('█', (int)((float)kda.Assists / maxVal * barMax));
                float kdr = kda.Deaths > 0 ? (float)kda.Kills / kda.Deaths : kda.Kills;

                string result = $"<color=#55ff55>{header}</color>\n"
                    + $"  K {kBar} {kda.Kills}\n"
                    + $"  D {dBar} {kda.Deaths}\n"
                    + $"  A {aBar} {kda.Assists}\n"
                    + $"  KDR: {kdr:F2}";
                SendReply(player, result);
            }
            else
            {
                SendReply(player, Msg(player, "ChartUsage"));
            }
        }

        [ChatCommand("ac-export")]
        void CmdAcExport(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }
            if (args.Length == 0 || args[0].Trim().ToLowerInvariant() != "csv") { SendReply(player, Msg(player, "ExportUsage")); return; }

            if (_playerStats.Count == 0) { SendReply(player, Msg(player, "ExportEmpty")); return; }

            var csv = new System.Text.StringBuilder();
            csv.AppendLine("player_id,weapon,accuracy,shots,hits,global_nerf,manual_override,kills,deaths,assists,ping_avg,ping_stddev,ping_anomalies,ls_incidents");

            int rows = 0;
            foreach (var kv in _playerStats)
            {
                ulong pid = kv.Key;
                float globalNerf = GetLowestNerf(pid);

                PlayerKDAStats kda;
                _playerKDAStats.TryGetValue(pid, out kda);
                int kills = kda != null ? kda.Kills : 0;
                int deaths = kda != null ? kda.Deaths : 0;
                int assists = kda != null ? kda.Assists : 0;

                PlayerPingStats ps;
                _playerPingStats.TryGetValue(pid, out ps);
                double pingAvg = ps != null ? ps.EMA : 0.0;
                double pingStdDev = ps != null ? ps.StdDev : 0.0;
                int anomalies = ps != null ? ps.AnomalyCount : 0;

                List<LagSwitchIncident> incidents;
                _lagswitchIncidents.TryGetValue(pid, out incidents);
                int lsCount = incidents != null ? incidents.Count : 0;

                float manualMult;
                string overrideStr = _manualOverrides.TryGetValue(pid, out manualMult)
                    ? $"{(1f - manualMult) * 100f:F0}"
                    : "";

                foreach (var wkv in kv.Value)
                {
                    int shots = wkv.Value.History.Count;
                    int hits = wkv.Value.History.Count(x => x.IsHit);
                    float acc = wkv.Value.GetAccuracy();
                    csv.AppendLine($"{pid},{wkv.Key},{acc:F4},{shots},{hits},{globalNerf:F4},{overrideStr},{kills},{deaths},{assists},{pingAvg:F1},{pingStdDev:F1},{anomalies},{lsCount}");
                    rows++;
                }
            }

            string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            string fileName = $"MogyAntiCheat_Export_{timestamp}.csv";
            string filePath = System.IO.Path.Combine(_runtimeDataDirectory, fileName);
            try
            {
                System.IO.File.WriteAllText(filePath, csv.ToString(), System.Text.Encoding.UTF8);
                SendReply(player, Msg(player, "ExportDone", filePath, rows));
            }
            catch (Exception ex)
            {
                SendReply(player, $"[MogyAC] Export failed: {ex.Message}");
            }
        }

        [ChatCommand("ac-config-tune")]
        void CmdAcConfigTune(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }
            if (args.Length < 2) { SendReply(player, Msg(player, "ConfigTuneUsage")); return; }

            string paramName = args[0].Trim();
            string valueStr = args[1].Trim();

            if (paramName.Equals("MissExpirySeconds", StringComparison.OrdinalIgnoreCase))
            {
                float val;
                if (!float.TryParse(valueStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out val) || val <= 0)
                {
                    SendReply(player, Msg(player, "ConfigTuneInvalidValue", paramName, valueStr));
                    return;
                }
                string old = Config["MissExpirySeconds"] != null ? Config["MissExpirySeconds"].ToString() : "?";
                Config["MissExpirySeconds"] = (double)val;
                SaveConfig();
                SendReply(player, Msg(player, "ConfigTuneUpdated", paramName, val, old));
            }
            else if (paramName.Equals("MaxHitDistance", StringComparison.OrdinalIgnoreCase))
            {
                float val;
                if (!float.TryParse(valueStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out val) || val < 0)
                {
                    SendReply(player, Msg(player, "ConfigTuneInvalidValue", paramName, valueStr));
                    return;
                }
                string old = Config["MaxHitDistance"] != null ? Config["MaxHitDistance"].ToString() : "?";
                Config["MaxHitDistance"] = (double)val;
                SaveConfig();
                SendReply(player, Msg(player, "ConfigTuneUpdated", paramName, val, old));
            }
            else if (paramName.Equals("LagswitchDetection.Threshold", StringComparison.OrdinalIgnoreCase))
            {
                float val;
                if (!float.TryParse(valueStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out val) || val < 0f || val > 1f)
                {
                    SendReply(player, Msg(player, "ConfigTuneInvalidValue", paramName, valueStr));
                    return;
                }
                var cfg = Config["LagswitchDetection"] as Dictionary<string, object>;
                if (cfg == null) { SendReply(player, Msg(player, "ConfigTuneInvalidParam", paramName)); return; }
                string old = cfg.ContainsKey("Threshold") ? cfg["Threshold"].ToString() : "?";
                cfg["Threshold"] = (double)val;
                SaveConfig();
                SendReply(player, Msg(player, "ConfigTuneUpdated", paramName, val, old));
            }
            else if (paramName.Equals("PingMonitoring.AnomalyThresholdStdDev", StringComparison.OrdinalIgnoreCase))
            {
                float val;
                if (!float.TryParse(valueStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out val) || val <= 0)
                {
                    SendReply(player, Msg(player, "ConfigTuneInvalidValue", paramName, valueStr));
                    return;
                }
                var cfg = Config["PingMonitoring"] as Dictionary<string, object>;
                if (cfg == null) { SendReply(player, Msg(player, "ConfigTuneInvalidParam", paramName)); return; }
                string old = cfg.ContainsKey("AnomalyThresholdStdDev") ? cfg["AnomalyThresholdStdDev"].ToString() : "?";
                cfg["AnomalyThresholdStdDev"] = (double)val;
                SaveConfig();
                SendReply(player, Msg(player, "ConfigTuneUpdated", paramName, val, old));
            }
            else
            {
                SendReply(player, Msg(player, "ConfigTuneInvalidParam", paramName));
            }
        }

        [ChatCommand("ac-suggest")]
        void CmdAcSuggest(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player, PermissionAdmin)) { SendReply(player, Msg(player, "NoPermission")); return; }
            if (!IsMLServiceEnabled() || string.IsNullOrEmpty(GetMLServiceEndpoint()))
            {
                SendReply(player, Msg(player, "SuggestNoService"));
                return;
            }

            SendReply(player, Msg(player, "SuggestFetching"));

            string endpoint = GetMLServiceEndpoint() + "/config-recommend";
            string token = GetMLServiceAuthToken();
            var headers = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(token))
                headers["Authorization"] = "Bearer " + token;

            ulong playerId = player.userID;
            webrequest.Enqueue(endpoint, null, (code, body) =>
            {
                BasePlayer admin = BasePlayer.FindByID(playerId);
                if (admin == null) return;

                if (code != 200 || string.IsNullOrEmpty(body))
                {
                    SendReply(admin, Msg(admin, "SuggestNoService"));
                    return;
                }

                try
                {
                    var root = JObject.Parse(body);
                    var recs = root["recommendations"] as JObject;
                    if (recs == null || !recs.HasValues)
                    {
                        SendReply(admin, Msg(admin, "SuggestNoChanges"));
                        return;
                    }

                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine($"<color=#55ff55>{Msg(admin, "SuggestHeader")}</color>");

                    foreach (var rec in recs)
                    {
                        var obj = rec.Value as JObject;
                        if (obj == null) continue;
                        string cur = obj["current"]?.ToString() ?? "?";
                        string recommended = obj["recommended"]?.ToString() ?? "?";
                        double conf = obj["confidence"] != null ? (double)obj["confidence"] : 0.0;
                        sb.AppendLine(Msg(admin, "SuggestRow", rec.Key, cur, recommended, conf));
                    }

                    SendReply(admin, sb.ToString());
                }
                catch
                {
                    SendReply(admin, Msg(admin, "SuggestNoService"));
                }
            }, this, Core.Libraries.RequestMethod.GET, headers);
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
            report += Msg(player, "HelpMLFeedback") + "\n";
            report += Msg(player, "HelpDashboard") + "\n";
            report += Msg(player, "HelpOverride") + "\n";
            report += Msg(player, "HelpChart") + "\n";
            report += Msg(player, "HelpExport") + "\n";
            report += Msg(player, "HelpConfigTune") + "\n";
            report += Msg(player, "HelpSuggest") + "\n";
            report += Msg(player, "HelpDailyNow") + "\n";
            report += Msg(player, "HelpHelp");
            SendReply(player, report);
        }
    }
}
