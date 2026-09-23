using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ToggleAutoAttack
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class ToggleAutoAttack : BaseUnityPlugin
    {
        // TODO: change the GUID to match your own naming convention (e.g. "yourname.valheim.toggleautoattack")
        public const string PluginGuid = "sopmehua.valheim.toggleautoattack";
        public const string PluginName = "ToggleAutoAttack";
        public const string PluginVersion = "1.1.0";

        public static ConfigEntry<KeyboardShortcut> ToggleKey;
        public static ConfigEntry<bool> ShowOnScreenMessage;
        public static ConfigEntry<string> CancelButtons;
        public static ConfigEntry<string> PauseButtons;

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
                "Show a short on-screen message when continuous attack turns on, off, or gets cancelled.");

            CancelButtons = Config.Bind(
                "General", "CancelButtons", "Attack",
                "Comma-separated list of Valheim input button names (same names used by the game's own " +
                "control bindings, e.g. Attack, Jump, Dodge, Block...). Pressing any of them fully cancels " +
                "continuous attack. Uses the player's current key bindings automatically, including rebinds.");

            PauseButtons = Config.Bind(
                "General", "PauseButtons", "Forward",
                "Comma-separated list of Valheim input button names. Holding any of them temporarily pauses " +
                "continuous attack; it resumes automatically on release (e.g. Forward).");

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

            // Minimap.InTextInput() avoids toggling while chatting.
            if (!Minimap.InTextInput() && ToggleKey.Value.IsDown())
            {
                bool active = AutoAttackState.Toggle();
                ShowMessage(active ? "Continuous attack: ON" : "Continuous attack: OFF");
            }

            if (AutoAttackState.Active && ButtonList.AnyDown(CancelButtons.Value))
            {
                AutoAttackState.Reset();
                ShowMessage("Continuous attack: cancelled");
            }

            AutoAttackState.Paused = ButtonList.AnyHeld(PauseButtons.Value);
        }

        private void ShowMessage(string text)
        {
            if (ShowOnScreenMessage.Value && MessageHud.instance != null)
            {
                MessageHud.instance.ShowMessage(MessageHud.MessageType.Center, text);
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }
    }

    /// <summary>
    /// Parses and checks comma-separated lists of ZInput button names (e.g. "Attack,Jump").
    /// Using ZInput's own button names rather than raw KeyCodes means these automatically track
    /// the player's current key bindings, including rebinds done in the game's own options.
    /// Unknown/misspelled names are silently ignored by ZInput itself (TryGetButtonState just
    /// falls through to false), so a typo here won't throw - it just won't match anything.
    /// </summary>
    internal static class ButtonList
    {
        public static bool AnyDown(string namesCsv)
        {
            foreach (string name in Split(namesCsv))
            {
                if (ZInput.GetButtonDown(name))
                    return true;
            }
            return false;
        }

        public static bool AnyHeld(string namesCsv)
        {
            foreach (string name in Split(namesCsv))
            {
                if (ZInput.GetButton(name))
                    return true;
            }
            return false;
        }

        private static IEnumerable<string> Split(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                yield break;

            foreach (string part in csv.Split(','))
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0)
                    yield return trimmed;
            }
        }
    }

    /// <summary>
    /// Holds the current on/off/paused state of the continuous-attack toggle.
    /// </summary>
    internal static class AutoAttackState
    {
        public static bool Active { get; private set; }
        public static bool Paused { get; set; }

        public static bool Toggle()
        {
            Active = !Active;
            Paused = false;
            return Active;
        }

        public static void Reset()
        {
            Active = false;
            Paused = false;
        }
    }

    /// <summary>
    /// Makes ZInput.GetButton("Attack") report "held down" while the toggle is active and not
    /// paused, so the game behaves exactly as if the player were physically holding the attack
    /// button - nothing else needs to change.
    ///
    /// Scope: only ZInput.GetButton (the held/continuous query) is patched, and only for the
    /// exact button name "Attack". GetButtonDown/GetButtonUp - used by ButtonList.AnyDown for
    /// cancellation, including the default "Attack" cancel entry - read a completely separate,
    /// independently-tracked state (ButtonDef.Pressed/Released vs Held in ZInput.cs), so a
    /// genuine manual click is always detected correctly regardless of this patch.
    /// </summary>
    [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButton), new Type[] { typeof(string) })]
    internal static class ZInput_GetButton_ForceAttackHold
    {
        private static void Postfix(string name, ref bool __result)
        {
            if (__result || !AutoAttackState.Active || AutoAttackState.Paused)
                return; // already true, toggle inactive, or currently paused: nothing to do

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
