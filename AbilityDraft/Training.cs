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

    // ---- the click in the TAB upgrade view -------------------------------------------------------------------
    // Clients with the optional addon carry the Deadworks UI bridge and a small script (addon/panorama) that puts a
    // button over each ability icon and sends the command above. The script only runs where the server loads it,
    // and only acts while "train" is 1, so it stays quiet on other servers and outside a drafted match.
    const string TrainPanelId = "abilitydraft_hud";
    const string TrainLayout = "file://{resources}/layout/abilitydraft_hud.xml";
    readonly Dictionary<int, bool> _trainHud = new();       // slot -> the "train" value the client was last told
    readonly Dictionary<int, string> _trainSkills = new();  // slot -> the upgrade state the client was last told
    bool _trainHudWired;

    void SyncTrainHud()
    {
        foreach (int gone in _trainHud.Keys.Where(slot => Players.FromSlot(slot) == null).ToList())
        {
            _trainHud.Remove(gone);
            _trainSkills.Remove(gone);
        }
        if (!Native.CanTrain) return;
        foreach (var c in Players.GetAll())
        {
            if (c.IsBot || !UI.HasClientBootstrap(c.Slot)) continue;
            bool want = _phase == Phase.Match && _kits.ContainsKey(c.Slot);
            bool loaded = _trainHud.TryGetValue(c.Slot, out bool told);
            if (want && loaded && told) SendSkills(c);
            if (!loaded && !want || loaded && told == want) continue;
            if (!_trainHudWired)
            {
                _trainHudWired = true;
                // The client rebuilt its panels: load again and tell it everything anew.
                UI.ClientResync += slot => { _trainHud.Remove(slot); _trainSkills.Remove(slot); };
            }
            if (!loaded) UI.Panel(TrainPanelId).LoadXml(c.Recipients, TrainLayout);
            UI.Panel(TrainPanelId).Set(c.Recipients, "train", want ? "1" : "0");
            _trainHud[c.Slot] = want;
            _trainSkills.Remove(c.Slot);
            if (want) SendSkills(c);
            Log($"train hud for slot {c.Slot}: {(loaded ? "" : "loaded, ")}train={(want ? 1 : 0)}");
        }
    }

    static readonly int[] TierCost = [1, 2, 5];

    /// <summary>
    /// The stock upgrade pips are drawn for the hero's original abilities, so the client draws its own from this:
    /// per slot the upgrade bits (bit 0 unlocked, bits 1-3 the tiers) and whether the next step can be afforded.
    /// </summary>
    void SendSkills(CCitadelPlayerController c)
    {
        if (c.GetHeroPawn() is not { } pawn) return;
        int points = pawn.GetCurrency(ECurrencyType.EAbilityPoints), unlocks = pawn.GetCurrency(ECurrencyType.EAbilityUnlocks);
        var parts = new List<string>();
        for (int slot = 0; slot < Slots; slot++)
        {
            if (pawn.GetAbilityBySlot((EAbilitySlot)slot) is not CCitadelBaseAbility a) return;
            int bits = a.UpgradeBits, tier = (bits & 2) == 0 ? 0 : (bits & 4) == 0 ? 1 : (bits & 8) == 0 ? 2 : 3;
            bool canTrain = a.CanBeUpgraded && ((bits & 1) == 0 ? unlocks > 0 : tier < 3 && points >= TierCost[tier]);
            parts.Add($"{bits}:{(canTrain ? 1 : 0)}");
        }
        string state = string.Join(",", parts);
        if (_trainSkills.GetValueOrDefault(c.Slot) == state) return;
        _trainSkills[c.Slot] = state;
        UI.Panel(TrainPanelId).Set(c.Recipients, "skills", state);
    }
}
