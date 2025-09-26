// E1 Invisible Man (1vX) – CounterStrikeSharp v1.0.340
// Console: e1_* | Chat: !set, !random, !start, !stop, !reload, !save
// Build: .NET 8
// ModuleVersion: 1.3.2

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using Microsoft.Extensions.Logging;

namespace E1InvisibleMan;

[MinimumApiVersion(340)]
public class E1InvisibleManPlugin : BasePlugin
{
    public override string ModuleName => "E1 Invisible Man (1vX)";
    public override string ModuleVersion => "1.3.2";
    public override string ModuleAuthor => "Axel Dread | Element One Gaming";
    public override string ModuleDescription => "Invisible Man mode using CheckTransmit + per-weapon spam tax with chat aliases";

    public class Config
    {
        public double RevealSeconds { get; set; } = 0.3;
        public double MissWindowSeconds { get; set; } = 0.75;

        public bool RevealOnWeaponFireEnabled { get; set; } = true;
        public bool RevealOnFootstepEnabled { get; set; } = true;
        public bool RevealOnJumpEnabled { get; set; } = true;
        public bool RevealOnPlantEnabled { get; set; } = true;
        public bool RevealOnDefuseEnabled { get; set; } = true;
        public bool RevealOnGrenadeEnabled { get; set; } = true;

        public bool RunRevealEnabled { get; set; } = true;
        public float RunRevealSpeed { get; set; } = 131f;

        public bool RevealOnDamageEnabled { get; set; } = true;
        public double RevealOnDamageSeconds { get; set; } = 0.35;

        public int ShotgunPenaltyHP { get; set; } = 8;
        public int SniperPenaltyHP { get; set; } = 5;
        public int SMGPenaltyHP { get; set; } = 2;
        public int PistolPenaltyHP { get; set; } = 2;
        public int RiflePenaltyHP { get; set; } = 2;
        public int LMGPenaltyHP { get; set; } = 2;
        public int GrenadePenaltyHP { get; set; } = 0;
        public int KnifePenaltyHP { get; set; } = 2;
        public int DefaultPenaltyHP { get; set; } = 2;

        public bool PenaltyCanKill { get; set; } = true;

        public bool BonusHealthPerEnemyEnabled { get; set; } = true;
        public int BonusHealthPerEnemyAmount { get; set; } = 100;

        public bool DebugLog { get; set; } = false;
    }

    private Config _cfg = new();
    private static readonly JsonSerializerOptions s_jsonIndented = new() { WriteIndented = true };

    private string ConfigPath => Path.Combine(
        ModuleDirectory.Replace("\\plugins\\", "\\configs\\plugins\\").Replace("/plugins/", "/configs/plugins/"),
        "E1InvisibleMan",
        "config.json"
    );

    private void LoadConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            if (!File.Exists(ConfigPath))
            {
                SaveConfig();
                Logger.LogInformation("[E1] Created default config at: {path}", ConfigPath);
                return;
            }

            var json = File.ReadAllText(ConfigPath);
            var cfg = JsonSerializer.Deserialize<Config>(json);
            if (cfg != null) _cfg = cfg;
            Logger.LogInformation("[E1] Loaded config from {path}", ConfigPath);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[E1] Failed to load config, using defaults");
        }
    }

    private void SaveConfig()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_cfg, s_jsonIndented);
            File.WriteAllText(ConfigPath, json);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[E1] Failed to save config");
        }
    }

    private readonly Random _rng = new();
    private CCSPlayerController? _invisibleMan;
    private bool _modeActive;
    private DateTime _revealUntilUtc = DateTime.MinValue;

    private readonly Dictionary<int, Stack<PendingPenalty>> _pendingRefundsBySlot = new();

    private readonly struct PendingPenalty
    {
        public readonly int Amount;
        public readonly DateTime ExpiresUtc;

        public PendingPenalty(int amount, DateTime expiresUtc)
        {
            Amount = amount;
            ExpiresUtc = expiresUtc;
        }
    }

    public override void Load(bool hotReload)
    {
        LoadConfig();

        AddCommand("e1_set", "Select the Invisible Man (#userid | partialName | @me)", CmdSetInvisibleMan);
        AddCommand("e1_random", "Pick a random Invisible Man", CmdRandomInvisibleMan);
        AddCommand("e1_start", "Start Invisible Man mode", CmdStart);
        AddCommand("e1_stop", "Stop Invisible Man mode", CmdStop);
        AddCommand("e1_reload", "Reload E1 config", CmdReload);
        AddCommand("e1_save", "Save E1 config", CmdSave);

        RegisterEventHandler<EventPlayerChat>(OnPlayerChat);

        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventPlayerFootstep>(OnFootstep);
        RegisterEventHandler<EventPlayerJump>(OnJump);
        RegisterEventHandler<EventBombBeginplant>(OnBeginPlant);
        RegisterEventHandler<EventBombBegindefuse>(OnBeginDefuse);
        RegisterEventHandler<EventGrenadeThrown>(OnGrenadeThrow);

        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);

        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);

        Logger.LogInformation("[E1] Invisible Man loaded (v{ver}).", ModuleVersion);
    }

    public override void Unload(bool hotReload)
    {
        SaveConfig();
        base.Unload(hotReload);
    }

    private HookResult OnPlayerChat(EventPlayerChat @event, GameEventInfo info)
    {
        var player = Utilities.GetPlayerFromUserid(@event.Userid);
        if (player is null || !player.IsValid) return HookResult.Continue;

        var msg = (@event.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(msg)) return HookResult.Continue;
        if (!(msg.StartsWith('!') || msg.StartsWith('/'))) return HookResult.Continue;

        var line = msg[1..].Trim();
        if (string.IsNullOrEmpty(line)) return HookResult.Continue;

        var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : string.Empty;

        switch (cmd)
        {
            case "set":
            case "ghost":
            case "invisible":
            case "man":
                ChatSelectInvisibleMan(player, arg);
                break;
            case "random":
                DoRandomInvisibleMan(player);
                break;
            case "start":
                DoStart(player);
                break;
            case "stop":
                DoStop(player);
                break;
            case "reload":
                DoReload(player);
                break;
            case "save":
                DoSave(player);
                break;
        }

        return HookResult.Continue;
    }

    private void ChatSelectInvisibleMan(CCSPlayerController caller, string arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            PrintTo(caller, "Usage: !set <#userid | partialName | @me>");
            return;
        }
        if (TrySelectInvisibleManByArg(caller, arg))
        {
            RecalcInvisibleManBonusHP();
        }
    }

    private bool TrySelectInvisibleManByArg(CCSPlayerController? caller, string arg)
    {
        CCSPlayerController? target = null;

        if (arg.Equals("@me", StringComparison.OrdinalIgnoreCase) && caller != null && caller.IsValid)
        {
            target = caller;
        }
        else if (arg.StartsWith('#') && int.TryParse(arg.AsSpan(1), out int userId))
        {
            target = Utilities.GetPlayerFromUserid(userId);
        }
        else
        {
            var all = Utilities.GetPlayers()
                .Where(p => p != null && p.IsValid && !string.IsNullOrWhiteSpace(p.PlayerName));
            target = all.FirstOrDefault(p => p.PlayerName.Contains(arg, StringComparison.OrdinalIgnoreCase));
        }

        if (target == null || !target.IsValid)
        {
            PrintTo(caller, $"No player found for '{arg}'.");
            return false;
        }

        _invisibleMan = target;
        _modeActive = true;
        _revealUntilUtc = DateTime.MinValue;
        Server.PrintToChatAll($"[E1] Invisible Man selected: {target.PlayerName}. Invisible Man is hidden except briefly on configured reveal events.");
        return true;
    }

    private void OnCheckTransmit(CCheckTransmitInfoList list)
    {
        if (_invisibleMan is null || !_modeActive) return;

        var pawn = _invisibleMan.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return;

        bool timedVisible = DateTime.UtcNow <= _revealUntilUtc;

        bool speedVisible = false;
        if (_cfg.RunRevealEnabled)
        {
            float speed2D = 0f;

            try
            {
                var vAbs = pawn.AbsVelocity;
                speed2D = GetSpeed2DFromAny(vAbs);
            }
            catch
            {
                try
                {
                    var v = pawn.Velocity;
                    speed2D = GetSpeed2DFromAny(v);
                }
                catch
                {
                    speed2D = 0f;
                }
            }

            speedVisible = speed2D >= _cfg.RunRevealSpeed;

            if (_cfg.DebugLog && speedVisible)
                Logger.LogInformation("[E1] Speed reveal: {spd} >= {thr}", speed2D, _cfg.RunRevealSpeed);
        }

        bool visibleNow = timedVisible || speedVisible;

        foreach (var (info, viewer) in list)
        {
            if (viewer == null || !viewer.IsValid) continue;
            if (viewer.Slot == _invisibleMan.Slot) continue;

            if (!visibleNow)
            {
                info.TransmitEntities.Remove(pawn);
            }
        }
    }

    private static float GetSpeed2DFromAny(object velocity)
    {
        if (velocity is CNetworkVelocityVector net)
        {
            return MathF.Sqrt(net.X * net.X + net.Y * net.Y);
        }

        if (velocity is CounterStrikeSharp.API.Modules.Utils.Vector vec)
        {
            return MathF.Sqrt(vec.X * vec.X + vec.Y * vec.Y);
        }

        return 0f;
    }

    private void TriggerReveal(string reason, double? seconds = null)
    {
        var dur = Math.Max(0.01, seconds ?? _cfg.RevealSeconds);
        _revealUntilUtc = DateTime.UtcNow.AddSeconds(dur);
        if (_cfg.DebugLog)
            Logger.LogInformation("[E1] Reveal ({reason}) for {sec:F2}s until {until}", reason, dur, _revealUntilUtc);
    }

    private HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        var shooter = @event.Userid;
        if (shooter == null || !shooter.IsValid) return HookResult.Continue;

        if (_modeActive && _invisibleMan != null && shooter.Slot == _invisibleMan.Slot)
        {
            if (_cfg.RevealOnWeaponFireEnabled)
                TriggerReveal("weapon_fire");
        }
        else if (_modeActive && _invisibleMan != null)
        {
            var weapon = (@event.Weapon ?? string.Empty).ToLowerInvariant();
            if (!IsGrenade(weapon))
            {
                int dmg = GetPenaltyForWeapon(weapon);
                if (dmg > 0)
                {
                    ApplyPenalty(shooter, dmg);

                    var slot = shooter.Slot;
                    var until = DateTime.UtcNow.AddSeconds(Math.Max(0.01, _cfg.MissWindowSeconds));
                    if (!_pendingRefundsBySlot.TryGetValue(slot, out var stack))
                    {
                        stack = new Stack<PendingPenalty>(8);
                        _pendingRefundsBySlot[slot] = stack;
                    }
                    stack.Push(new PendingPenalty(dmg, until));
                }
            }
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        if (!_modeActive || _invisibleMan is null) return HookResult.Continue;

        var victim = @event.Userid;
        var attacker = @event.Attacker;

        if (victim == null || attacker == null) return HookResult.Continue;
        if (!victim.IsValid || !attacker.IsValid) return HookResult.Continue;

        if (_cfg.RevealOnDamageEnabled && victim.Slot == _invisibleMan.Slot)
        {
            TriggerReveal("damage", _cfg.RevealOnDamageSeconds);
        }

        if (victim.Slot != _invisibleMan.Slot || attacker.Slot == _invisibleMan.Slot) return HookResult.Continue;

        if (_pendingRefundsBySlot.TryGetValue(attacker.Slot, out var stack))
        {
            var now = DateTime.UtcNow;
            while (stack.Count > 0 && stack.Peek().ExpiresUtc < now) stack.Pop();

            if (stack.Count > 0)
            {
                var pending = stack.Pop();
                RefundPenalty(attacker, pending.Amount);
            }

            if (stack.Count == 0)
                _pendingRefundsBySlot.Remove(attacker.Slot);
        }

        return HookResult.Continue;
    }

    private HookResult OnFootstep(EventPlayerFootstep @event, GameEventInfo info)
    {
        if (!_modeActive || _invisibleMan is null) return HookResult.Continue;
        if (_cfg.RevealOnFootstepEnabled)
        {
            var p = @event.Userid;
            if (p != null && p.IsValid && p.Slot == _invisibleMan.Slot)
                TriggerReveal("footstep");
        }
        return HookResult.Continue;
    }

    private HookResult OnJump(EventPlayerJump @event, GameEventInfo info)
    {
        if (!_modeActive || _invisibleMan is null) return HookResult.Continue;
        if (_cfg.RevealOnJumpEnabled)
        {
            var p = @event.Userid;
            if (p != null && p.IsValid && p.Slot == _invisibleMan.Slot)
                TriggerReveal("jump");
        }
        return HookResult.Continue;
    }

    private HookResult OnBeginPlant(EventBombBeginplant @event, GameEventInfo info)
    {
        if (!_modeActive || _invisibleMan is null) return HookResult.Continue;
        if (_cfg.RevealOnPlantEnabled)
        {
            var p = @event.Userid;
            if (p != null && p.IsValid && p.Slot == _invisibleMan.Slot)
                TriggerReveal("beginplant");
        }
        return HookResult.Continue;
    }

    private HookResult OnBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
        if (!_modeActive || _invisibleMan is null) return HookResult.Continue;
        if (_cfg.RevealOnDefuseEnabled)
        {
            var p = @event.Userid;
            if (p != null && p.IsValid && p.Slot == _invisibleMan.Slot)
                TriggerReveal("begindefuse");
        }
        return HookResult.Continue;
    }

    private HookResult OnGrenadeThrow(EventGrenadeThrown @event, GameEventInfo info)
    {
        if (!_modeActive || _invisibleMan is null) return HookResult.Continue;
        if (_cfg.RevealOnGrenadeEnabled)
        {
            var p = @event.Userid;
            if (p != null && p.IsValid && p.Slot == _invisibleMan.Slot)
                TriggerReveal("grenade");
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        if (!_modeActive || _invisibleMan is null) return HookResult.Continue;
        var p = @event.Userid;
        if (p != null && p.IsValid && p.Slot == _invisibleMan.Slot)
        {
            RecalcInvisibleManBonusHP();
        }
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (!_modeActive || _invisibleMan is null) return HookResult.Continue;
        RecalcInvisibleManBonusHP();
        return HookResult.Continue;
    }

    private void CmdSetInvisibleMan(CCSPlayerController? caller, CommandInfo command)
    {
        var arg = command.GetArg(1);
        if (string.IsNullOrWhiteSpace(arg))
        {
            PrintTo(caller, "Usage: e1_set <#userid | partialName | @me>");
            return;
        }
        if (TrySelectInvisibleManByArg(caller, arg))
        {
            RecalcInvisibleManBonusHP();
        }
    }

    private void CmdRandomInvisibleMan(CCSPlayerController? caller, CommandInfo _)
    {
        DoRandomInvisibleMan(caller);
    }

    private void DoRandomInvisibleMan(CCSPlayerController? caller)
    {
        var players = Utilities.GetPlayers()
            .Where(p => p != null && p.IsValid && p.PlayerPawn.Value != null && p.PlayerPawn.Value.IsValid)
            .ToList();

        if (players.Count == 0)
        {
            PrintTo(caller, "No players to choose from.");
            return;
        }

        var chosen = players[_rng.Next(players.Count)];
        _invisibleMan = chosen;
        _modeActive = true;
        _revealUntilUtc = DateTime.MinValue;

        Server.PrintToChatAll($"[E1] Random Invisible Man: {chosen.PlayerName}. Invisible Man is hidden except briefly on configured reveal events.");
        RecalcInvisibleManBonusHP();
    }

    private void CmdStart(CCSPlayerController? caller, CommandInfo _)
    {
        DoStart(caller);
    }

    private void DoStart(CCSPlayerController? _caller)
    {
        _modeActive = true;
        Server.PrintToChatAll("[E1] Invisible Man mode active.");
        RecalcInvisibleManBonusHP();
    }

    private void CmdStop(CCSPlayerController? caller, CommandInfo _)
    {
        DoStop(caller);
    }

    private void DoStop(CCSPlayerController? _caller)
    {
        _modeActive = false;
        _invisibleMan = null;
        _revealUntilUtc = DateTime.MinValue;
        _pendingRefundsBySlot.Clear();
        Server.PrintToChatAll("[E1] Invisible Man mode stopped.");
    }

    private void CmdReload(CCSPlayerController? caller, CommandInfo _)
    {
        DoReload(caller);
    }

    private void DoReload(CCSPlayerController? caller)
    {
        LoadConfig();
        PrintTo(caller, "[E1] Config reloaded.");
        RecalcInvisibleManBonusHP();
    }

    private void CmdSave(CCSPlayerController? caller, CommandInfo _)
    {
        DoSave(caller);
    }

    private void DoSave(CCSPlayerController? caller)
    {
        SaveConfig();
        PrintTo(caller, "[E1] Config saved.");
        RecalcInvisibleManBonusHP();
    }

    private void ApplyPenalty(CCSPlayerController shooter, int hp)
    {
        var pawn = shooter.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return;
        if (hp <= 0) return;

        var newHp = pawn.Health - hp;
        if (!_cfg.PenaltyCanKill && newHp < 1)
            newHp = 1;

        pawn.Health = Math.Max(0, newHp);
        ForceHealthStateChanged(pawn);

        if (pawn.Health <= 0)
        {
            try
            {
                shooter.CommitSuicide(false, true);
            }
            catch { }
        }
    }

    private void RefundPenalty(CCSPlayerController shooter, int hp)
    {
        var pawn = shooter.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return;
        if (hp <= 0) return;

        int maxHp = pawn.MaxHealth > 0 ? pawn.MaxHealth : 100;
        pawn.Health = Math.Min(maxHp, pawn.Health + hp);
        ForceHealthStateChanged(pawn);
    }

    private static void ForceHealthStateChanged(CCSPlayerPawn pawn)
    {
        try
        {
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_iHealth");
        }
        catch { }
    }

    private static bool IsGrenade(string weapon)
    {
        weapon = weapon.ToLowerInvariant();
        return weapon.Contains("grenade") ||
               weapon.Contains("molotov") ||
               weapon.Contains("flashbang") ||
               weapon.Contains("smoke") ||
               weapon.Contains("decoy") ||
               weapon.Contains("incendiary");
    }

    private int GetPenaltyForWeapon(string weapon)
    {
        weapon = weapon.ToLowerInvariant();

        if (weapon.Contains("knife") || weapon == "weapon_bayonet")
            return _cfg.KnifePenaltyHP;

        if (weapon.Contains("nova") || weapon.Contains("xm1014") || weapon.Contains("mag7") || weapon.Contains("sawedoff"))
            return _cfg.ShotgunPenaltyHP;

        if (weapon.Contains("awp") || weapon.Contains("ssg08") || weapon.Contains("g3sg1") || weapon.Contains("scar20"))
            return _cfg.SniperPenaltyHP;

        if (weapon.Contains("mp9") || weapon.Contains("mp7") || weapon.Contains("mp5") || weapon.Contains("mac10") ||
            weapon.Contains("ump45") || weapon.Contains("bizon") || weapon.Contains("p90"))
            return _cfg.SMGPenaltyHP;

        if (weapon.Contains("glock") || weapon.Contains("hkp2000") || weapon.Contains("usp") || weapon.Contains("p250") ||
            weapon.Contains("elite") || weapon.Contains("fiveseven") || weapon.Contains("tec9") ||
            weapon.Contains("cz75a") || weapon.Contains("deagle") || weapon.Contains("revolver"))
            return _cfg.PistolPenaltyHP;

        if (weapon.Contains("ak47") || weapon.Contains("m4a1") || weapon.Contains("galil") ||
            weapon.Contains("famas") || weapon.Contains("aug") || weapon.Contains("sg556"))
            return _cfg.RiflePenaltyHP;

        if (weapon.Contains("negev") || weapon.Contains("m249"))
            return _cfg.LMGPenaltyHP;

        return _cfg.DefaultPenaltyHP;
    }

    private void RecalcInvisibleManBonusHP()
    {
        if (!_modeActive || _invisibleMan is null) return;
        if (!_cfg.BonusHealthPerEnemyEnabled) return;

        var pawn = _invisibleMan.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return;

        int team = GetTeamNum(_invisibleMan);
        if (team != 2 && team != 3) return;

        int enemies = CountEnemies(team);

        int per = Math.Max(1, _cfg.BonusHealthPerEnemyAmount);
        int total = Math.Max(per, per * Math.Max(1, enemies));

        try { pawn.MaxHealth = total; } catch { }
        pawn.Health = total;
        ForceHealthStateChanged(pawn);

        if (_cfg.DebugLog)
            Logger.LogInformation("[E1] Bonus HP applied: enemies={enemies}, per={per}, totalHP={hp}", enemies, per, total);
    }

    private static int GetTeamNum(CCSPlayerController p)
    {
        try { return p.TeamNum; } catch { return 0; }
    }

    private static int CountEnemies(int myTeam)
    {
        int enemies = 0;
        foreach (var p in Utilities.GetPlayers())
        {
            if (p == null || !p.IsValid) continue;
            int t = 0;
            try { t = p.TeamNum; } catch { t = 0; }
            if ((t == 2 || t == 3) && t != myTeam) enemies++;
        }
        return enemies;
    }

    private static void PrintTo(CCSPlayerController? who, string msg)
    {
        if (who == null || !who.IsValid) Server.PrintToChatAll(msg);
        else who.PrintToChat(msg);
    }
}
