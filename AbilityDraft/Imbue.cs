using System.Text.RegularExpressions;
using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace AbilityDraft;

// Items that attach to one ability ("imbue" items). The shop sends "buydependentitem <item> <slot 0-3>", and the
// engine resolves that slot through the hero's own data, so with a drafted kit the purchase finds no target and
// nothing is bought. Here the request becomes an ordinary purchase of the same item - the shop's own checks and
// prices apply - and the bought item is then attached to the ability that really sits in the slot.
// The ordinary purchase has to come from the client: Server.ClientCommand does not reach the shop (tried), and
// the API has no price to charge by hand. So the client's addon script is asked to send "buyitem" itself, which
// makes this a feature of the optional addon.
// The approach is from Binger4/Deadlock-Ability-Draft (MIT).
public sealed partial class DraftPlugin
{
    sealed record PendingImbue(string Item, EAbilitySlot Slot, float Deadline);

    const float ImbueSeconds = 3f;          // how long the ordinary purchase may take to land
    readonly Dictionary<int, PendingImbue> _imbues = new();
    int _imbueSeq;
    // A drafted ability arrives without the flag the hero's own abilities get, and the engine then turns the item
    // down although the pair fits (CanBeImbuedBy says so).
    static readonly SchemaAccessor<bool> AbilityCanBeImbued = new("CCitadelBaseAbility"u8, "m_bCanBeImbued"u8, 0);
    static readonly bool AbilityCanBeImbuedKnown = (long)AbilityCanBeImbued.GetAddress(IntPtr.Zero) > 0;

    /// <summary>Returns true when the command was an ability-targeted purchase the plugin took over.</summary>
    bool ImbueCommand(ClientConCommandEvent args)
    {
        if (args.Command != "buydependentitem" || args.Controller is not { } c || _phase != Phase.Match || !_kits.ContainsKey(c.Slot)) return false;
        if (HasNativeKit(c)) return false;      // the hero's own table holds the kit: the stock purchase works
        if (args.Args.Length != 3 || !int.TryParse(args.Args[2], out int slot) || slot is < 0 or >= Slots) return false;
        string item = args.Args[1];
        if (c.GetHeroPawn() is not { } pawn || pawn.GetAbilityBySlot((EAbilitySlot)slot) is not CCitadelBaseAbility target) return false;
        // Moving or re-buying an item the hero already owns stays with the engine.
        if (pawn.AbilityComponent.FindAbilityByName(item) != null) return false;
        // Whether the item fits the ability is the engine's call, made when the item is attached (a misfit is refunded
        // there). The plugin's own pre-checks turned down pairs the shop had offered, so they are only logged now.
        bool isImbue = Regex.IsMatch(item, @"\Aupgrade_[a-z0-9_]{1,100}\z") && ItemInfo.CanBeImbued(item);
        if (!isImbue)
        {
            Log($"imbue refused for slot {c.Slot}: {item} is not an ability-targeted item");
            return true;
        }
        if (_imbues.TryGetValue(c.Slot, out var busy) && Now < busy.Deadline) return true;
        if (!_trainHud.ContainsKey(c.Slot))
        {
            bool ru = _seats.TryGetValue(c.Slot, out var seat) ? seat.Ru : _ru;
            Chat.PrintToChat(c, ru ? "[Draft] Предметы «на способность» работают только с аддоном Ability Brawl." : "[Draft] Ability-targeted items need the Ability Brawl addon.");
            Log($"imbue refused for slot {c.Slot}: no addon on the client");
            return true;
        }
        _imbues[c.Slot] = new PendingImbue(item, (EAbilitySlot)slot, Now + ImbueSeconds);
        Log($"imbue requested by slot {c.Slot}: {item} -> {target.AbilityName} (ability {slot + 1}); target.CanBeImbued={target.CanBeImbued} target.CanBeImbuedBy={target.CanBeImbuedBy(item)}");
        if (AbilityCanBeImbuedKnown && !target.CanBeImbued) AbilityCanBeImbued.Set(target.Handle, true);
        UI.Panel(TrainPanelId).Set(c.Recipients, "buy", $"{++_imbueSeq}|{item}");
        return true;
    }

    /// <summary>Attaches a just-bought item to its ability once the purchase has gone through.</summary>
    void TickImbues()
    {
        foreach (var (slot, pending) in _imbues.ToList())
        {
            var pawn = Players.FromSlot(slot)?.GetHeroPawn();
            var item = pawn?.AbilityComponent.FindAbilityByName(pending.Item);
            if (pawn != null && item != null)
            {
                _imbues.Remove(slot);
                var result = pawn.ImbueItem(item, pending.Slot);
                if (result == ImbueResult.Success)
                {
                    Log($"imbue done for slot {slot}: {pending.Item} on ability {(int)pending.Slot + 1}");
                    continue;
                }
                // Paid for but not attached: give the money back rather than leave a useless item.
                bool refunded = pawn.SellItem(pending.Item, fullRefund: true);
                Log($"imbue failed for slot {slot}: {pending.Item} ({result}), refunded={refunded}");
            }
            else if (pawn == null || Now >= pending.Deadline)
            {
                _imbues.Remove(slot);
                Log($"imbue dropped for slot {slot}: {pending.Item} was not bought");
            }
        }
    }
}
