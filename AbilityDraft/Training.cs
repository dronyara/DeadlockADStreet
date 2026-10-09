using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace AbilityDraft;

// Unlocking and upgrading drafted abilities by command. The stock "trainorupgradeability <1-4>" looks the slot up
// in the hero's own data and then searches the pawn for that ability, which a drafted kit no longer has. The same
// engine function is called here with the ability that really sits in the slot, so costs, requirements and effects
// stay stock. The approach is from Binger4/Deadlock-Ability-Draft (MIT).
public sealed partial class DraftPlugin
{
    /// <summary>Handles the training commands of a player with a drafted kit. Returns true when the command is used up.</summary>
    bool TrainCommand(ClientConCommandEvent args)
    {
        if (args.Controller is not { } c || !_kits.ContainsKey(c.Slot)) return false;
        // With the kit written into the hero's own table the stock commands find the right ability by themselves.
        if (HasNativeKit(c)) return false;
        // The stock HUD click sends these for the hero's original ability; left alone they would do nothing or,
        // when an original happens to sit in another slot, train the wrong one.
        if (args.Command is "upgrade_ability" or "upgrade_ability_in_field") return true;
        if (args.Command != "trainorupgradeability") return false;
        if (!Native.CanTrain || args.Args.Length < 2 || !int.TryParse(args.Args[^1], out int slot) || slot is < 1 or > Slots) return true;
        if (c.GetHeroPawn() is not { } pawn || pawn.GetAbilityBySlot((EAbilitySlot)(slot - 1)) is not CCitadelBaseAbility ability) return true;
        int before = ability.UpgradeBits;
        Native.Train(pawn, ability);
        Log($"train slot {c.Slot} ability {slot} ({ability.AbilityName}): bits {before} -> {ability.UpgradeBits}, points left {pawn.GetCurrency(ECurrencyType.EAbilityPoints)}");
        return true;
    }

    // ---- the client's addon script ---------------------------------------------------------------------------
    // Clients with the optional addon carry the Deadworks UI bridge and a small script (addon/panorama). The server
    // loads it for a player from the moment their draft begins and tells it:
    //   "train"  1 while the player holds a drafted kit in a running match: clicks in the TAB view are live
    //   "skills" the kit's upgrade state, which the script shows on the game's own upgrade pips - the client works
    //            those out for the hero's original abilities and would leave them empty
    //   "buy"    buy this item (Imbue.cs)
    // The script only runs where a server loads it, so it stays quiet everywhere else.
    const string TrainPanelId = "abilitydraft_hud";
    const string TrainLayout = "file://{resources}/layout/abilitydraft_hud.xml";
    readonly Dictionary<int, bool> _trainHud = new();       // slot -> the "train" value the client was last told
    readonly Dictionary<int, string> _trainSkills = new();  // slot -> the "skills" value the client was last told
    bool _trainHudWired;

    void SyncTrainHud()
    {
        foreach (int gone in _trainHud.Keys.Where(slot => Players.FromSlot(slot) == null).ToList())
        {
            _trainHud.Remove(gone);
            _trainSkills.Remove(gone);
        }
        foreach (var c in Players.GetAll())
        {
            if (c.IsBot || !UI.HasClientBootstrap(c.Slot)) continue;
            bool train = _phase == Phase.Match && _kits.ContainsKey(c.Slot);
            bool loaded = _trainHud.TryGetValue(c.Slot, out bool told);
            // In a draft the script is loaded once the first cards are out, not when the match starts.
            bool drafting = _native.TryGetValue(c.Slot, out var seat) && seat.SavedRerolls >= 0;
            if (!loaded && !train && !drafting) continue;
            if (!_trainHudWired)
            {
                _trainHudWired = true;
                // The client lost its panels and asked for them again. Deadworks reloads the layout and replays the
                // fields by itself, so the player stays on the list of those who have the script - taking them off
                // it made the plugin treat them as addon-less for the two seconds the reload took.
                UI.ClientResync += slot => _trainSkills.Remove(slot);
            }
            if (!loaded) UI.Panel(TrainPanelId).LoadXml(c.Recipients, TrainLayout);
            if (!loaded || told != train)
            {
                UI.Panel(TrainPanelId).Set(c.Recipients, "train", train ? "1" : "0");
                _trainHud[c.Slot] = train;
                Log($"addon script for slot {c.Slot}: {(loaded ? "" : "loaded, ")}train={(train ? 1 : 0)}");
            }
            if (train) SendSkills(c);
        }
    }

    static readonly int[] TierCost = [1, 2, 5];

    /// <summary>Per slot "<upgrade bits>:<can the next step be afforded>"; bit 0 is unlocked, bits 1-3 the tiers.</summary>
    void SendSkills(CCitadelPlayerController c)
    {
        if (c.GetHeroPawn() is not { } pawn) return;
        int points = pawn.GetCurrency(ECurrencyType.EAbilityPoints), unlocks = pawn.GetCurrency(ECurrencyType.EAbilityUnlocks);
        var parts = new List<string>();
        for (int slot = 0; slot < Slots; slot++)
        {
            if (pawn.GetAbilityBySlot((EAbilitySlot)slot) is not CCitadelBaseAbility a) return;
            int bits = a.UpgradeBits, tier = (bits & 2) == 0 ? 0 : (bits & 4) == 0 ? 1 : (bits & 8) == 0 ? 2 : 3;
            bool can = a.CanBeUpgraded && ((bits & 1) == 0 ? unlocks > 0 : tier < 3 && points >= TierCost[tier]);
            parts.Add($"{bits}:{(can ? 1 : 0)}");
        }
        string state = string.Join(",", parts);
        if (_trainSkills.GetValueOrDefault(c.Slot) == state) return;
        _trainSkills[c.Slot] = state;
        UI.Panel(TrainPanelId).Set(c.Recipients, "skills", state);
    }
}
