using Oxide.Plugins;
using System;
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("MogyAcZeroDamageAlert", "Mogy", "1.0.0")]
    [Description("Server alert when MogyAntiCheat reduces a player's outgoing damage to 0%.")]
    public class MogyAcZeroDamageAlert : RustPlugin
    {
        private const float ZeroThreshold = 0.0001f;
        private const float AlertCooldownSeconds = 60f;

        private readonly Dictionary<ulong, DateTime> _lastAlertByAttacker = new Dictionary<ulong, DateTime>();

        private void OnMogyAcPenaltyApplied(Dictionary<string, object> payload)
        {
            if (payload == null) return;

            float appliedMultiplier = ReadFloat(payload, "appliedMultiplier");
            float scaledDamage = ReadFloat(payload, "scaledDamage");

            bool isZeroDamage = appliedMultiplier <= ZeroThreshold || scaledDamage <= ZeroThreshold;
            if (!isZeroDamage) return;

            ulong attackerId = ReadUlong(payload, "attackerId");
            if (IsOnCooldown(attackerId)) return;

            string attackerName = ResolvePlayerName(attackerId);
            string weaponName = ReadString(payload, "weaponShortName");

            PrintToChat($"<color=#ff4d4d>[MogyAC]</color> Figyelem: <color=#ffd966>{attackerName}</color> sebzese 0%-ra csokkent ({weaponName}). Csalogyanus jatekos a szerveren!");
        }

        private bool IsOnCooldown(ulong attackerId)
        {
            if (attackerId == 0) return false;

            DateTime now = DateTime.UtcNow;
            DateTime lastAlert;
            if (_lastAlertByAttacker.TryGetValue(attackerId, out lastAlert))
            {
                if ((now - lastAlert).TotalSeconds < AlertCooldownSeconds)
                {
                    return true;
                }
            }

            _lastAlertByAttacker[attackerId] = now;
            return false;
        }

        private string ResolvePlayerName(ulong playerId)
        {
            if (playerId == 0) return "Ismeretlen";

            BasePlayer player = BasePlayer.FindByID(playerId) ?? BasePlayer.FindSleeping(playerId);
            if (player == null || string.IsNullOrEmpty(player.displayName))
            {
                return playerId.ToString();
            }

            return player.displayName;
        }

        private static float ReadFloat(Dictionary<string, object> payload, string key)
        {
            object value;
            if (!payload.TryGetValue(key, out value) || value == null)
            {
                return 0f;
            }

            try
            {
                return Convert.ToSingle(value);
            }
            catch
            {
                return 0f;
            }
        }

        private static ulong ReadUlong(Dictionary<string, object> payload, string key)
        {
            object value;
            if (!payload.TryGetValue(key, out value) || value == null)
            {
                return 0;
            }

            try
            {
                return Convert.ToUInt64(value);
            }
            catch
            {
                return 0;
            }
        }

        private static string ReadString(Dictionary<string, object> payload, string key)
        {
            object value;
            if (!payload.TryGetValue(key, out value) || value == null)
            {
                return "ismeretlen fegyver";
            }

            string result = value.ToString();
            return string.IsNullOrEmpty(result) ? "ismeretlen fegyver" : result;
        }
    }
}
