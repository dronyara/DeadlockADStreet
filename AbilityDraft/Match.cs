using DeadworksManaged.Api;

namespace AbilityDraft;

// Turning the vote into a running match.
public sealed partial class DraftPlugin
{
    // The engine reads this when a match is set up; with it on, the same dl_midtown map plays as Street Brawl
    // (rounds, buy phases with the item draft). It can be flipped on a live map: citadel_street_brawl_reset
    // rebuilds the match under the new mode, so heroes and teams survive and no map reload is needed.
    const string BrawlCvar = "citadel_gamemode_streetbrawl_enabled";
    // Street Brawl narrows the match to one lane through this convar and never widens it again - not on a mode
    // switch, not even on a map change. 0 means all lanes.
    const string ActiveLaneCvar = "citadel_active_lane";

    Rules _forcedRules;       // test bridge only

    void StartMatch()
    {
        _phase = Phase.Match;
        Log($"MATCH START rules={_rules} kits={_kits.Count}");
        // With the native functions available the draft always happens on the Street Brawl screen;
        // for Standard rules that phase is only borrowed and the mode is switched back afterwards.
        bool nativeDraft = Native.Ready && _kits.Count == 0;
        if (_rules == Rules.StreetBrawl || nativeDraft)
        {
            Server.ExecuteCommand($"{BrawlCvar} 1");
            Server.ExecuteCommand("citadel_street_brawl_reset");
            if (nativeDraft) ArmNativeDraft(thenStandard: _rules != Rules.StreetBrawl);
            _wallsUp = false;       // from here the brawl runs the spawn walls: up for a buy phase, down for a round
        }
        else SetSpawnWalls(false);
        GameRules.ChangeGameState(EGameState.GameInProgress);
        GameRules.SetGameStartTime(Now);
        // A mode reset may rebuild heroes; OnPawnHeroInitialized re-applies kits then, this covers the case it does not.
        Timer.Once(1.Seconds(), ApplyAllKits);
    }

    void ApplyAllKits()
    {
        foreach (var (slot, kit) in _kits)
        {
            if (Players.FromSlot(slot)?.GetHeroPawn() is not { } pawn) continue;
            ApplyKit(pawn, kit);
            if (_rules != Rules.StreetBrawl) continue;
            // Street Brawl hands out its unlocks before a kit made ahead of the match (/chaos, the text draft) is on
            // the hero, so such a kit starts unlocked, as a kit drafted on the brawl screen does.
            for (int i = 0; i < Slots; i++)
                if (pawn.GetAbilityBySlot((EAbilitySlot)i) is CCitadelBaseAbility a && (a.UpgradeBits & 1) == 0) a.UpgradeBits |= 1;
        }
    }

    [Command("newdraft", Description = "Lobby leader: end the current match and reload the map into a fresh lobby")]
    public void CmdNewDraft(CCitadelPlayerController? caller = null)
    {
        if (caller != null && caller.Slot != Host()) throw new CommandException("Only the lobby leader can restart.");
        Log("lobby leader asked for a new lobby");
        _phase = Phase.Lobby;
        Server.ChangeLevel(Server.MapName);
    }
}
