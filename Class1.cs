using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace E1InvisibleMan;

[MinimumApiVersion(340)]
public class E1InvisibleManPlugin : BasePlugin
{
    public override string ModuleName => "E1 Invisible Man (1vX)";
    public override string ModuleVersion => "1.0.1"; // minimal clean version
    public override string ModuleAuthor => "Axel Dread | Element One Gaming - http://elementonegaming.net";
    public override string ModuleDescription => "Enables choosing a player as the Invisible Man, who is completely invisible until making noise.";

    // ---------- Config ----------
    public class Config
    {
        public double RevealSeconds { get; set; } = 0.5;      // visible window after Ghost makes noise
        public double MissWindowSeconds { get; set; } = 0.75;  // time after a shot to hit Ghost and get a refund
        public int ShotPenaltyHP { get; set; } = 4;            // HP lost per shot (provisional, refunded on hit)
        public bool DebugLog { get; set; } = false;            // extra console logs

        // Running reveal (velocity safety net if footstep event missing)
        public bool RunningRevealEnabled { get; set; } = true;
        public float RunningSpeedThreshold { get; set; } = 220f;
        public float RunningCooldownSeconds { get; set; } = 0.30f;
    }

    private Config _cfg = new();

    private string ConfigPath => Path.GetFullPath(Path.Combine(
        ModuleDirectory,
        "..", "..", // from plugins/<ThisPlugin> -> configs/plugins
        "configs", "plugins", "E1InvisibleMan", "config.json"
    ));

    private void LoadConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            if (!File.Exists(ConfigPath))
            {
                SaveConfig();
                this.Logger.LogInformation("[E1] Created default config at: {path}", ConfigPath);
                return;
            }

            var json = File.ReadAllText(ConfigPath);
            var cfg = JsonSerializer.Deserialize<Config>(json);
            if (cfg != null) _cfg = cfg;
            this.Logger.LogInformation("[E1] Loaded config from {path}", ConfigPath);
        }
        catch (Exception ex)
        {
            this.Logger.LogError(ex, "[E1] Failed to load config, using defaults");
        }
    }

    private void SaveConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            this.Logger.LogError(ex, "[E1] Failed to save config");
        }
    }

    // ---------- State ----------
    private CCSPlayerController? _ghost;
    private DateTime _revealUntilUtc = DateTime.MinValue;
    private bool _modeActive = false;

    // Landing detection
    private bool _ghostWasOnGround = false;
    private float _ghostPrevVelZ = 0f;

    // Running reveal helper
    private DateTime _nextRunRevealUtc = DateTime.MinValue;

    // Per-shooter state: immediate penalty on fire + refundable credit within MissWindowSeconds
    private class ShooterState
    {
        public Queue<Timer> RefundCredits { get; } = new(); // each timer = one refundable credit that expires
    }
    private readonly Dictionary<int, ShooterState> _shooters = new();

    // ---------- Lifecycle ----------
    public override void Load(bool hotReload)
    {
        LoadConfig();

        // Commands
        AddCommand("e1_set", "Select the Ghost (#userid, partial name, or @me)", CmdSetGhost);
        AddCommand("e1_random", "Pick a random Ghost", CmdRandomGhost);
        AddCommand("e1_start", "Start Ghost mode (visibility active)", CmdStart);
        AddCommand("e1_stop", "Stop Ghost mode", CmdStop);
        AddCommand("e1_reload", "Reload E1 config", CmdReload);
        AddCommand("e1_save", "Save E1 config", CmdSave);
        AddCommand("e1_debug", "Toggle debug logs (0/1)", CmdDebug);

        // Listeners / hooks
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);

        // Events (noise + miss window + land/fall)
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventPlayerFootstep>(OnFootstep);
        RegisterEventHandler<EventPlayerJump>(OnJump);
        RegisterEventHandler<EventBombBeginplant>(OnBeginPlant);
        RegisterEventHandler<EventBombBegindefuse>(OnBeginDefuse);
        RegisterEventHandler<EventGrenadeThrown>(OnGrenadeThrow);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt); // refund if Ghost is hit; detect fall-damage

        // Timers
        AddTimer(0.1f, MovementTick, TimerFlags.REPEAT); // movement + landing + running detection

        this.Logger.LogInformation("[E1] Invisible Man loaded. Version {v}", ModuleVersion);
    }

    public override void Unload(bool hotReload)
    {
        SaveConfig();
        base.Unload(hotReload);
    }

    // ---------- Map start ----------
    private void OnMapStart(string map)
    {
        _modeActive = false;
        _ghost = null;
        _revealUntilUtc = DateTime.MinValue;
        ClearAllPendingRefunds();
        _ghostWasOnGround = false;
        _ghostPrevVelZ = 0f;
        _nextRunRevealUtc = DateTime.MinValue;
    }

    // ---------- Visibility core (CheckTransmit) ----------
    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (_ghost is null || !_modeActive) return;

        var pawn = _ghost.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return;

        bool visibleNow = DateTime.UtcNow <= _revealUntilUtc;

        for (int i = 0; i < infoList.Count; i++)
        {
            var (info, viewer) = infoList[i];
            if (viewer == null || !viewer.IsValid) continue;
            if (viewer.Slot == _ghost.Slot) continue; // Ghost sees self
            // Let spectators/dead freely observe the Ghost (prevents frozen camera when spectating)
            var vPawn = viewer.PlayerPawn.Value;
            bool viewerSpectatingOrDead = vPawn == null || !vPawn.IsValid;
            if (viewerSpectatingOrDead) continue;

            if (!visibleNow) info.TransmitEntities.Remove(pawn);
        }
    }

    private void TriggerReveal(string reason)
    {
        _revealUntilUtc = DateTime.UtcNow.AddSeconds(Math.Max(0.01, _cfg.RevealSeconds));
        if (_cfg.DebugLog)
            this.Logger.LogInformation("[E1] Reveal: {reason} until {t:HH:mm:ss.fff}Z", reason, _revealUntilUtc);
    }

    // ---------- Timers ----------
    private void MovementTick()
    {
        if (!_modeActive || _ghost is null) return;

        if (TryGetAbsVelocity(_ghost, out var vx, out var vy, out var vz))
        {
            bool onGround = Math.Abs(vz) < 5.0f;
            bool wasFallingFast = (_ghostPrevVelZ < -250f);
            bool justLanded = !_ghostWasOnGround && onGround && wasFallingFast;
            if (justLanded)
            {
                if (_cfg.DebugLog) this.Logger.LogInformation("[E1] Reveal: hard_land_no_damage");
                TriggerReveal("hard_land_no_damage");
            }
            _ghostWasOnGround = onGround;
            _ghostPrevVelZ = vz;

            // Running detection (velocity-based)
            if (_cfg.RunningRevealEnabled)
            {
                var now = DateTime.UtcNow;
                if (now >= _nextRunRevealUtc)
                {
                    float speed2D = MathF.Sqrt(vx * vx + vy * vy);
                    if (onGround && speed2D >= _cfg.RunningSpeedThreshold)
                    {
                        if (_cfg.DebugLog) this.Logger.LogInformation("[E1] Reveal: running_speed (v={v:F1})", speed2D);
                        TriggerReveal("running_speed");
                        _nextRunRevealUtc = now.AddSeconds(Math.Max(0.05f, _cfg.RunningCooldownSeconds));
                    }
                }
            }
        }
    }

    // ---------- Helpers ----------
    private bool TryGetAbsVelocity(CCSPlayerController player, out float vx, out float vy, out float vz)
    {
        vx = vy = vz = 0f;
        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return false;

        // Simple and stable paths first
        try { var v = pawn.AbsVelocity; vx = v.X; vy = v.Y; vz = v.Z; return true; } catch { }
        try { var v = pawn.Velocity; vx = v.X; vy = v.Y; vz = v.Z; return true; } catch { }
        return false;
    }

    private void EnqueueShot(CCSPlayerController shooter)
    {
        if (_cfg.ShotPenaltyHP <= 0) return;

        var slot = shooter.Slot;
        if (!_shooters.TryGetValue(slot, out var state))
        {
            state = new ShooterState();
            _shooters[slot] = state;
        }

        // 1) Apply penalty immediately (provisional)
        var pawn = shooter.PlayerPawn.Value;
        if (pawn != null && pawn.IsValid)
        {
            // Apply immediately
            pawn.Health = Math.Max(1, pawn.Health - _cfg.ShotPenaltyHP);
            // Force a state change so clients see it instantly
            try { Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_iHealth"); } catch { }
            try { Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth"); } catch { }
        }

        // 2) Create a refundable credit that expires after MissWindowSeconds
        //    If the shooter hits the Ghost before it expires, we refund ShotPenaltyHP once.
        while (state.RefundCredits.Count > 50)
        {
            var old = state.RefundCredits.Dequeue();
            try { old.Kill(); } catch { }
        }

        var t = AddTimer((float)Math.Max(0.05, _cfg.MissWindowSeconds), () =>
        {
            // Expire one credit silently (no further action since we already applied damage)
            if (_shooters.TryGetValue(slot, out var st) && st.RefundCredits.Count > 0)
            {
                try { var _ = st.RefundCredits.Dequeue(); } catch { }
                if (st.RefundCredits.Count == 0) _shooters.Remove(slot);
            }
        });

        state.RefundCredits.Enqueue(t);
    }

    private void CancelOnePending(CCSPlayerController shooter)
    {
        var slot = shooter.Slot;
        if (_shooters.TryGetValue(slot, out var state) && state.RefundCredits.Count > 0)
        {
            var t = state.RefundCredits.Dequeue();
            try { t.Kill(); } catch { }

            // Refund the previously applied provisional damage
            var pawn = shooter.PlayerPawn.Value;
            if (pawn != null && pawn.IsValid)
            {
                int maxHp = 100;
                try { if (pawn.MaxHealth > 0) maxHp = pawn.MaxHealth; } catch { }
                pawn.Health = Math.Min(maxHp, pawn.Health + _cfg.ShotPenaltyHP);
                try { Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_iHealth"); } catch { }
                try { Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth"); } catch { }
            }

            if (state.RefundCredits.Count == 0) _shooters.Remove(slot);
        }
    }

    private void ClearAllPendingRefunds()
    {
        foreach (var kv in _shooters)
        {
            while (kv.Value.RefundCredits.Count > 0)
            {
                var t = kv.Value.RefundCredits.Dequeue();
                try { t.Kill(); } catch { }
            }
        }
        _shooters.Clear();
    }

    // ---------- Events ----------
    [GameEventHandler]
    public HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        if (!_modeActive) return HookResult.Continue;
        var shooter = @event.Userid;
        if (shooter == null || !shooter.IsValid) return HookResult.Continue;

        if (_ghost != null && shooter.Slot == _ghost.Slot)
        {
            TriggerReveal("weapon_fire");
        }
        else
        {
            // Skip grenade-type weapons from the penalty system
            try
            {
                var w = @event.Weapon ?? string.Empty;
                var wn = w.ToLowerInvariant();
                if (wn.Contains("grenade") || wn.Contains("flash") || wn.Contains("smoke") || wn.Contains("molotov") || wn.Contains("incendiary") || wn.Contains("decoy"))
                    return HookResult.Continue;
            }
            catch { }
            EnqueueShot(shooter);
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        if (!_modeActive || _ghost is null) return HookResult.Continue;
        var victim = @event.Userid;
        var attacker = @event.Attacker;
        if (victim == null || attacker == null || !victim.IsValid || !attacker.IsValid) return HookResult.Continue;

        // If attacker hits the Ghost within the window, cancel one pending credit (refund HP)
        if (victim.Slot == _ghost.Slot && attacker.Slot != _ghost.Slot)
        {
            CancelOnePending(attacker);
        }

        // If Ghost took fall damage, also reveal (noise)
        if (victim.Slot == _ghost.Slot && (attacker.Slot == victim.Slot || attacker.Slot == -1) && @event.DmgHealth > 0)
            TriggerReveal("fall_damage");

        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnFootstep(EventPlayerFootstep @event, GameEventInfo info)
    {
        if (!_modeActive || _ghost is null) return HookResult.Continue;
        var p = @event.Userid;
        if (p != null && p.IsValid && p.Slot == _ghost.Slot)
        {
            if (_cfg.DebugLog) this.Logger.LogInformation("[E1] Reveal: footstep");
            TriggerReveal("footstep");
        }
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnJump(EventPlayerJump @event, GameEventInfo info)
    {
        if (!_modeActive || _ghost is null) return HookResult.Continue;
        var p = @event.Userid;
        if (p != null && p.IsValid && p.Slot == _ghost.Slot) TriggerReveal("jump");
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBeginPlant(EventBombBeginplant @event, GameEventInfo info)
    {
        if (!_modeActive || _ghost is null) return HookResult.Continue;
        var p = @event.Userid;
        if (p != null && p.IsValid && p.Slot == _ghost.Slot) TriggerReveal("beginplant");
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
        if (!_modeActive || _ghost is null) return HookResult.Continue;
        var p = @event.Userid;
        if (p != null && p.IsValid && p.Slot == _ghost.Slot) TriggerReveal("begindefuse");
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnGrenadeThrow(EventGrenadeThrown @event, GameEventInfo info)
    {
        if (!_modeActive || _ghost is null) return HookResult.Continue;
        var p = @event.Userid;
        if (p != null && p.IsValid && p.Slot == _ghost.Slot) TriggerReveal("grenade");
        return HookResult.Continue;
    }

    // ---------- Commands ----------
    private void CmdSetGhost(CCSPlayerController? caller, CommandInfo command)
    {
        var arg = command.GetArg(1);
        if (string.IsNullOrWhiteSpace(arg)) { PrintTo(caller, "Usage: e1_set <#userid | partialName | @me>"); return; }

        CCSPlayerController? target = null;
        if (arg == "@me" && caller != null && caller.IsValid) target = caller;
        else if (arg.StartsWith("#") && int.TryParse(arg.AsSpan(1), out int userId)) target = Utilities.GetPlayerFromUserid(userId);
        else target = Utilities.GetPlayers().FirstOrDefault(p => p != null && p.IsValid && !string.IsNullOrWhiteSpace(p.PlayerName) && p.PlayerName.Contains(arg, StringComparison.OrdinalIgnoreCase));

        if (target == null || !target.IsValid) { PrintTo(caller, $"No player found for '{arg}'."); return; }

        _ghost = target; _modeActive = true; _revealUntilUtc = DateTime.MinValue; ClearAllPendingRefunds();
        Server.PrintToChatAll($"[E1] Ghost selected: {target.PlayerName}. Ghost is invisible except briefly on noise.");
    }

    private void CmdRandomGhost(CCSPlayerController? caller, CommandInfo command)
    {
        var players = Utilities.GetPlayers().Where(p => p != null && p.IsValid && p.PlayerPawn.Value != null && p.PlayerPawn.Value.IsValid).ToList();
        if (players.Count == 0) { PrintTo(caller, "No players to choose from."); return; }

        var idx = Random.Shared.Next(players.Count);
        _ghost = players[idx]; _modeActive = true; _revealUntilUtc = DateTime.MinValue; ClearAllPendingRefunds();
        Server.PrintToChatAll($"[E1] Random Ghost is {_ghost!.PlayerName}");
    }

    private void CmdStart(CCSPlayerController? caller, CommandInfo command)
    {
        if (_ghost == null || !_ghost.IsValid) PrintTo(caller, "Warning: no Ghost selected yet. Use e1_set.");
        else Server.PrintToChatAll("[E1] Ghost mode active.");  
        _modeActive = true;
    }

    private void CmdStop(CCSPlayerController? caller, CommandInfo command)
    {
        _modeActive = false; _ghost = null; _revealUntilUtc = DateTime.MinValue; ClearAllPendingRefunds();
        Server.PrintToChatAll("[E1] Ghost mode stopped.");
    }

    private void CmdReload(CCSPlayerController? caller, CommandInfo command)
    {
        LoadConfig();
        PrintTo(caller, "[E1] Config reloaded.");
    }

    private void CmdSave(CCSPlayerController? caller, CommandInfo command)
    {
        SaveConfig();
        PrintTo(caller, "[E1] Config saved.");
    }

    private void CmdDebug(CCSPlayerController? caller, CommandInfo command)
    {
        var arg = command.GetArg(1);
        if (string.IsNullOrWhiteSpace(arg)) { PrintTo(caller, $"[E1] DebugLog is {(_cfg.DebugLog ? 1 : 0)}. Usage: e1_debug <0|1>"); return; }
        _cfg.DebugLog = (arg == "1" || arg.Equals("true", StringComparison.OrdinalIgnoreCase));
        SaveConfig();
        PrintTo(caller, $"[E1] DebugLog set to {(_cfg.DebugLog ? 1 : 0)}");
    }

    private static void PrintTo(CCSPlayerController? who, string msg)
    {
        if (who == null || !who.IsValid) Server.PrintToChatAll(msg);
        else who.PrintToChat(msg);
    }
}
