using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using UnityEngine;

namespace ToggleAutoAttack
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class ToggleAutoAttack : BaseUnityPlugin
    {
        public const string PluginGuid = "orax.ToggleAutoAttack";
        public const string PluginName = "ToggleAutoAttack";
        public const string PluginVersion = "0.1.0";

        public static ConfigEntry<KeyboardShortcut> ToggleKey;
        public static ConfigEntry<bool> ShowOnScreenMessage;

        private static ManualLogSource _log;
        private Harmony _harmony;

        internal static void LogStatic(string message) => _log?.LogInfo(message);

        private void Awake()
        {
            _log = Logger;

            ToggleKey = Config.Bind(
                "General", "ToggleKey", new KeyboardShortcut(KeyCode.X, KeyCode.LeftAlt),
                "Key combination that starts/stops continuous attacking (equivalent to holding down the attack button).");

            ShowOnScreenMessage = Config.Bind(
                "General", "ShowOnScreenMessage", true,
                "Show a short on-screen message when continuous attack is turned on or off.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void Update()
        {
            if (Player.m_localPlayer == null)
            {
                AutoAttackState.Reset();
                return;
            }

            // Avoids toggling while chatting.
            // Minimap.InTextInput() is a general-purpose "is the player currently typing in a
            // text field" check (chat, name inputs, etc.).
            if (Minimap.InTextInput())
                return;

            if (!ToggleKey.Value.IsDown())
                return;

            bool active = AutoAttackState.Toggle();
            if (ShowOnScreenMessage.Value && MessageHud.instance != null)
            {
                MessageHud.instance.ShowMessage(
                    MessageHud.MessageType.Center,
                    active ? "Continuous attack: ON" : "Continuous attack: OFF");
            }

        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    /// <summary>
    /// Holds the current on/off state of the continuous-attack toggle.
    /// </summary>
    internal static class AutoAttackState
    {
        public static bool Active { get; private set; }

        public static bool Toggle()
        {
            Active = !Active;
            return Active;
        }

        public static void Reset()
        {
            Active = false;
        }
    }

    /// <summary>
    /// Makes ZInput.GetButton("Attack") report "held down" while the toggle is active, so the
    /// game behaves exactly as if the player were physically holding the attack button - nothing
    /// else needs to change.
    ///
    /// Scope: only ZInput.GetButton (the held/continuous query) is patched, and only for the
    /// exact button name "Attack". Across the decompiled source, GetButton("Attack") (as opposed
    /// to GetButtonDown, an edge-triggered query used elsewhere for piece placement and the
    /// camera) is read in exactly one place: PlayerController.FixedUpdate, which already filters
    /// out inventory/menu contexts before acting on it - so those safeguards still apply
    /// automatically to the spoofed value.
    /// </summary>
    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButton), new Type[] { typeof(string) })]
    internal static class ZInput_GetButton_ForceAttackHold
    {
        private static void Postfix(string name, ref bool __result)
        {
            if (__result || !AutoAttackState.Active)
                return; // already true, or toggle inactive: nothing to do

            if (name == "Attack")
            {
                __result = true;
            }
        }
    }

    /// <summary>
    /// Safety net: turns the toggle off automatically when the local player dies, so continuous
    /// attack doesn't stay silently active through a respawn.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
    internal static class Player_OnDeath_ResetAutoAttack
    {
        private static void Postfix(Player __instance)
        {
            if (__instance == Player.m_localPlayer)
            {
                AutoAttackState.Reset();
            }
        }
    }
}
