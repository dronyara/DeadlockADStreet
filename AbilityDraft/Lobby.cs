using DeadworksManaged.Api;

namespace AbilityDraft;

// Lobby helpers: a server without a matchmaking lobby has no team or hero screens of its own to rely on.
public sealed partial class DraftPlugin
{
    [Command("hero", Description = "Lobby: pick a hero by name, e.g. /hero haze or /hero \"grey talon\"")]
    public void CmdHero(CCitadelPlayerController caller, string name)
    {
        if (_phase != Phase.Lobby) throw new CommandException("Heroes are locked once the draft has started.");
        var key = name.Replace(" ", "").Replace("&", "");
        var hero = Enum.GetValues<Heroes>()
            .Where(h => h.GetHeroData()?.AvailableInGame == true)
            .Select(h => (Hero: h, Shown: h.ToDisplayName().Replace(" ", "").Replace("&", "")))
            .Where(h => h.Shown.StartsWith(key, StringComparison.OrdinalIgnoreCase) || h.Hero.ToString().StartsWith(key, StringComparison.OrdinalIgnoreCase))
            .Select(h => (Heroes?)h.Hero)
            .FirstOrDefault() ?? throw new CommandException($"No playable hero matches '{name}'.");
        if (caller.TeamNum is not (2 or 3)) caller.ChangeTeam(SmallerTeam());
        caller.SelectHero(hero);
        Chat.PrintToChatAll($"[Draft] {caller.PlayerName}: {hero.ToDisplayName()}");
    }

    [Command("team", Description = "Lobby: /team amber | /team sapphire")]
    public void CmdTeam(CCitadelPlayerController caller, string team)
    {
        if (_phase != Phase.Lobby) throw new CommandException("Teams are locked once the draft has started.");
        int t = team.ToLowerInvariant() switch
        {
            "2" or "a" or "amber" => 2,
            "3" or "s" or "sapphire" => 3,
            _ => throw new CommandException("Use /team amber or /team sapphire."),
        };
        caller.ChangeTeam(t);
        Chat.PrintToChatAll($"[Draft] {caller.PlayerName} -> {(t == 2 ? "Amber" : "Sapphire")}");
    }

    static int SmallerTeam()
    {
        int amber = Players.GetAll().Count(p => p.TeamNum == 2), sapphire = Players.GetAll().Count(p => p.TeamNum == 3);
        return sapphire < amber ? 3 : 2;
    }

    // Heroes and teams are part of the draft's premise, so they stay fixed from /draft until the next lobby.
    public override HookResult OnClientConCommand(ClientConCommandEvent args)
    {
        if (TraceClientCommands) Log($"clientcmd slot={args.Controller?.Slot} {args.Command} [{string.Join(" ", args.Args)}]");
        if (NativeBuyClick(args) || TrainCommand(args) || ImbueCommand(args)) return HookResult.Stop;
        return _phase is Phase.Drafting or Phase.Voting or Phase.Starting or Phase.Restoring && args.Command is "selecthero" or "changeteam" or "jointeam"
            ? HookResult.Stop
            : HookResult.Continue;
    }

    // Switched on by an empty file: abilitydraft.trace in %TEMP%, or AbilityDraft.trace next to the plugin DLL.
    static readonly bool TraceClientCommands = File.Exists(Path.Combine(Path.GetTempPath(), "abilitydraft.trace"))
        || File.Exists(Path.Combine(PluginDir, "AbilityDraft.trace"));
}
