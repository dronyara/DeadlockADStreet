using DeadworksManaged.Api;

namespace AbilityDraft;

// Turning the vote into a running match.
public sealed partial class DraftPlugin
{
    // The engine reads this when a match is set up; with it on, the same dl_midtown map plays as Street Brawl
    // (rounds, buy phases with the item draft). It can be flipped on a live map: citadel_street_brawl_reset
    // rebuilds the match under the new mode, so heroes and teams survive and no map reload is needed.
    const string BrawlCvar = "citadel_gamemode_streetbrawl_enabled";

    Rules _forcedRules;       // test bridge only

    void StartMatch()
    {
        _phase = Phase.Match;
        Log($"MATCH START rules={_rules} kits={_kits.Count}");
        if (_rules == Rules.StreetBrawl)
        {
            Server.ExecuteCommand($"{BrawlCvar} 1");
            Server.ExecuteCommand("citadel_street_brawl_reset");
        }
        GameRules.ChangeGameState(EGameState.GameInProgress);
        GameRules.SetGameStartTime(Now);
        // A mode reset may rebuild heroes; OnPawnHeroInitialized re-applies kits then, this covers the case it does not.
        Timer.Once(1.Seconds(), ApplyAllKits);
    }

    void ApplyAllKits()
    {
        foreach (var (slot, kit) in _kits)
            if (Players.FromSlot(slot)?.GetHeroPawn() is { } pawn) ApplyKit(pawn, kit);
    }

    [Command("newdraft", Description = "Host: end the current match and reload the map into a fresh lobby")]
    public void CmdNewDraft(CCitadelPlayerController? caller = null)
    {
        if (caller != null && caller.Slot != Host()) throw new CommandException("Only the host can restart.");
        Log("host asked for a new lobby");
        _phase = Phase.Lobby;
        Server.ChangeLevel(Server.MapName);
    }
}
