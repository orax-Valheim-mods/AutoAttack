using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static HarmonyLib.AccessTools;

namespace ToggleAutoAttack
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class ToggleAutoAttack : BaseUnityPlugin
    {
        // TODO: change the GUID to match your own naming convention (e.g. "yourname.valheim.toggleautoattack")
        public const string PluginGuid = "sopmehua.valheim.toggleautoattack";
        public const string PluginName = "ToggleAutoAttack";
        public const string PluginVersion = "1.2.0";

        public static ConfigEntry<KeyboardShortcut> ToggleKey;
        public static ConfigEntry<bool> ShowOnScreenMessage;

        internal static ToggleAutoAttack Instance;

        private static ManualLogSource _log;
        private Harmony _harmony;

        internal static void LogStatic(string message) => _log?.LogInfo(message);

        private void Awake()
        {
            Instance = this;
            _log = Logger;

            ToggleKey = Config.Bind(
                "General", "ToggleKey", new KeyboardShortcut(KeyCode.Mouse1, KeyCode.LeftAlt),
                "Key combination that starts/stops continuous attacking (equivalent to holding down the attack button).");

            ShowOnScreenMessage = Config.Bind(
                "General", "ShowOnScreenMessage", true,
                "Show a short on-screen message when continuous attack turns on, off, or gets cancelled.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void Update()
        {
            // Fallback in case ZInput.Initialize() somehow already ran before our Harmony patch
            // was applied: generate the per-button entries here too, it's a no-op once already done.
            if (DynamicButtonConfig.CancelEntries.Count == 0)
                DynamicButtonConfig.Generate(Config);

            if (Player.m_localPlayer == null)
            {
                AutoAttackState.Reset();
                return;
            }

            bool justToggled = false;

            // Minimap.InTextInput() avoids toggling while chatting.
            if (!Minimap.InTextInput() && ToggleKey.Value.IsDown())
            {
                bool active = AutoAttackState.Toggle();
                ShowMessage(active ? "Continuous attack: ON" : "Continuous attack: OFF");
                justToggled = true;
            }

            // Skip cancel check on the exact frame the toggle hotkey was pressed.
            if (!justToggled && AutoAttackState.Active && DynamicButtonConfig.AnyDown(DynamicButtonConfig.CancelEntries))
            {
                AutoAttackState.Reset();
                ShowMessage("Continuous attack: cancelled");
            }

            AutoAttackState.Paused = DynamicButtonConfig.AnyHeld(DynamicButtonConfig.PauseEntries);
        }

        internal void ShowMessage(string text)
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
    /// Generates one ConfigEntry&lt;bool&gt; per real Valheim input button - a checkbox under
    /// "CancelButtons" and one under "PauseButtons" - instead of a free-text comma-separated list.
    /// Button names are read via reflection from ZInput's private m_buttons dictionary rather than
    /// hardcoded, so the list always matches whatever this game version (or another mod) actually
    /// registers. Only Rebindable buttons are included, to filter out internal/system-only entries
    /// and keep the config to real player-facing actions.
    /// </summary>
    internal static class DynamicButtonConfig
    {
        public static readonly Dictionary<string, ConfigEntry<bool>> CancelEntries = new Dictionary<string, ConfigEntry<bool>>();
        public static readonly Dictionary<string, ConfigEntry<bool>> PauseEntries = new Dictionary<string, ConfigEntry<bool>>();

        private static readonly FieldRef<ZInput, Dictionary<string, ZInput.ButtonDef>> ButtonsRef =
            AccessTools.FieldRefAccess<ZInput, Dictionary<string, ZInput.ButtonDef>>("m_buttons");

        public static void Generate(ConfigFile config)
        {
            if (CancelEntries.Count > 0)
                return; // already generated

            ZInput instance = ZInput.instance;
            if (instance == null)
                return; // too early - ZInput not constructed yet

            IEnumerable<string> names = ButtonsRef.Invoke(instance).Values
                .Where(b => b.Rebindable) // keep only real player-facing actions
                .Select(b => b.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

            foreach (string name in names)
            {
                string label = DescribeButton(instance, name);

                bool defaultValue = name is "SecondaryAttack" or "Block" or "Use" or "Crouch" or "AltDodge" or "AutoRun" or "Sit";
                CancelEntries[name] = config.Bind(
                    "CancelButtons", name, defaultValue,
                    $"Pressing {label} fully cancels continuous attack.");

                defaultValue = name is "SecondaryAttack" or "Block" or "Forward" or "Left" or "Backward" or "Right";
                PauseEntries[name] = config.Bind(
                    "PauseButtons", name, defaultValue,
                    $"Holding {label} temporarily pauses continuous attack; it resumes automatically on release.");
            }

            ToggleAutoAttack.LogStatic($"Generated {CancelEntries.Count} cancel/pause button entries from ZInput.");
        }

        /// <summary>
        /// Builds a label like: "Attack" (Attaque, currently bound to: Mouse Left)
        /// - the technical ZInput name (used as the actual lookup key everywhere else),
        /// - its translation in the game's current UI language, via the same "$settings_" +
        ///   name.ToLower() key convention ZInput itself uses to build its own settings menu
        ///   labels (see ZInput.FormatAndAddString),
        /// - the key/button currently bound to it, via ZInput's own GetBoundKeyString().
        /// Both dynamic parts are looked up defensively: Localization may not be initialized yet
        /// at this point in the game's startup sequence, and this class's exact behavior on a
        /// missing translation couldn't be verified from the decompiled source available here.
        /// </summary>
        private static string DescribeButton(ZInput instance, string name)
        {
            string translated = null;
            try
            {
                if (Localization.instance != null)
                {
                    translated = Localization.instance.Localize("$settings_" + name.ToLower());
                }
            }
            catch (Exception e)
            {
                ToggleAutoAttack.LogStatic($"Could not localize button \"{name}\": {e.Message}");
            }

            string boundKey = null;
            try
            {
                boundKey = instance.GetBoundKeyString(name, emptyStringOnMissing: true);
                // GetBoundKeyString can return either an already-displayable string (from
                // InputBinding.ToDisplayString, for most keyboard keys) or a raw localization key
                // pulled from ZInput's own s_keyLocalizationMap (e.g. "$button_mouse0" for mouse
                // buttons). Localize() only replaces recognized "$xxx" tokens and leaves plain
                // text untouched, so running the result through it handles both cases correctly.
                if (!string.IsNullOrEmpty(boundKey) && Localization.instance != null)
                {
                    boundKey = Localization.instance.Localize(boundKey);
                }
            }
            catch (Exception e)
            {
                ToggleAutoAttack.LogStatic($"Could not read bound key for \"{name}\": {e.Message}");
            }

            bool hasTranslation = !string.IsNullOrEmpty(translated) && translated != name;
            bool hasBoundKey = !string.IsNullOrEmpty(boundKey);

            if (hasTranslation && hasBoundKey)
                return $"\"{name}\" ({translated}, currently bound to: {boundKey})";
            if (hasTranslation)
                return $"\"{name}\" ({translated})";
            if (hasBoundKey)
                return $"\"{name}\" (currently bound to: {boundKey})";
            return $"\"{name}\"";
        }

        public static bool AnyDown(Dictionary<string, ConfigEntry<bool>> entries)
        {
            foreach (KeyValuePair<string, ConfigEntry<bool>> pair in entries)
            {
                if (pair.Value.Value && ZInput.GetButtonDown(pair.Key))
                    return true;
            }
            return false;
        }

        public static bool AnyHeld(Dictionary<string, ConfigEntry<bool>> entries)
        {
            foreach (KeyValuePair<string, ConfigEntry<bool>> pair in entries)
            {
                if (pair.Value.Value && ZInput.GetButton(pair.Key))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Generates the per-button config entries as soon as ZInput itself is ready. See
    /// DynamicButtonConfig's doc comment for why this specific hook point was chosen.
    /// </summary>
    [HarmonyPatch(typeof(ZInput), nameof(ZInput.Initialize))]
    internal static class ZInput_Initialize_GenerateButtonConfig
    {
        private static void Postfix()
        {
            if (ToggleAutoAttack.Instance != null)
            {
                DynamicButtonConfig.Generate(ToggleAutoAttack.Instance.Config);
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
    /// exact button name "Attack". GetButtonDown/GetButtonUp - used by DynamicButtonConfig.AnyDown
    /// for cancellation - read a completely separate, independently-tracked state
    /// (ButtonDef.Pressed/Released vs Held in ZInput.cs), so a genuine manual click is always
    /// detected correctly regardless of this patch.
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
