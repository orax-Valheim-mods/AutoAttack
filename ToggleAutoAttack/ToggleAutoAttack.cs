using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
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

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<KeyboardShortcut> ToggleKey;
        public static ConfigEntry<bool> CancelOnWeaponChange;
        public static ConfigEntry<bool> ShowOnScreenMessage;
        public static ConfigEntry<MessageHud.MessageType> ScreenMessagePosition;

        internal static ToggleAutoAttack Instance;

        private static ManualLogSource _log;
        private Harmony _harmony;

        internal static void LogStatic(string message) => _log?.LogInfo(message);

        private void Awake()
        {
            Instance = this;
            _log = Logger;

            Enabled = Config.Bind(
                "General",
                "Enabled",
                true,
                "Master switch for the mod. When disabled, continuous attack is forced off and the game behaves exactly as vanilla (no toggle key, no pause/cancel buttons, no jump or weapon-swap adjustments)."
            );

            ToggleKey = Config.Bind(
                "General",
                "ToggleKey",
                new KeyboardShortcut(KeyCode.Mouse1, KeyCode.LeftAlt),
                "Key combination that starts/stops continuous attacking (equivalent to holding down the attack button)."
            );

            CancelOnWeaponChange = Config.Bind(
                "General",
                "CancelOnWeaponChange",
                true,
                "Cancel continuous attack if the equipped weapon changes while it is active (swapping to another weapon, unequipping, or the weapon being destroyed)."
            );

            ShowOnScreenMessage = Config.Bind(
                "HUD",
                "ShowOnScreenMessage",
                true,
                "Show a short on-screen message when continuous attack turns on, off, or gets cancelled."
            );

            ScreenMessagePosition = Config.Bind(
                "HUD",
                "ScreenMessagePosition",
                MessageHud.MessageType.TopLeft,
                "Position of the screen message."
            );

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void Update()
        {
            if (DynamicButtonConfig.CancelEntries.Count == 0)
                DynamicButtonConfig.Generate(Config);

            // Master switch. AutoAttackState.Active is the single gate every Harmony patch
            // checks (forced Attack hold, jump bypass, and all four weapon-swap guards), so
            // resetting it here turns the whole mod inert: no toggle key, no pause/cancel, no
            // weapon-change detection, and vanilla behavior everywhere else. Reset() also
            // clears ActiveWeapon, so re-enabling later starts from a fresh baseline.
            if (!Enabled.Value)
            {
                AutoAttackState.Reset();
                return;
            }

            if (Player.m_localPlayer == null)
            {
                AutoAttackState.Reset();
                return;
            }

            // Evaluate pause state at the beginning of Update
            AutoAttackState.Paused = DynamicButtonConfig.AnyHeld(DynamicButtonConfig.PauseEntries);

            bool justToggled = false;

            if (!Minimap.InTextInput() && ToggleKey.Value.IsDown())
            {
                bool active = AutoAttackState.Toggle();
                ShowMessage(active ? "Continuous attack: ON" : "Continuous attack: OFF");
                justToggled = true;
            }

            if (
                !justToggled
                && AutoAttackState.Active
                && DynamicButtonConfig.AnyDown(DynamicButtonConfig.CancelEntries)
            )
            {
                AutoAttackState.Reset();
                ShowMessage("Continuous attack: cancelled");
            }

            // Safety: cancel if the weapon being attacked with changed since the toggle was
            // turned on. Comparing ItemData references (not names or uids): swapping, unequipping
            // or losing the weapon replaces Humanoid.m_rightItem/GetCurrentWeapon()'s result with
            // a different instance, while merely moving the same item around in the inventory
            // keeps the same instance and therefore does not cancel.
            if (
                !justToggled
                && AutoAttackState.Active
                && CancelOnWeaponChange.Value
                && !ReferenceEquals(
                    Player.m_localPlayer.GetCurrentWeapon(),
                    AutoAttackState.ActiveWeapon
                )
            )
            {
                AutoAttackState.Reset();
                ShowMessage("Continuous attack: cancelled (weapon changed)");
            }
        }

        internal void ShowMessage(string text)
        {
            if (ShowOnScreenMessage.Value && MessageHud.instance != null)
            {
                MessageHud.instance.ShowMessage(ScreenMessagePosition.Value, text);
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
        public static readonly Dictionary<string, ConfigEntry<bool>> CancelEntries =
            new Dictionary<string, ConfigEntry<bool>>();
        public static readonly Dictionary<string, ConfigEntry<bool>> PauseEntries =
            new Dictionary<string, ConfigEntry<bool>>();

        private static readonly FieldRef<ZInput, Dictionary<string, ZInput.ButtonDef>> ButtonsRef =
            AccessTools.FieldRefAccess<ZInput, Dictionary<string, ZInput.ButtonDef>>("m_buttons");

        public static void Generate(ConfigFile config)
        {
            if (CancelEntries.Count > 0)
                return; // already generated

            ZInput instance = ZInput.instance;
            if (instance == null)
                return; // too early - ZInput not constructed yet

            IEnumerable<string> names = ButtonsRef
                .Invoke(instance)
                .Values.Where(b => b.Rebindable) // keep only real player-facing actions
                .Select(b => b.Name)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

            foreach (string name in names)
            {
                string label = DescribeButton(instance, name);

                bool cancelDefaultValue =
                    name
                    is "Attack"
                        or "SecondaryAttack"
                        or "Block"
                        or "Use"
                        or "Crouch"
                        or "AltDodge"
                        or "AutoRun"
                        or "Sit";

                CancelEntries[name] = config.Bind(
                    "Cancel buttons",
                    name,
                    cancelDefaultValue,
                    $"Pressing {label} fully cancels continuous attack."
                );

                // "Attack" is deliberately never a pause button: the toggle itself fakes a held
                // Attack through the ZInput.GetButton patch, so AnyHeld would read back the
                // forced value while evaluating the pause state. That makes Paused depend on
                // its own previous-frame value and the toggle would flicker on/off every frame.
                if (name == "Attack")
                    continue;

                bool pauseDefaultValue =
                    name
                    is "SecondaryAttack"
                        or "Jump"
                        or "Block"
                        or "Forward"
                        or "Left"
                        or "Backward"
                        or "Right";

                PauseEntries[name] = config.Bind(
                    "Pause buttons",
                    name,
                    pauseDefaultValue,
                    $"Holding {label} temporarily pauses continuous attack; it resumes automatically on release."
                );
            }

            ToggleAutoAttack.LogStatic(
                $"Generated {CancelEntries.Count} cancel/pause button entries from ZInput."
            );
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

        /// <summary>
        /// The weapon equipped at the moment the toggle was turned on. CancelOnWeaponChange
        /// compares the currently equipped weapon against this reference every Update and
        /// cancels the toggle if it no longer matches.
        /// </summary>
        public static ItemDrop.ItemData ActiveWeapon { get; private set; }

        public static bool Toggle()
        {
            Active = !Active;
            Paused = false;
            ActiveWeapon = Active ? Player.m_localPlayer?.GetCurrentWeapon() : null;
            return Active;
        }

        public static void Reset()
        {
            Active = false;
            Paused = false;
            ActiveWeapon = null;
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
    /// Lets the local player's jump through while the toggle is on.
    ///
    /// Why this is needed: Character.Jump refuses to jump while InAttack() unless force is set
    /// (Character.cs: "force || !InAttack()"). With the toggle faking a held Attack, the swing
    /// animation is restarted every FixedUpdate (Player.cs: m_attackHold feeds StartAttack), so
    /// InAttack() - the animator's "attack" tag - is true almost always. Meanwhile the jump edge
    /// is computed exactly once (PlayerController.FixedUpdate: jump = button &amp;&amp; !m_lastJump)
    /// and is never retried: the press gets swallowed and the player never jumps.
    ///
    /// The "Jump" pause button only stops the *fake hold*; the swing already in progress keeps its
    /// attack animator tag until the animation ends, which is long after the one-frame edge. So we
    /// force the jump through instead - the same force path the vanilla game itself uses
    /// (CharacterAnimEvent calls Jump(force: true) from animation events).
    ///
    /// Scope: local player only, toggle Active (paused counts as active - Reset() is not called),
    /// and Jump/JoyJump must actually be held, so vanilla behavior is untouched while the toggle
    /// is off. Only the !InAttack() check is bypassed; grounded/dead/encumbered/dodge checks in
    /// Character.Jump still apply.
    /// </summary>
    [HarmonyPatch(typeof(Character), nameof(Character.Jump), new Type[] { typeof(bool) })]
    internal static class Character_Jump_AllowWhileAutoAttack
    {
        private static void Prefix(Character __instance, ref bool force)
        {
            if (
                !force
                && AutoAttackState.Active
                && __instance == Player.m_localPlayer
                && (ZInput.GetButton("Jump") || ZInput.GetButton("JoyJump"))
            )
            {
                force = true;
            }
        }
    }

    /// <summary>
    /// Lets the player change weapon while the continuous-attack toggle is on.
    ///
    /// Why this is needed: vanilla refuses to swap equipment during a swing - three guards call
    /// InAttack() (the animator's "attack" tag):
    ///   1. Player.ToggleEquipped (hotbar 1-8 path: Player.Update -> UseHotbarItem ->
    ///      Humanoid.UseItem -> ToggleEquipped) returns "handled" without doing anything, so the
    ///      keypress is swallowed silently - the weapon simply never gets selected.
    ///   2. Humanoid.EquipItem returns false (blocks the hotbar path too, plus inventory
    ///      drag & drop, which calls EquipItem directly).
    ///   3. Player.UpdateActionQueue freezes queued equip/unequip actions (items with
    ///      m_equipDuration > 0), which never reach completion between swings that the fake
    ///      held Attack restarts every FixedUpdate.
    /// With the toggle faking a held Attack, InAttack() is true almost always, so all three
    /// guards make weapon swapping look completely dead.
    ///
    /// How: the three patches below transpile those methods, replacing the InAttack() call with
    /// the helpers here. Stack shape is unchanged (the receiver was already pushed for the
    /// original callvirt, one bool comes back) and every other check in those methods - InDodge,
    /// dead, swimming, durability, DLC, ... - stays intact. Outside the toggle (or for any other
    /// character) the helpers fall through to the real InAttack(), so vanilla behavior is
    /// untouched.
    /// </summary>
    internal static class EquipDuringAutoAttack
    {
        private static readonly MethodInfo BlocksEquipMethod =
            AccessTools.Method(typeof(EquipDuringAutoAttack), nameof(BlocksEquip));

        private static readonly MethodInfo BlocksQueuedActionMethod =
            AccessTools.Method(typeof(EquipDuringAutoAttack), nameof(BlocksQueuedAction));

        private static readonly FieldRef<Player, List<Player.MinorActionData>> ActionQueueRef =
            FieldRefAccess<Player, List<Player.MinorActionData>>("m_actionQueue");

        /// <summary>
        /// True only for the local player while the toggle is active (paused counts as active -
        /// Reset() is not called when pausing, and the residual swing blocks swaps either way).
        /// </summary>
        private static bool BypassEnabled(Humanoid self)
        {
            return AutoAttackState.Active && self == Player.m_localPlayer;
        }

        /// <summary>Replaces InAttack() in Player.ToggleEquipped and Humanoid.EquipItem.</summary>
        public static bool BlocksEquip(Humanoid self)
        {
            return !BypassEnabled(self) && self.InAttack();
        }

        /// <summary>
        /// Replaces InAttack() in Player.UpdateActionQueue. Only Equip/Unequip heads are
        /// bypassed so their progress bar advances during auto-attack; other queued actions -

        /// notably Reload for crossbows - keep the vanilla "not while attacking" rule.
        /// </summary>
        public static bool BlocksQueuedAction(Humanoid self)
        {
            if (BypassEnabled(self))
            {
                List<Player.MinorActionData> queue = ActionQueueRef((Player)self);
                if (queue.Count > 0)
                {
                    Player.MinorActionData.ActionType type = queue[0].m_type;
                    if (
                        type == Player.MinorActionData.ActionType.Equip
                        || type == Player.MinorActionData.ActionType.Unequip
                    )
                    {
                        return false;
                    }
                }
            }
            return self.InAttack();
        }

        public static IEnumerable<CodeInstruction> TranspileBlocksEquip(
            IEnumerable<CodeInstruction> instructions
        )
        {
            return ReplaceInAttackCall(instructions, BlocksEquipMethod);
        }

        public static IEnumerable<CodeInstruction> TranspileBlocksQueuedAction(
            IEnumerable<CodeInstruction> instructions
        )
        {
            return ReplaceInAttackCall(instructions, BlocksQueuedActionMethod);
        }

        /// <summary>
        /// Swaps every InAttack() call (callvirt or call, operand may be declared on either
        /// Humanoid or Character) for the given static helper taking the receiver as its single
        /// parameter: identical stack shape, mutated in place so branch labels are preserved.
        /// Each patched method contains exactly one such call.
        /// </summary>
        private static IEnumerable<CodeInstruction> ReplaceInAttackCall(
            IEnumerable<CodeInstruction> instructions,
            MethodInfo replacement
        )
        {
            foreach (CodeInstruction instruction in instructions)
            {
                if (
                    (instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call)
                    && instruction.operand is MethodInfo target
                    && target.Name == nameof(Character.InAttack)
                )
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = replacement;
                }
                yield return instruction;
            }
        }
    }

    /// <summary>Guard 1: hotbar 1-8 presses were swallowed silently while InAttack().</summary>
    [HarmonyPatch(typeof(Player), "ToggleEquipped")]
    internal static class Player_ToggleEquipped_AllowSwapDuringAutoAttack
    {
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions
        )
        {
            return EquipDuringAutoAttack.TranspileBlocksEquip(instructions);
        }
    }

    /// <summary>
    /// Guard 2: EquipItem refused the swap (hotbar path and inventory drag &amp; drop both end
    /// up here). InDodge() is left untouched - the toggle never fakes a dodge.
    /// </summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipItem))]
    internal static class Humanoid_EquipItem_AllowSwapDuringAutoAttack
    {
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions
        )
        {
            return EquipDuringAutoAttack.TranspileBlocksEquip(instructions);
        }
    }

    /// <summary>
    /// Guard 3: queued equip/unequip actions for items with m_equipDuration &gt; 0 never
    /// progressed because the action queue returns early while InAttack().
    /// </summary>
    [HarmonyPatch(typeof(Player), "UpdateActionQueue")]
    internal static class Player_UpdateActionQueue_AllowQueuedSwapDuringAutoAttack
    {
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions
        )
        {
            return EquipDuringAutoAttack.TranspileBlocksQueuedAction(instructions);
        }
    }

    /// <summary>
    /// Guard 4: the "Hide/show weapon" hotkey (ZInput button "Hide", handled in Player.Update)
    /// only calls HideHandItems() when !InAttack() &amp;&amp; !InDodge(). With the toggle faking a
    /// held Attack, InAttack() is true almost always, so pressing the key did nothing except in
    /// the rare one-frame gap between two swings - the user had to press repeatedly before the
    /// weapon actually got hidden. That hide is also what flips GetCurrentWeapon() and lets
    /// CancelOnWeaponChange cancel the toggle, hence "several presses to cancel".
    ///
    /// InDodge() stays untouched (the toggle never fakes a dodge). The show path
    /// (ShowHandItems -> EquipItem) was already unlocked by the EquipItem patch above.
    /// Player.Update contains exactly one InAttack() call (verified: lines 838-1019), so
    /// replacing it via the shared helper is precise. JoyHide shares the same block, so the
    /// gamepad path is covered too.
    /// </summary>
    [HarmonyPatch(typeof(Player), "Update")]
    internal static class Player_Update_AllowHideDuringAutoAttack
    {
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions
        )
        {
            return EquipDuringAutoAttack.TranspileBlocksEquip(instructions);
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
