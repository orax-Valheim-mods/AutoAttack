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

namespace ToggleAutoAttack;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class ToggleAutoAttack : BaseUnityPlugin
{
    public const string PluginGuid = "orax.ToggleAutoAttack";
    public const string PluginName = "ToggleAutoAttack";
    public const string PluginVersion = "0.1.0";

    public static ConfigEntry<bool> Enabled;
    public static ConfigEntry<KeyboardShortcut> ToggleKey;
    public static ConfigEntry<string> GamepadToggleButtons;
    public static ConfigEntry<bool> CancelOnWeaponChange;
    public static ConfigEntry<bool> ShowOnScreenMessage;
    public static ConfigEntry<MessageHud.MessageType> ScreenMessagePosition;
    public static ConfigEntry<string> MessageOn;
    public static ConfigEntry<string> MessageOff;
    public static ConfigEntry<string> MessageCancelled;
    public static ConfigEntry<string> MessageWeaponChanged;

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
            "Keyboard",
            "Toggle auto-attack key",
            new KeyboardShortcut(KeyCode.Mouse1, KeyCode.LeftAlt),
            "Keyboard key combination that starts/stops continuous attacking (equivalent to holding down the attack button)."
        );

        GamepadToggleButtons = Config.Bind(
            "Gamepad",
            "Toggle auto-attack buttons",
            "JoyLBumper, JoyRBumper",
            "Gamepad buttons that must all be held together to start/stop continuous attacking, as comma or plus separated ZInput button names (JoyLBumper, JoyRBumper = L1 + R1 by default). The combo fires on the press that completes it, like the keyboard combination. Leave empty to disable the gamepad toggle."
        );

        CancelOnWeaponChange = Config.Bind(
            "General",
            "Cancel on weapon change",
            true,
            "Cancel continuous attack if the equipped weapon changes while it is active (swapping to another weapon, unequipping, or the weapon being destroyed)."
        );

        ShowOnScreenMessage = Config.Bind(
            "HUD",
            "Show on screen message",
            true,
            "Show a short on-screen message when continuous attack turns on, off, or gets cancelled."
        );

        ScreenMessagePosition = Config.Bind(
            "HUD",
            "Screen message position",
            MessageHud.MessageType.TopLeft,
            "Position of the screen message."
        );

        MessageOn = Config.Bind(
            "Messages",
            "Turned on",
            "Continuous attack: ON",
            "Text shown when the toggle turns on. Leave empty to hide this message."
        );

        MessageOff = Config.Bind(
            "Messages",
            "Turned off",
            "Continuous attack: OFF",
            "Text shown when the toggle turns off. Leave empty to hide this message."
        );

        MessageCancelled = Config.Bind(
            "Messages",
            "Cancelled",
            "Continuous attack: cancelled",
            "Text shown when a cancel button stops continuous attack. Leave empty to hide this message."
        );

        MessageWeaponChanged = Config.Bind(
            "Messages",
            "Cancelled (weapon changed)",
            "Continuous attack: cancelled (weapon changed)",
            "Text shown when continuous attack stops because the equipped weapon changed (only when \"Cancel on weapon change\" is enabled). Leave empty to hide this message."
        );

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll();

        Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
    }

    private void Update()
    {
        if (DynamicButtonConfig.CancelEntries.Count == 0)
            DynamicButtonConfig.Generate(Config);

        // Parsed and edge-tracked on every frame - even while the toggle is disabled or no
        // player is loaded - so releases are observed too and the press edge never goes stale
        // (e.g. when the combo is completed and released while typing in chat).
        bool gamepadToggleDown = GamepadToggleDown();

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

        if (
            !Minimap.InTextInput()
            && (ToggleKey.Value.IsDown() || gamepadToggleDown)
        )
        {
            bool active = AutoAttackState.Toggle();
            ShowMessage(active ? MessageOn.Value : MessageOff.Value);
            justToggled = true;
        }

        if (
            !justToggled
            && AutoAttackState.Active
            && DynamicButtonConfig.AnyDown(DynamicButtonConfig.CancelEntries)
        )
        {
            AutoAttackState.Reset();
            ShowMessage(MessageCancelled.Value);
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
            ShowMessage(MessageWeaponChanged.Value);
        }
    }

    internal void ShowMessage(string text)
    {
        if (string.IsNullOrEmpty(text))
            return; // empty config value: this specific message is disabled
        if (ShowOnScreenMessage.Value && MessageHud.instance != null)
        {
            MessageHud.instance.ShowMessage(ScreenMessagePosition.Value, text);
        }
    }

    private static string[] _gamepadChord = new string[0];
    private static string _gamepadChordRaw;
    private static bool _gamepadChordHeldLastFrame;
    private static bool _gamepadChordValidated;

    /// <summary>
    /// Gamepad twin of ToggleKey: fires once when every button of the configured combo is
    /// held together - the same "press completes the combo" edge as KeyboardShortcut.IsDown,
    /// tracked here because ZInput has no combo concept of its own.
    ///
    /// Runs on every Update regardless of Enabled/player state so the held state (and thus
    /// the release that re-arms the edge) is always observed; the caller decides whether the
    /// resulting press counts.
    ///
    /// Default JoyLBumper + JoyRBumper (L1 + R1), audited against the game's own use of the
    /// raw buttons: L3+R3 is the guardian-power chord (Player.Update flag6), R3 hides the
    /// weapon (which would trip CancelOnWeaponChange right after toggling), Start/Select open
    /// menu/map, and the triggers are the game's modifier prefix (console, connect panel) -
    /// while the bumpers only have hold-style actions plus one harmless secondary-attack
    /// edge. ZInput.GetButton returns false for unknown names, so a typo can only disable
    /// the combo; that case is reported once by the validation below.
    /// </summary>
    private bool GamepadToggleDown()
    {
        string raw = GamepadToggleButtons.Value;
        if (!string.Equals(raw, _gamepadChordRaw, StringComparison.Ordinal))
        {
            _gamepadChordRaw = raw;
            _gamepadChord = (raw ?? string.Empty)
                .Split(new[] { ',', '+' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToArray();
            _gamepadChordHeldLastFrame = false;
            _gamepadChordValidated = false;
        }

        bool allHeld = _gamepadChord.Length > 0;
        for (int i = 0; allHeld && i < _gamepadChord.Length; i++)
        {
            allHeld = ZInput.GetButton(_gamepadChord[i]);
        }

        bool pressed = allHeld && !_gamepadChordHeldLastFrame;
        _gamepadChordHeldLastFrame = allHeld;

        // Validate only once the button registry is known to be populated (DynamicButtonConfig
        // having generated entries proves it): an unknown name would silently never complete
        // the combo, so at least say so in the log.
        if (!_gamepadChordValidated && DynamicButtonConfig.CancelEntries.Count > 0)
        {
            _gamepadChordValidated = true;
            foreach (string button in _gamepadChord)
            {
                if (!DynamicButtonConfig.IsRegisteredButton(button))
                {
                    LogStatic(
                        $"Gamepad toggle: ZInput has no button named \"{button}\" - it can never complete the combo. Check [Gamepad] Toggle auto-attack buttons."
                    );
                }
            }
        }

        return pressed;
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }
}

/// <summary>
/// Generates one ConfigEntry&lt;bool&gt; per real Valheim input button, split into
/// device-specific sections - "Keyboard cancel buttons"/"Keyboard pause buttons" for
/// keyboard/mouse and "Gamepad cancel buttons"/"Gamepad pause buttons" for the controller -
/// instead of a free-text comma-separated list.
///
/// Device split uses ButtonDef.Source (the same classification ClearGamepadButtons uses,
/// derived from the binding path containing "Gamepad"). Keyboard/mouse buttons are filtered
/// by ZInput's own Rebindable flag; gamepad buttons can't be - Valheim registers every Joy
/// binding with rebindable: false (controller buttons aren't rebindable in its settings) -
/// so instead every Gamepad-source button is taken, generic physical ones (JoyButtonA,
/// JoyLTrigger, JoyLStickUp, ...) included, mirroring how the keyboard side exposes every
/// real player-facing action.
///
/// Button names are read via reflection from ZInput's private m_buttons dictionary rather than
/// hardcoded, so the lists always match whatever this game version (or another mod) actually
/// registers. Note the Joy set reflects the controller layout active at startup
/// (Classic/Alternative1/Alternative2 register overlapping but not identical aliases);
/// entries for names missing from the current layout simply read false, and restarting the
/// game after a layout change regenerates the rest.
/// </summary>
internal static class DynamicButtonConfig
{
    public static readonly Dictionary<string, ConfigEntry<bool>> CancelEntries =
        new Dictionary<string, ConfigEntry<bool>>();
    public static readonly Dictionary<string, ConfigEntry<bool>> PauseEntries =
        new Dictionary<string, ConfigEntry<bool>>();

    private static readonly FieldRef<ZInput, Dictionary<string, ZInput.ButtonDef>> ButtonsRef =
        AccessTools.FieldRefAccess<ZInput, Dictionary<string, ZInput.ButtonDef>>("m_buttons");

    /// <summary>True if ZInput currently registers a button with this exact name.</summary>
    public static bool IsRegisteredButton(string name)
    {
        ZInput instance = ZInput.instance;
        return instance != null && ButtonsRef(instance).ContainsKey(name);
    }

    public static void Generate(ConfigFile config)
    {
        if (CancelEntries.Count > 0)
            return; // already generated

        ZInput instance = ZInput.instance;
        if (instance == null)
            return; // too early - ZInput not constructed yet

        IEnumerable<ZInput.ButtonDef> buttons = ButtonsRef
            .Invoke(instance)
            .Values.Where(b => b.Rebindable || b.Source == ZInput.InputSource.Gamepad)
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase);

        foreach (ZInput.ButtonDef button in buttons)
        {
            string name = button.Name;
            bool gamepad = button.Source == ZInput.InputSource.Gamepad;
            string label = DescribeButton(instance, name);
            string cancelSection = gamepad
                ? "Gamepad cancel buttons"
                : "Keyboard cancel buttons";
            string pauseSection = gamepad ? "Gamepad pause buttons" : "Keyboard pause buttons";

            // Gamepad defaults mirror the keyboard ones with two deliberate gaps: JoyDodge
            // stays false because it shares its physical button with JoyJump on the classic
            // layout (pressing jump to pause would instead cancel), and there is no
            // single-button gamepad equivalent of AutoRun to mirror.
            bool cancelDefaultValue = gamepad
                ? name
                    is "JoyAttack"
                        or "JoySecondaryAttack"
                        or "JoyBlock"
                        or "JoyUse"
                        or "JoyCrouch"
                        or "JoySit"
                : name
                    is "Attack"
                        or "SecondaryAttack"
                        or "Block"
                        or "Use"
                        or "Crouch"
                        or "AltDodge"
                        or "AutoRun"
                        or "Sit";

            CancelEntries[name] = config.Bind(
                cancelSection,
                name,
                cancelDefaultValue,
                $"Pressing {label} fully cancels continuous attack."
            );

            // "Attack" is deliberately never a pause button: the toggle itself fakes a held
            // Attack through the ZInput.GetButton patch, so AnyHeld would read back the
            // forced value while evaluating the pause state. That makes Paused depend on
            // its own previous-frame value and the toggle would flicker on/off every frame.
            // "JoyAttack" never reads the forced value (only "Attack" is faked), but it is
            // skipped too so both devices keep the same pause set.
            if (name == "Attack" || name == "JoyAttack")
                continue;

            bool pauseDefaultValue = gamepad
                ? name
                    is "JoySecondaryAttack"
                        or "JoyJump"
                        or "JoyBlock"
                        or "JoyLStickUp"
                        or "JoyLStickDown"
                        or "JoyLStickLeft"
                        or "JoyLStickRight"
                : name
                    is "SecondaryAttack"
                        or "Jump"
                        or "Block"
                        or "Forward"
                        or "Left"
                        or "Backward"
                        or "Right";

            PauseEntries[name] = config.Bind(
                pauseSection,
                name,
                pauseDefaultValue,
                $"Holding {label} temporarily pauses continuous attack; it resumes automatically on release."
            );
        }

        ToggleAutoAttack.LogStatic(
            $"Generated {CancelEntries.Count} cancel / {PauseEntries.Count} pause button entries from ZInput (keyboard + gamepad)."
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
/// Bow weapons are the exception, needing the hold briefly simulated as released in two
/// cases (see the helpers below): once the draw is full (to fire the arrow), and while the
/// draw state is stuck at -1 (to let it recover after the activation combo's Block press).
/// Reload weapons (crossbows) are the opposite: the hold is dropped entirely while waiting
/// for the reload action (see ReloadPending).
///
/// Scope: only ZInput.GetButton (the held/continuous query) is patched, and only for the
/// exact button name "Attack". GetButtonDown/GetButtonUp - used by DynamicButtonConfig.AnyDown
/// for cancellation - read a completely separate, independently-tracked state
/// (ButtonDef.Pressed/Released vs Held in ZInput.cs), so a genuine manual click is always
/// detected correctly regardless of this patch.
///
/// Gamepad players need no second patch: PlayerController computes the attack hold as
/// GetButton("Attack") || GetButton("JoyAttack"), so the single faked "Attack" alone makes
/// the gamepad input path see a held attack too.
/// </summary>
[HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButton), new Type[] { typeof(string) })]
internal static class ZInput_GetButton_ForceAttackHold
{
    private static void Postfix(string name, ref bool __result)
    {
        if (__result || !AutoAttackState.Active || AutoAttackState.Paused)
            return; // already true, toggle inactive, or currently paused: nothing to do

        if (name != "Attack")
            return;

        // Reload weapon waiting for its reload action: see ReloadPending.
        if (ReloadPending())
            return; // leave __result untouched: drop the fake hold until the weapon can fire

        // Bow stuck at draw time -1: see BowDrawStuck.
        if (BowDrawStuck())
            return; // one read without hold lets UpdateAttackBowDraw heal -1 -> 0

        // Bow micro-release: see BowFullyDrawn.
        if (BowFullyDrawn())
            return; // leave __result false: simulate the release edge for exactly one read

        __result = true;
    }

    private static readonly FieldRef<Humanoid, float> AttackDrawTimeRef = FieldRefAccess<
        Humanoid,
        float
    >("m_attackDrawTime");

    /// <summary>
    /// True when a bow's draw state is stuck at -1 and needs one tick without the fake hold
    /// to recover.
    ///
    /// Why it gets stuck: UpdateAttackBowDraw sets m_attackDrawTime = -1 whenever the player
    /// is blocking, in a minor action, or attached (and when StartDraw fails, e.g. no ammo),
    /// and its first branch only heals that back to 0 on a tick where m_attackHold is
    /// false. The default ToggleKey is Mouse1 + LeftAlt, and Mouse1 is the vanilla "Block"
    /// binding - which PlayerController feeds into m_blocking unconditionally, bow included
    /// (PlayerController.cs: "blockHold = GetButton(Block)"). So the activation combo itself
    /// blocks for a few frames, leaves the draw at -1, and the permanently faked hold then
    /// never gives the healing tick: branche 1 needs !m_attackHold, branche 2 needs >= 0,
    /// branche 3 needs > 0 - nothing ever starts. Pressing any pause button (the default
    /// pause set is exactly Forward/Backward/Left/Right) cut the hold for one tick and
    /// "fixed" it, which is why moving the player made auto-attack come alive.
    ///
    /// The one-tick release here does the same thing deterministically, whatever put the
    /// draw state at -1. m_attackDrawTime is protected on Humanoid, hence the FieldRef.
    /// </summary>
    private static bool BowDrawStuck()
    {
        Player player = Player.m_localPlayer;
        if (player == null)
            return false;

        ItemDrop.ItemData weapon = player.GetCurrentWeapon();
        if (weapon == null || !weapon.m_shared.m_attack.m_bowDraw)
            return false; // cheap check first: only bow-draw weapons can have a draw state

        return AttackDrawTimeRef(player) < 0f;
    }

    /// <summary>
    /// True while a reload weapon (crossbows) has fired and its reload action hasn't
    /// finished yet - the fake hold is dropped for that window.
    ///
    /// The reload itself does NOT need our help: UpdateWeaponLoading queues it
    /// unconditionally at the top of PlayerAttackInput, and once the shot animation ends
    /// the queued Reload progresses anyway, because Humanoid.StartAttack (InMinorAction())
    /// and Attack.Start (!IsWeaponLoaded()) both refuse the next swing the fake hold keeps
    /// requesting - which is exactly what lets InAttack() go false and opens the vanilla
    /// UpdateActionQueue gate. So the cycle (shot, full reload, shot) already worked.
    ///
    /// What the fake hold *did* do meanwhile is re-request an impossible attack every
    /// FixedUpdate: for a bow-draw crossbow that reran the whole draw state machine per
    /// cycle - StartDraw, full stamina drain and hold VFX over and over until the reload
    /// completed - and for other reload weapons it cloned and Stopped a refused Attack
    /// each tick. Dropping the hold removes that waste; __result is left untouched, so a
    /// physically held button still behaves exactly like vanilla.
    /// </summary>
    private static bool ReloadPending()
    {
        Player player = Player.m_localPlayer;
        if (player == null)
            return false;

        ItemDrop.ItemData weapon = player.GetCurrentWeapon();
        return weapon != null
            && weapon.m_shared.m_attack.m_requiresReload // bows use the draw/ammo system, not this one
            && !player.IsWeaponLoaded();
    }

    /// <summary>
    /// True when a bow has been drawn to full tension and can be fired right now.
    ///
    /// Why this exists: Player.UpdateAttackBowDraw only calls StartAttack from its
    /// "else if (m_attackDrawTime > 0f)" branch, which is reached only when m_attackHold
    /// goes false - i.e. the arrow fires on the *release* of the attack button. With the
    /// toggle faking a permanent hold, the draw fills to 100% and then just keeps draining
    /// stamina (the "hold" branch) and never fires. So when the draw is full, we skip
    /// forcing the hold for a single read: the game sees a release, fires the arrow
    /// (StartAttack) and resets m_attackDrawTime to 0 - and the next read forces again,
    /// starting a fresh draw. That turns one draw/fire cycle into continuous bow attack.
    ///
    /// InAttack() is required because Humanoid.StartAttack refuses while the previous
    /// shot's animation still plays (and UpdateAttackBowDraw resets the full draw even on
    /// a failed StartAttack) - without it, firing during the animation would silently eat
    /// the completed draw.
    ///
    /// Only reached for name == "Attack", and after the __result/Active/Paused early-outs,
    /// so a player physically holding Attack still gets pure vanilla behavior.
    /// </summary>
    private static bool BowFullyDrawn()
    {
        Player player = Player.m_localPlayer;
        if (player == null)
            return false;

        ItemDrop.ItemData weapon = player.GetCurrentWeapon();
        if (weapon == null || !weapon.m_shared.m_attack.m_bowDraw)
            return false; // cheap check first: only bow-draw weapons ever need this

        if (player.InAttack())
            return false; // keep holding: firing now would fail StartAttack and eat the draw

        return player.GetAttackDrawPercentage() >= 1f;
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
    private static readonly MethodInfo BlocksEquipMethod = AccessTools.Method(
        typeof(EquipDuringAutoAttack),
        nameof(BlocksEquip)
    );

    private static readonly MethodInfo BlocksQueuedActionMethod = AccessTools.Method(
        typeof(EquipDuringAutoAttack),
        nameof(BlocksQueuedAction)
    );

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
