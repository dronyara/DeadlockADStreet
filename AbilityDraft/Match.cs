using DeadworksManaged.Api;

namespace AbilityDraft;

// Turning the vote into a running match.
public sealed partial class DraftPlugin
{
    Rules _forcedRules;       // test bridge only

    void StartMatch()
    {
        _phase = Phase.Match;
        Log($"MATCH START rules={_rules} kits={_kits.Count}");
        foreach (var (slot, kit) in _kits)
            if (Players.FromSlot(slot)?.GetHeroPawn() is { } pawn) ApplyKit(pawn, kit);
        GameRules.ChangeGameState(EGameState.GameInProgress);
        GameRules.SetGameStartTime(Now);
    }
}
