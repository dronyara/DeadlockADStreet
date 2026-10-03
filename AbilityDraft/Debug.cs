using DeadworksManaged.Api;

namespace AbilityDraft;

// Log file and a file-driven test bridge: lines written to %TEMP%\abilitydraft.cmd are run on the server.
// It lets the draft be exercised end to end with bots, without a game client.
public sealed partial class DraftPlugin
{
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "abilitydraft.log");
    static readonly string BridgePath = Path.Combine(Path.GetTempPath(), "abilitydraft.cmd");

    static void Log(string s)
    {
        Console.WriteLine("[AD] " + s);
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {s}{Environment.NewLine}"); } catch { }
    }

    void PollBridge()
    {
        string[] lines;
        try
        {
            if (!File.Exists(BridgePath)) return;
            lines = File.ReadAllLines(BridgePath);
            File.Delete(BridgePath);
        }
        catch { return; }

        foreach (var line in lines.Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var p = line.Split(' ', 2);
            var arg = p.Length > 1 ? p[1] : "";
            try { RunBridge(p[0], arg); }
            catch (Exception ex) { Log($"bridge '{line}' failed: {ex.Message}"); }
        }
    }

    void RunBridge(string cmd, string arg)
    {
        var a = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (cmd)
        {
            case "sv":
                Log($"> {arg}");
                Server.ExecuteCommand(arg);
                break;
            case "cvars":
                foreach (var v in Server.EnumerateConVars().Where(v => v.Name.Contains(arg, StringComparison.OrdinalIgnoreCase)))
                    Log($"  cvar {v.Name} = '{v.Value}' (def '{v.DefaultValue}', {v.Type}, flags 0x{v.Flags:x}) {v.Description}");
                foreach (var v in Server.EnumerateConCommands().Where(v => v.Name.Contains(arg, StringComparison.OrdinalIgnoreCase)))
                    Log($"  cmd  {v.Name} (flags 0x{v.Flags:x}) {v.Description}");
                break;
            case "state":
                Log($"STATE phase={_phase} rules={_rules} host={_hostSlot} gamestate={GameRules.GameState} mode={GameRules.GameMode} match={GameRules.MatchMode} " +
                    $"clock={GameRules.GameClock:F0} map={Server.MapName} players={string.Join(",", Players.GetAll().Select(c => $"{c.Slot}:{c.PlayerName}:t{c.TeamNum}:{c.GetHeroPawn()?.HeroID.ToString() ?? "nohero"}"))}");
                break;
            case "kits":
                foreach (var pawn in Players.GetAllPawns())
                    Log($"  kit slot={pawn.Controller?.Slot} hero={pawn.HeroID} lvl={pawn.Level} [{string.Join(", ", KitOf(pawn))}]");
                break;
            case "bot":
                Log($"fake client '{arg}' -> slot {Server.CreateFakeClient(arg.Length > 0 ? arg : "DraftBot")}");
                break;
            case "hero":
                {
                    // hero <slot> <team 2|3> <Heroes name>: seats a fake client so it can take part in the draft.
                    var c = Players.FromSlot(int.Parse(a[0])) ?? throw new Exception("no such slot");
                    c.ChangeTeam(int.Parse(a[1]));
                    c.SelectHero(Enum.Parse<Heroes>(a[2], true));
                    Log($"slot {a[0]} -> team {a[1]} hero {a[2]}");
                    break;
                }
            case "go":
                _phase = Phase.Match;
                GameRules.ChangeGameState(EGameState.GameInProgress);
                break;
            case "ents":
                foreach (var g in Entities.All.Where(e => e.DesignerName.Contains(arg, StringComparison.OrdinalIgnoreCase)).GroupBy(e => e.DesignerName))
                    Log($"  ent {g.Key} x{g.Count()}");
                break;
            case "draft":
                StartDraft();
                break;
            case "pick":
                Log($"bridge pick -> {Pick(_seats[int.Parse(a[0])], int.Parse(a[1]) - 1)}");
                break;
            case "reroll":
                Log($"bridge reroll -> {Reroll(_seats[int.Parse(a[0])], int.Parse(a[1]) - 1)}");
                break;
            case "vote":
                CastVote(int.Parse(a[0]), a[1].StartsWith('b') ? Rules.StreetBrawl : Rules.Standard);
                break;
            case "forcevote":
                // Bots do not vote; this stands in for a human majority in an all-bot test.
                _forcedRules = a[0].StartsWith('b') ? Rules.StreetBrawl : Rules.Standard;
                Log($"forced rules = {_forcedRules}");
                break;
            case "angles":
                foreach (var pawn in Players.GetAllPawns())
                    Log($"  slot={pawn.Controller?.Slot} view={pawn.ViewAngles} eye={pawn.EyeAngles} cam={pawn.CameraAngles} eyepos={pawn.EyePosition}");
                break;
            case "unlock":
                foreach (var pawn in Players.GetAllPawns())
                    foreach (var ab in pawn.AbilityComponent.Abilities.Where(ab => ab.IsSignature))
                        ab.UpgradeBits = 1;
                Log("all signature abilities unlocked");
                break;
            case "cast":
                {
                    var pawn = Players.FromSlot(int.Parse(a[0]))?.GetHeroPawn();
                    var slot = (EAbilitySlot)(int.Parse(a[1]) - 1);
                    Log($"cast slot={a[0]} ability={(pawn?.GetAbilityBySlot(slot) as CCitadelBaseAbility)?.AbilityName} -> {pawn?.ExecuteAbilityBySlot(slot)}");
                    break;
                }
            default:
                Log($"unknown bridge command '{cmd}'");
                break;
        }
    }
}
