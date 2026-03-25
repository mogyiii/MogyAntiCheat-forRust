using Oxide.Core.Plugins;
using Oxide.Plugins;
using System.Collections.Generic;

namespace Oxide.Plugins
{
    [Info("MogyAcExampleSubscriber", "Mogy", "0.1.0")]
    [Description("Example external plugin that consumes MogyAntiCheat public API hooks.")]
    public class MogyAcExampleSubscriber : RustPlugin
    {
        [PluginReference] private Plugin MogyAntiCheat;

        private void OnMogyAcSuspicion(Dictionary<string, object> payload)
        {
            if (payload == null) return;

            Puts($"[MogyAC API] Suspicion event: {payload["playerId"]} with {payload["weaponShortName"]}, suggested nerf {payload["suggestedNerf"]}");
        }

        private void OnMogyAcPenaltyApplied(Dictionary<string, object> payload)
        {
            if (payload == null) return;

            Puts($"[MogyAC API] Penalty event: attacker {payload["attackerId"]}, x{payload["appliedMultiplier"]}");
        }

        [ChatCommand("ac-api-check")]
        private void CmdAcApiCheck(BasePlayer player, string command, string[] args)
        {
            if (player == null || !player.IsAdmin) return;
            if (MogyAntiCheat == null)
            {
                SendReply(player, "MogyAntiCheat plugin not found.");
                return;
            }

            var version = MogyAntiCheat.Call("GetApiVersion");
            var state = MogyAntiCheat.Call("GetPlayerAcState", player.userID) as Dictionary<string, object>;

            SendReply(player, $"MogyAC API version: {version}");
            SendReply(player, state == null ? "No AC state found for this player." : "AC state fetched successfully.");
        }
    }
}
