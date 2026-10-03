using System.Runtime.InteropServices;
using DeadworksManaged.Api;

namespace AbilityDraft;

// The ability draft on the stock Street Brawl "item draft" screen - nothing is installed on the client.
// During the buy phase the engine deals every hero three item options and networks them. As soon as they are
// dealt, the item ids are overwritten with ability ids, so the stock screen draws ability cards. A pick comes
// in as an ability key (1-3): the ability goes into the slot and the engine reroll function deals the next
// three. After the fourth pick the patching stops and the same reroll hands the player their real item options.
public sealed partial class DraftPlugin
{
    const int RerollsPerPick = 3;

    // ItemDraftRoundState_t / ItemDraftOption_t layout (see PATCHING.md, checked with the "draftdump" bridge command).
    const int StateOptionCount = 8, StateOptionData = 16;
    const int OptionSize = 248, OptionItemId = 96, OptionRare = 241;
    const uint TokenSeed = 0x31415926;

    sealed class NativeSeat
    {
        public int Slot;
        public bool Bot, Ru = true;
        public int Round;
        public bool RoundOpen;                              // rerolls already granted for the current round
        public readonly string?[] Kit = new string?[Slots];
        public readonly AbilityDef?[] Offer = new AbilityDef?[Offers];
        public readonly uint[] Written = new uint[Offers];
        public readonly HashSet<string> Seen = new();       // shown this round, so a reroll brings new cards
        public int SavedRerolls = -1, SavedRoundsLeft, SavedRoundsTotal;
        public bool Prev1, Prev2, Prev3;
        public float NextBotAt;
    }

    readonly Dictionary<int, NativeSeat> _native = new();
    static readonly SchemaAccessor<int> DraftState = new("CCitadelPlayerPawn"u8, "m_ItemDraftRoundState"u8, 0);
    // The header "pick N of M" on the stock screen is drawn from these two counters.
    static readonly int RoundsLeftOffset = (int)new SchemaAccessor<int>("ItemDraftRoundState_t"u8, "m_nRoundsRemaining"u8, 0).GetAddress(IntPtr.Zero);
    static readonly int RoundsTotalOffset = (int)new SchemaAccessor<int>("ItemDraftRoundState_t"u8, "m_nRoundsTotal"u8, 0).GetAddress(IntPtr.Zero);

    // Standard rules have no draft screen of their own, so the draft borrows Street Brawl's first buy phase:
    // the match starts as Street Brawl, everyone drafts, then the mode is switched back and the match restarts.
    const float BorrowedDraftSeconds = 50f;      // must end before the buy phase does and the brawl round begins
    bool _nativeThenStandard;
    float _nativeDeadline;

    void ArmNativeDraft(bool thenStandard)
    {
        _nativeThenStandard = thenStandard;
        _nativeDeadline = Now + BorrowedDraftSeconds + StartDelaySeconds;
        _native.Clear();
        foreach (var s in _seats.Values)
            _native[s.Slot] = new NativeSeat { Slot = s.Slot, Bot = s.Bot, Ru = s.Ru };
        Log($"NATIVE DRAFT armed for slots {string.Join(",", _native.Keys)} (rounds offsets {RoundsLeftOffset}/{RoundsTotalOffset})");
    }

    void TickNativeDraft()
    {
        if (_native.Count == 0) return;
        foreach (var ns in _native.Values)
        {
            if (ns.Round >= Slots || Players.FromSlot(ns.Slot)?.GetHeroPawn() is not { } pawn) continue;
            var state = DraftState.GetAddress(pawn.Handle);
            var data = Marshal.ReadIntPtr(state, StateOptionData);
            if (data == IntPtr.Zero || Marshal.ReadInt32(state, StateOptionCount) < Offers)
            {
                Array.Clear(ns.Offer);
                continue;
            }
            bool dealt = false;
            for (int i = 0; i < Offers; i++)
                dealt |= ns.Written[i] == 0 || (uint)Marshal.ReadInt32(data, i * OptionSize + OptionItemId) != ns.Written[i];
            if (dealt) DealNative(ns, pawn, state, data);
            else if (ns.Bot && Now >= ns.NextBotAt) PickNative(ns, pawn, Random.Shared.Next(Offers));
        }
        if (_nativeThenStandard && (Now >= _nativeDeadline || _native.Values.All(ns => ns.Round >= Slots || Players.FromSlot(ns.Slot) == null)))
            SwitchToStandard();
    }

    void SwitchToStandard()
    {
        // Whoever ran out of time gets random abilities for the slots still open.
        foreach (var ns in _native.Values.Where(ns => ns.Round < Slots))
        {
            if (Players.FromSlot(ns.Slot)?.GetHeroPawn() is not { } pawn) continue;
            for (; ns.Round < Slots; ns.Round++)
            {
                bool ult = ns.Round == Slots - 1;
                var pool = AbilityPool.All.Where(a => a.Ult == ult && !ns.Kit.Contains(a.Name)).ToList();
                var pick = pool[Random.Shared.Next(pool.Count)];
                if (pawn.GetAbilityBySlot((EAbilitySlot)ns.Round) is CCitadelBaseAbility old) pawn.RemoveAbility(old);
                pawn.AddAbility(pick.Name, (ushort)ns.Round);
                ns.Kit[ns.Round] = pick.Name;
            }
            _kits[ns.Slot] = ns.Kit.Select(k => k!).ToArray();
            Log($"native slot {ns.Slot} timed out, kit completed at random: [{string.Join(", ", ns.Kit)}]");
        }
        _native.Clear();
        _nativeThenStandard = false;
        Log($"SWITCH TO STANDARD kits={_kits.Count}");

        // The mode follows this convar live; cycling the game state restarts the match under it.
        // (citadel_street_brawl_reset must NOT be used here: it starts a brawl round whatever the convar says.)
        Server.ExecuteCommand($"{BrawlCvar} 0");
        GameRules.ChangeGameState(EGameState.PreGameWait);
        GameRules.ChangeGameState(EGameState.GameInProgress);
        GameRules.SetGameStartTime(Now);
        Announce("STANDARD", "Матч начался · The match has started");
        Timer.Once(1.Seconds(), () =>
        {
            foreach (var pawn in Players.GetAllPawns())
            {
                // Items drafted while waiting for the others belong to the borrowed brawl phase, not to this match.
                foreach (var item in pawn.AbilityComponent.Abilities.Where(a => a.IsItem).Select(a => a.AbilityName).ToList())
                    pawn.RemoveItem(item);
            }
            ApplyAllKits();
        });
    }

    /// <summary>The engine has just dealt this hero three items: turn them into three abilities for the current slot.</summary>
    void DealNative(NativeSeat ns, CCitadelPlayerPawn pawn, IntPtr state, IntPtr data)
    {
        if (!ns.RoundOpen)
        {
            if (ns.SavedRerolls < 0)
            {
                ns.SavedRerolls = pawn.GetCurrency(ECurrencyType.EItemDraftRerolls);
                ns.SavedRoundsLeft = Marshal.ReadInt32(state, RoundsLeftOffset);
                ns.SavedRoundsTotal = Marshal.ReadInt32(state, RoundsTotalOffset);
                if (!ns.Bot && Players.FromSlot(ns.Slot) is { } c)
                {
                    c.HudAnnounce(ns.Ru ? "ВЫБОР СПОСОБНОСТЕЙ" : "ABILITY DRAFT", ns.Ru ? "Клавиши 1-3 — взять. Не кликай по карточкам!" : "Keys 1-3 pick. Do not click the cards!");
                    Chat.PrintToChat(c, ns.Ru
                        ? "[Draft] Выбор способностей: клавиши 1-3 берут карточку, «Прокрутить» меняет все три. НЕ кликай по карточке способности мышью — игра вылетит."
                        : "[Draft] Ability draft: keys 1-3 take a card, the Reroll button deals new ones. Do NOT click an ability card - the game will crash.");
                }
            }
            pawn.SetCurrency(ECurrencyType.EItemDraftRerolls, RerollsPerPick);
            ns.Seen.Clear();
            ns.RoundOpen = true;
        }
        Marshal.WriteInt32(state, RoundsTotalOffset, Slots);
        Marshal.WriteInt32(state, RoundsLeftOffset, Slots - ns.Round);

        bool ult = ns.Round == Slots - 1;
        var pool = AbilityPool.All.Where(a => a.Ult == ult && !ns.Kit.Contains(a.Name) && !ns.Seen.Contains(a.Name)).ToList();
        if (pool.Count < Offers)
        {
            ns.Seen.Clear();
            pool = AbilityPool.All.Where(a => a.Ult == ult && !ns.Kit.Contains(a.Name)).ToList();
        }
        for (int i = 0; i < Offers; i++)
        {
            var pick = pool[Random.Shared.Next(pool.Count)];
            pool.Remove(pick);
            ns.Seen.Add(pick.Name);
            ns.Offer[i] = pick;
            ns.Written[i] = MurmurHash2.HashLowerCase(pick.Name, TokenSeed);
            Marshal.WriteInt32(data, i * OptionSize + OptionItemId, (int)ns.Written[i]);
            Marshal.WriteByte(data, i * OptionSize + OptionRare, 0);
        }
        ns.NextBotAt = Now + BotThinkSeconds;
        (ns.Prev1, ns.Prev2, ns.Prev3) = (true, true, true);      // a key still held from the last pick does not count
        Log($"native slot {ns.Slot} round {ns.Round + 1}: {string.Join(", ", ns.Offer.Select(a => a!.Name))} (rerolls {pawn.GetCurrency(ECurrencyType.EItemDraftRerolls)})");
    }

    void PickNative(NativeSeat ns, CCitadelPlayerPawn pawn, int i)
    {
        if (ns.Offer[i] is not { } pick) return;
        if (pawn.GetAbilityBySlot((EAbilitySlot)ns.Round) is CCitadelBaseAbility old) pawn.RemoveAbility(old);
        bool ok = pawn.AddAbility(pick.Name, (ushort)ns.Round) != null;
        ns.Kit[ns.Round] = pick.Name;
        Log($"native slot {ns.Slot} round {ns.Round + 1}: took {pick.Name} ({pick.En}, {pick.HeroEn}){(ok ? "" : " - AddAbility FAILED")}");
        ns.Round++;
        ns.RoundOpen = false;
        Array.Clear(ns.Offer);

        var state = DraftState.GetAddress(pawn.Handle);
        if (ns.Round >= Slots)
        {
            // Hand the stock item draft back exactly as the engine left it.
            _kits[ns.Slot] = ns.Kit.Select(k => k!).ToArray();
            Marshal.WriteInt32(state, RoundsTotalOffset, ns.SavedRoundsTotal);
            Marshal.WriteInt32(state, RoundsLeftOffset, ns.SavedRoundsLeft);
            pawn.SetCurrency(ECurrencyType.EItemDraftRerolls, Math.Max(0, ns.SavedRerolls) + 1);
            if (!ns.Bot && Players.FromSlot(ns.Slot) is { } c)
                Chat.PrintToChat(c, "[Draft] " + (ns.Ru ? "Твой набор: " : "Your kit: ") + string.Join(", ", ns.Kit.Select(k => AbilityPool.Find(k!)?.Title(ns.Ru) ?? k))
                    + (!_nativeThenStandard ? "" : ns.Ru ? ". Ждём остальных — предметы с этого экрана в матч не попадут." : ". Waiting for the others - items from this screen will not carry over."));
            Log($"native slot {ns.Slot} finished: [{string.Join(", ", ns.Kit)}]");
        }
        else
        {
            Marshal.WriteInt32(state, RoundsLeftOffset, Slots - ns.Round);
            pawn.SetCurrency(ECurrencyType.EItemDraftRerolls, pawn.GetCurrency(ECurrencyType.EItemDraftRerolls) + 1);
        }
        // Dealing again is the only thing that makes the client redraw the cards; the reroll spends the one just added.
        Native.Reroll(pawn);
    }

    /// <summary>Ability keys 1-3 on the stock draft screen. Returns true when the input belonged to the native draft.</summary>
    bool NativeInput(AbilityAttemptEvent args)
    {
        if (!_native.TryGetValue(args.PlayerSlot, out var ns) || ns.Bot || ns.Round >= Slots || ns.Offer[0] == null) return false;
        args.BlockAll();
        bool k1 = args.IsHeld(InputButton.Ability1), k2 = args.IsHeld(InputButton.Ability2), k3 = args.IsHeld(InputButton.Ability3);
        int pressed = k1 && !ns.Prev1 ? 0 : k2 && !ns.Prev2 ? 1 : k3 && !ns.Prev3 ? 2 : -1;
        (ns.Prev1, ns.Prev2, ns.Prev3) = (k1, k2, k3);
        if (pressed >= 0 && args.Controller?.GetHeroPawn() is { } pawn) PickNative(ns, pawn, pressed);
        return true;
    }
}
