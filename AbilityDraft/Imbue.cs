using DeadworksManaged.Api;

namespace AbilityDraft;

// Items that attach to one ability ("imbue" items). The shop sends "buydependentitem <item> <slot 0-3>", and the
// engine resolves that slot through the hero's own data, so with a drafted kit the purchase finds no target and
// nothing is bought. Here the request becomes an ordinary purchase of the same item - the shop's own checks and
// prices apply - and the bought item is then attached to the ability that really sits in the slot.
// The approach is from Binger4/Deadlock-Ability-Draft (MIT).
public sealed partial class DraftPlugin
{
    sealed record PendingImbue(string Item, EAbilitySlot Slot, float Deadline);

    const float ImbueSeconds = 3f;          // how long the ordinary purchase may take to land
    readonly Dictionary<int, PendingImbue> _imbues = new();

    /// <summary>Returns true when the command was an ability-targeted purchase the plugin took over.</summary>
    bool ImbueCommand(ClientConCommandEvent args)
    {
        if (args.Command != "buydependentitem" || args.Controller is not { } c || _phase != Phase.Match || !_kits.ContainsKey(c.Slot)) return false;
        if (args.Args.Length != 3 || !int.TryParse(args.Args[2], out int slot) || slot is < 0 or >= Slots) return false;
        string item = args.Args[1];
        if (c.GetHeroPawn() is not { } pawn || pawn.GetAbilityBySlot((EAbilitySlot)slot) is not CCitadelBaseAbility target) return false;
        // Moving or re-buying an item the hero already owns stays with the engine.
        if (pawn.AbilityComponent.FindAbilityByName(item) != null) return false;
        if (!ItemInfo.CanBeImbued(item) || !target.CanBeImbued || !target.CanBeImbuedBy(item))
        {
            Log($"imbue refused for slot {c.Slot}: {item} does not fit {target.AbilityName}");
            return true;
        }
        if (_imbues.TryGetValue(c.Slot, out var busy) && Now < busy.Deadline) return true;
        _imbues[c.Slot] = new PendingImbue(item, (EAbilitySlot)slot, Now + ImbueSeconds);
        Log($"imbue requested by slot {c.Slot}: {item} -> {target.AbilityName} (ability {slot + 1})");
        Server.ClientCommand(c.Slot, $"buyitem {item}");
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
