using System.Runtime.InteropServices;
using DeadworksManaged.Api;

namespace AbilityDraft;

// The ability draft on the stock Street Brawl "item draft" screen - nothing is installed on the client.
// During the buy phase the engine deals every hero three item options and networks them. As soon as they are
// dealt, the item ids are overwritten with ability ids, so the stock screen draws ability cards. A pick comes
// in as a digit typed in chat (1-3): the ability goes into the slot and the engine reroll function deals the
// next three. After the fourth pick the patching stops and the same reroll hands the player their real item options.
public sealed partial class DraftPlugin
{
    const int RerollsPerPick = 3;

    // ItemDraftRoundState_t / ItemDraftOption_t layout (see PATCHING.md, checked with the "draftdump" bridge command).
    const int StateOptionCount = 8, StateOptionData = 16, StateId = 112;
    const int OptionSize = 248, OptionItemId = 96, OptionUpgradeBits = 100, OptionDrafted = 240, OptionRare = 241;
    const uint TokenSeed = 0x31415926;

    // Experiment: an ability card is marked "already drafted" (ItemDraftOption_t.m_bHasBeenDrafted) in the hope that
    // the client then ignores a mouse click on it instead of building item data for it and crashing.
    // Bridge command "markdrafted 0|1" switches it at runtime; the next deal (or Reroll) shows the effect.
    static bool _markDrafted = true;

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
    InputButton _traceHeld;
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

    /// <summary>Ends a hero's stock draft the way the engine does after the last pick, so the client closes its screen.</summary>
    static void CloseDraft(CCitadelPlayerPawn pawn)
    {
        var state = DraftState.GetAddress(pawn.Handle);
        for (int i = 0; i < 6 && (Marshal.ReadInt32(state, StateOptionCount) > 0 || Marshal.ReadInt32(state, StateId) != -1); i++)
            Native.Advance(pawn);
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
        // Street Brawl strips the side lanes of their Walkers and Barracks for good, so a true Standard match needs
        // a fresh map. Everyone keeps their seat: team, hero and drafted kit are put back once the map is up again.
        _restore.Clear();
        foreach (var c in Players.GetAll())
        {
            if (c.IsBot || c.GetHeroPawn() is not { } pawn || !_kits.TryGetValue(c.Slot, out var kit)) continue;
            _restore[c.PlayerSteamId] = new Restore(c.TeamNum, pawn.HeroID, kit);
        }
        _native.Clear();
        _nativeThenStandard = false;
        Log($"SWITCH TO STANDARD: reloading {Server.MapName}, restoring {_restore.Count} players");
        if (_restore.Count == 0)
        {
            // Nobody to carry over (a bots-only test): there is no match to rebuild.
            _phase = Phase.Lobby;
            Server.ChangeLevel(Server.MapName);
            return;
        }
        Announce("STANDARD", "Карта перезагружается, набор сохранён · Reloading the map, your kit is kept");
        Server.ExecuteCommand($"{BrawlCvar} 0");
        Server.ExecuteCommand($"{ActiveLaneCvar} 0");
        _phase = Phase.Restoring;
        Server.ChangeLevel(Server.MapName);
    }

    // ---- restoring seats after the reload into Standard --------------------------------------------------------
    sealed record Restore(int Team, Heroes Hero, string[] Kit)
    {
        public float NextFixAt;
    }

    const float RestoreSeconds = 150f;      // slow machines need about a minute just to load the map
    readonly Dictionary<ulong, Restore> _restore = new();
    float _restoreDeadline;

    /// <summary>Puts every returning player back on their team and hero with their kit, then starts the match.</summary>
    void TickRestore()
    {
        bool allBack = true;
        foreach (var (steamId, r) in _restore)
        {
            var c = Players.GetAll().FirstOrDefault(p => !p.IsBot && p.PlayerSteamId == steamId);
            if (c == null) { allBack = false; continue; }
            var pawn = c.GetHeroPawn();
            bool ok = c.TeamNum == r.Team && pawn != null && pawn.HeroID == r.Hero && KitOf(pawn).SequenceEqual(r.Kit);
            if (ok) continue;
            allBack = false;
            if (Now < r.NextFixAt) continue;
            r.NextFixAt = Now + 3f;                         // hero changes take a moment to land; do not spam them
            _kits[c.Slot] = r.Kit;                          // re-applied by OnPawnHeroInitialized when the hero is rebuilt
            if (c.TeamNum != r.Team) c.ChangeTeam(r.Team);
            if (pawn == null || pawn.HeroID != r.Hero) c.SelectHero(r.Hero);
            else ApplyKit(pawn, r.Kit);
            Log($"restore {c.PlayerName}: team {c.TeamNum}->{r.Team} hero {pawn?.HeroID.ToString() ?? "none"}->{r.Hero}");
        }
        if (!allBack && Now < _restoreDeadline) return;

        Log($"RESTORE done (all back={allBack}), starting Standard match");
        _restore.Clear();
        _phase = Phase.Match;
        GameRules.ChangeGameState(EGameState.GameInProgress);
        GameRules.SetGameStartTime(Now);
        Announce("STANDARD", "Матч начался · The match has started");
        Timer.Once(1.Seconds(), ApplyAllKits);
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
                    c.HudAnnounce(ns.Ru ? "ВЫБОР СПОСОБНОСТЕЙ" : "ABILITY DRAFT", ns.Ru ? "Напиши в чат 1, 2 или 3. Не кликай по карточкам!" : "Type 1, 2 or 3 in chat. Do not click the cards!");
                    Chat.PrintToChat(c, ns.Ru
                        ? "[Draft] Выбор способностей: напиши в чат 1, 2 или 3 (слева, сверху, справа). «Прокрутить» меняет все три. НЕ кликай по карточке мышью — игра вылетит."
                        : "[Draft] Ability draft: type 1, 2 or 3 in chat (left, top, right). The Reroll button deals new cards. Do NOT click a card - the game will crash.");
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
            Marshal.WriteByte(data, i * OptionSize + OptionDrafted, (byte)(_markDrafted ? 1 : 0));
            // Bit 1 of the upgrade bits is the "enhanced" badge the dealt item may have carried.
            Marshal.WriteByte(data, i * OptionSize + OptionUpgradeBits, (byte)(Marshal.ReadByte(data, i * OptionSize + OptionUpgradeBits) & ~2));
        }
        ns.NextBotAt = Now + BotThinkSeconds;
        (ns.Prev1, ns.Prev2, ns.Prev3) = (true, true, true);      // a key still held from the last pick does not count
        Log($"native slot {ns.Slot} round {ns.Round + 1}: {string.Join(", ", ns.Offer.Select(a => a!.Name))} (rerolls {pawn.GetCurrency(ECurrencyType.EItemDraftRerolls)}, drafted flag {(_markDrafted ? 1 : 0)})");
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
            // The real items dealt next must be clickable: do not leave the experimental flag on the cards.
            var cards = Marshal.ReadIntPtr(state, StateOptionData);
            int cardCount = cards == IntPtr.Zero ? 0 : Math.Min(Offers, Marshal.ReadInt32(state, StateOptionCount));
            for (int card = 0; card < cardCount; card++)
                Marshal.WriteByte(cards, card * OptionSize + OptionDrafted, 0);
            Marshal.WriteInt32(state, RoundsTotalOffset, ns.SavedRoundsTotal);
            Marshal.WriteInt32(state, RoundsLeftOffset, ns.SavedRoundsLeft);
            pawn.SetCurrency(ECurrencyType.EItemDraftRerolls, Math.Max(0, ns.SavedRerolls) + 1);
            if (!ns.Bot && Players.FromSlot(ns.Slot) is { } c)
                Chat.PrintToChat(c, "[Draft] " + (ns.Ru ? "Твой набор: " : "Your kit: ") + string.Join(", ", ns.Kit.Select(k => AbilityPool.Find(k!)?.Title(ns.Ru) ?? k))
                    + (!_nativeThenStandard ? "" : ns.Ru ? ". Ждём остальных игроков." : ". Waiting for the other players."));
            Log($"native slot {ns.Slot} finished: [{string.Join(", ", ns.Kit)}]");
        }
        else
        {
            Marshal.WriteInt32(state, RoundsLeftOffset, Slots - ns.Round);
            pawn.SetCurrency(ECurrencyType.EItemDraftRerolls, pawn.GetCurrency(ECurrencyType.EItemDraftRerolls) + 1);
        }
        // Dealing again is the only thing that makes the client redraw the cards; the reroll spends the one just added.
        // In a borrowed phase there are no items to go back to: the finished player just gets the screen closed.
        if (ns.Round >= Slots && _nativeThenStandard) CloseDraft(pawn);
        else Native.Reroll(pawn);
    }

    /// <summary>
    /// A bare "1", "2" or "3" typed in chat is a choice. This is the dependable input on the stock draft screen,
    /// which keeps the keyboard for itself: ability keys only get through when the screen happens to lose focus.
    /// The same digits answer the rules vote and the text-menu draft. The message itself is not shown to anyone.
    /// </summary>
    public override HookResult OnChatMessage(ChatMessage message)
    {
        var text = message.ChatText.Trim();
        if (text.Length != 1 || text[0] < '1' || text[0] > '3') return HookResult.Continue;
        int choice = text[0] - '1', slot = message.SenderSlot;

        if (_native.TryGetValue(slot, out var ns) && !ns.Bot && ns.Round < Slots && ns.Offer[0] != null)
        {
            if (Players.FromSlot(slot)?.GetHeroPawn() is { } pawn) PickNative(ns, pawn, choice);
            return HookResult.Stop;
        }
        if (_seats.TryGetValue(slot, out var s) && !s.Bot)
        {
            if (_phase == Phase.Voting && choice < 2) { CastVote(slot, choice == 0 ? Rules.Standard : Rules.StreetBrawl); return HookResult.Stop; }
            if (_phase == Phase.Drafting && Pick(s, choice)) return HookResult.Stop;
        }
        return HookResult.Continue;
    }

    /// <summary>Ability keys 1-3 on the stock draft screen. Returns true when the input belonged to the native draft.</summary>
    bool NativeInput(AbilityAttemptEvent args)
    {
        if (TraceClientCommands && !args.Controller!.IsBot && args.HeldButtons != _traceHeld)
        {
            _traceHeld = args.HeldButtons;
            Log($"input slot={args.PlayerSlot} held=[{args.HeldButtons}] native={_native.TryGetValue(args.PlayerSlot, out var t)} round={t?.Round} offer={t?.Offer[0]?.Name}");
        }
        if (!_native.TryGetValue(args.PlayerSlot, out var ns) || ns.Bot || ns.Round >= Slots || ns.Offer[0] == null) return false;
        args.BlockAll();
        bool k1 = args.IsHeld(InputButton.Ability1), k2 = args.IsHeld(InputButton.Ability2), k3 = args.IsHeld(InputButton.Ability3);
        int pressed = k1 && !ns.Prev1 ? 0 : k2 && !ns.Prev2 ? 1 : k3 && !ns.Prev3 ? 2 : -1;
        (ns.Prev1, ns.Prev2, ns.Prev3) = (k1, k2, k3);
        if (pressed >= 0) Log($"native slot {ns.Slot} key {pressed + 1}");
        if (pressed >= 0 && args.Controller?.GetHeroPawn() is { } pawn) PickNative(ns, pawn, pressed);
        return true;
    }
}
