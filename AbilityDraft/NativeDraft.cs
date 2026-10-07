using System.Runtime.InteropServices;
using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

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

    // Clickable cards need the optional client addon (tools/build_cards.py): it gives every ability a twin, a real
    // item named ad_<ability> with the ability's icon and name. A click on a twin is safe and makes the client send
    // an ordinary "buyitem ad_<ability>", which the plugin answers with the ability. The server cannot see who has
    // the addon, and a client without it would not know the twins, so each player switches them on with /click.
    const string TwinPrefix = "ad_";
    readonly HashSet<ulong> _clickers = new();

    sealed class NativeSeat
    {
        public int Slot;
        public bool Bot, Ru = _ru;
        public bool Click;                                  // deals twin items instead of bare abilities
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
        _taken.Clear();
        foreach (var s in _seats.Values)
            _native[s.Slot] = new NativeSeat
            {
                Slot = s.Slot, Bot = s.Bot, Ru = s.Ru,
                Click = !s.Bot && Players.FromSlot(s.Slot) is { } c && _clickers.Contains(c.PlayerSteamId),
            };
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
                var pool = Candidates(ns, ult, avoidSeen: false, avoidOffered: false);
                var pick = pool[Random.Shared.Next(pool.Count)];
                _taken.Add(pick.Name);
                ReplaceAbility(pawn, ns.Round, pick.Name);
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
        Announce("STANDARD", L("Карта перезагружается, набор сохранён", "Reloading the map, your kit is kept"));
        Server.ExecuteCommand($"{BrawlCvar} 0");
        Server.ExecuteCommand($"{ActiveLaneCvar} 0");
        _phase = Phase.Restoring;
        _restoreMapLoaded = false;
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
    // The level change is queued, not instant: until the new map is up the old one still has every player in place,
    // which must not be mistaken for "everyone is back".
    bool _restoreMapLoaded;

    /// <summary>Puts every returning player back on their team and hero with their kit, then starts the match.</summary>
    void TickRestore()
    {
        if (!_restoreMapLoaded) return;
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
            BindHeroKit(r.Hero, c.Slot, r.Kit);             // so that the rebuilt hero is given the kit as its own
            if (c.TeamNum != r.Team) c.ChangeTeam(r.Team);
            if (pawn == null || pawn.HeroID != r.Hero) c.SelectHero(r.Hero);
            else ApplyKit(pawn, r.Kit);
            Log($"restore {c.PlayerName}: team {c.TeamNum}->{r.Team} hero {pawn?.HeroID.ToString() ?? "none"}->{r.Hero}");
        }
        if (!allBack && Now < _restoreDeadline) return;
        if (!Players.GetAll().Any(p => !p.IsBot))
        {
            // Everybody left during the reload: there is no match to start, the held map simply is the lobby again.
            Log("RESTORE abandoned: nobody came back, lobby is open");
            _restore.Clear();
            _kits.Clear();
            _rules = Rules.None;
            _phase = Phase.Lobby;
            return;
        }

        Log($"RESTORE done (all back={allBack}), starting Standard match");
        _restore.Clear();
        _phase = Phase.Match;
        GameRules.ChangeGameState(EGameState.GameInProgress);
        GameRules.SetGameStartTime(Now);
        Announce("STANDARD", L("Матч начался", "The match has started"));
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
                    if (ns.Click)
                    {
                        c.HudAnnounce(ns.Ru ? "ВЫБОР СПОСОБНОСТЕЙ" : "ABILITY DRAFT", ns.Ru ? "Кликни по карточке или напиши в чат 1, 2 или 3" : "Click a card or type 1, 2 or 3 in chat");
                        Chat.PrintToChat(c, ns.Ru
                            ? "[Draft] Выбор способностей: кликни по карточке или напиши в чат 1, 2 или 3. Карточки пустые? Нет аддона — напиши /click."
                            : "[Draft] Ability draft: click a card or type 1, 2 or 3 in chat. Blank cards? The addon is missing - type /click.");
                    }
                    else
                    {
                        c.HudAnnounce(ns.Ru ? "ВЫБОР СПОСОБНОСТЕЙ" : "ABILITY DRAFT", ns.Ru ? "Напиши в чат 1, 2 или 3 — клик по карточке не работает" : "Type 1, 2 or 3 in chat - clicking a card does nothing");
                        Chat.PrintToChat(c, ns.Ru
                            ? "[Draft] Выбор способностей: напиши в чат 1, 2 или 3 (слева, сверху, справа). «Прокрутить» меняет все три. Клик работает только с аддоном (/click)."
                            : "[Draft] Ability draft: type 1, 2 or 3 in chat (left, top, right). The Reroll button deals new cards. Clicking needs the addon (/click).");
                    }
                }
            }
            pawn.SetCurrency(ECurrencyType.EItemDraftRerolls, RerollsPerPick);
            ns.Seen.Clear();
            ns.RoundOpen = true;
        }
        Marshal.WriteInt32(state, RoundsTotalOffset, Slots);
        Marshal.WriteInt32(state, RoundsLeftOffset, Slots - ns.Round);

        bool ult = ns.Round == Slots - 1;
        Array.Clear(ns.Offer);                  // the cards being replaced no longer count as "on this player's screen"
        // Best case: cards nobody holds, nobody else is looking at, and this player has not seen this round.
        // Each fallback gives up one of those wishes; the last ones only matter when the pool is nearly used up.
        var pool = Candidates(ns, ult, avoidSeen: true, avoidOffered: true);
        if (pool.Count < Offers) { ns.Seen.Clear(); pool = Candidates(ns, ult, avoidSeen: false, avoidOffered: true); }
        if (pool.Count < Offers) pool = Candidates(ns, ult, avoidSeen: false, avoidOffered: false);
        for (int i = 0; i < Offers; i++)
        {
            var pick = pool[Random.Shared.Next(pool.Count)];
            pool.Remove(pick);
            ns.Seen.Add(pick.Name);
            ns.Offer[i] = pick;
            ns.Written[i] = MurmurHash2.HashLowerCase(ns.Click ? TwinPrefix + pick.Name : pick.Name, TokenSeed);
            Marshal.WriteInt32(data, i * OptionSize + OptionItemId, (int)ns.Written[i]);
            Marshal.WriteByte(data, i * OptionSize + OptionRare, 0);
            // A bare ability must not be clicked (the client crashes on it), a twin is there to be clicked.
            Marshal.WriteByte(data, i * OptionSize + OptionDrafted, (byte)(_markDrafted && !ns.Click ? 1 : 0));
            // Bit 1 of the upgrade bits is the "enhanced" badge the dealt item may have carried.
            Marshal.WriteByte(data, i * OptionSize + OptionUpgradeBits, (byte)(Marshal.ReadByte(data, i * OptionSize + OptionUpgradeBits) & ~2));
        }
        ns.NextBotAt = Now + BotThinkSeconds;
        (ns.Prev1, ns.Prev2, ns.Prev3) = (true, true, true);      // a key still held from the last pick does not count
        Log($"native slot {ns.Slot} round {ns.Round + 1}: {string.Join(", ", ns.Offer.Select(a => a!.Name))} (rerolls {pawn.GetCurrency(ECurrencyType.EItemDraftRerolls)}, {(ns.Click ? "twins" : $"drafted flag {(_markDrafted ? 1 : 0)}")})");
    }

    void PickNative(NativeSeat ns, CCitadelPlayerPawn pawn, int i)
    {
        if (ns.Offer[i] is not { } pick) return;
        if (_taken.Contains(pick.Name) && FreeAbilitiesLeft(ns, pick.Ult))
        {
            // Somebody got there first: no pick, new cards, and the reroll is on the house.
            if (!ns.Bot && Players.FromSlot(ns.Slot) is { } late)
                Chat.PrintToChat(late, ns.Ru ? $"[Draft] «{pick.Ru}» уже забрали — вот новые карточки, прокрутка бесплатная." : $"[Draft] {pick.En} is already taken - here are new cards, this reroll is free.");
            Log($"native slot {ns.Slot} round {ns.Round + 1}: {pick.Name} is already taken, free reroll");
            FreeReroll(ns, pawn);
            return;
        }
        bool ok = ReplaceAbility(pawn, ns.Round, pick.Name);
        ns.Kit[ns.Round] = pick.Name;
        _taken.Add(pick.Name);
        Log($"native slot {ns.Slot} round {ns.Round + 1}: took {pick.Name} ({pick.En}, {pick.HeroEn}){(ok ? "" : " - AddAbility FAILED")}");
        ns.Round++;
        ns.RoundOpen = false;
        Array.Clear(ns.Offer);
        ReplaceTakenCards(ns, pick);

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
            if (!ns.Bot && Players.FromSlot(ns.Slot) is { } c)
                Chat.PrintToChat(c, "[Draft] " + (ns.Ru ? "Твой набор: " : "Your kit: ") + string.Join(", ", ns.Kit.Select(k => AbilityPool.Find(k!)?.Title(ns.Ru) ?? k))
                    + (!_nativeThenStandard ? "" : ns.Ru ? ". Ждём остальных игроков." : ". Waiting for the other players."));
            if (!_nativeThenStandard)
            {
                // Street Brawl hands out unlocks through the TAB menu, which still lists the hero's own abilities
                // and so cannot unlock drafted ones. The whole kit starts unlocked instead.
                for (int slot = 0; slot < Slots; slot++)
                    if (pawn.GetAbilityBySlot((EAbilitySlot)slot) is CCitadelBaseAbility drafted && (drafted.UpgradeBits & 1) == 0)
                        drafted.UpgradeBits |= 1;
            }
            Log($"native slot {ns.Slot} finished: [{string.Join(", ", ns.Kit)}]");
        }
        else Marshal.WriteInt32(state, RoundsLeftOffset, Slots - ns.Round);
        // Dealing again is the only thing that makes the client redraw the cards.
        // In a borrowed phase there are no items to go back to: the finished player just gets the screen closed.
        if (ns.Round >= Slots && _nativeThenStandard) { CloseDraft(pawn); return; }
        if (ns.Bot || !_trainHud.ContainsKey(ns.Slot) || Players.FromSlot(ns.Slot) is not { } picker) { DealAfterPick(ns, pawn); return; }
        // The stock screen has a look for a taken card (it grows) and for the ones passed over (they fade), but only
        // shows it for a real purchase. A client with the addon is told which card was taken and puts those looks on
        // itself; the next deal waits for that to play.
        int seq = ++_pickSeq, seat = ns.Slot;
        UI.Panel(TrainPanelId).Set(picker.Recipients, "pick", $"{seq}|{i}");
        Timer.Once(PickShowSeconds.Seconds(), () =>
        {
            if (!_native.TryGetValue(seat, out var still) || still != ns || Players.FromSlot(seat) is not { } who || who.GetHeroPawn() is not { } p) return;
            DealAfterPick(ns, p);
            UI.Panel(TrainPanelId).Set(who.Recipients, "dealt", seq.ToString());
        });
    }

    const double PickShowSeconds = 0.6;
    int _pickSeq;

    /// <summary>The deal that follows a pick: a reroll on the house.</summary>
    void DealAfterPick(NativeSeat ns, CCitadelPlayerPawn pawn)
    {
        int rerolls = ns.Round >= Slots ? Math.Max(0, ns.SavedRerolls) : pawn.GetCurrency(ECurrencyType.EItemDraftRerolls);
        pawn.SetCurrency(ECurrencyType.EItemDraftRerolls, rerolls + 1);
        RerollNow(ns, pawn);
    }

    /// <summary>
    /// Has the engine deal again and, in the same tick, turns the new cards into abilities. Doing that a frame later
    /// (as the per-frame check does) reaches the client as two updates: it puts the engine's cards up, then sees
    /// them change and plays the reveal a second time, so cards that are already up blink one by one.
    /// </summary>
    void RerollNow(NativeSeat ns, CCitadelPlayerPawn pawn)
    {
        Native.Reroll(pawn);
        if (ns.Round >= Slots) return;      // the draft is over: these are the player's real items
        var state = DraftState.GetAddress(pawn.Handle);
        var data = Marshal.ReadIntPtr(state, StateOptionData);
        if (data == IntPtr.Zero || Marshal.ReadInt32(state, StateOptionCount) < Offers) return;
        for (int i = 0; i < Offers; i++)
        {
            if (ns.Written[i] != 0 && (uint)Marshal.ReadInt32(data, i * OptionSize + OptionItemId) == ns.Written[i]) continue;
            DealNative(ns, pawn, state, data);
            return;
        }
    }

    /// <summary>The Reroll button. Taken over so that the new cards are abilities from the first update on.</summary>
    bool NativeRerollClick(ClientConCommandEvent args)
    {
        if (args.Command != "itemdraftreroll" || args.Controller is not { } c) return false;
        if (!_native.TryGetValue(c.Slot, out var ns) || ns.Bot || c.GetHeroPawn() is not { } pawn) return false;
        if (ns.Round >= Slots || ns.Offer[0] == null) return false;
        RerollNow(ns, pawn);                // the engine function checks and spends the reroll itself
        return true;
    }

    readonly HashSet<string> _taken = new();        // abilities somebody has drafted in the current draft

    /// <summary>What may be dealt to this seat for a normal or an ultimate slot.</summary>
    List<AbilityDef> Candidates(NativeSeat ns, bool ult, bool avoidSeen, bool avoidOffered)
    {
        var offered = avoidOffered
            ? _native.Values.Where(o => o != ns).SelectMany(o => o.Offer).Where(a => a != null).Select(a => a!.Name).ToHashSet()
            : null;
        var list = Pool.Where(a => a.Ult == ult && !_taken.Contains(a.Name) && !ns.Kit.Contains(a.Name)
            && !(avoidSeen && ns.Seen.Contains(a.Name)) && !(offered != null && offered.Contains(a.Name))).ToList();
        if (list.Count >= Offers || avoidSeen || avoidOffered) return list;
        // Nothing unique is left (a tiny pool after the blacklist, or a very full server): repeats beat an empty screen.
        list = Pool.Where(a => a.Ult == ult && !ns.Kit.Contains(a.Name)).ToList();
        if (list.Count < Offers) list = AbilityPool.All.Where(a => a.Ult == ult && !ns.Kit.Contains(a.Name)).ToList();
        return list;
    }

    bool FreeAbilitiesLeft(NativeSeat ns, bool ult) =>
        Pool.Any(a => a.Ult == ult && !_taken.Contains(a.Name) && !ns.Kit.Contains(a.Name));

    /// <summary>Deals the hero new cards without spending one of their own rerolls.</summary>
    void FreeReroll(NativeSeat ns, CCitadelPlayerPawn pawn)
    {
        pawn.SetCurrency(ECurrencyType.EItemDraftRerolls, pawn.GetCurrency(ECurrencyType.EItemDraftRerolls) + 1);
        RerollNow(ns, pawn);
    }

    /// <summary>Everyone else who has the just-drafted ability on screen gets new cards, free of charge.</summary>
    void ReplaceTakenCards(NativeSeat picker, AbilityDef taken)
    {
        foreach (var other in _native.Values)
        {
            if (other == picker || other.Round >= Slots || !other.Offer.Any(a => a?.Name == taken.Name)) continue;
            if (Players.FromSlot(other.Slot)?.GetHeroPawn() is not { } pawn) continue;
            if (!other.Bot && Players.FromSlot(other.Slot) is { } c)
                Chat.PrintToChat(c, other.Ru ? $"[Draft] «{taken.Ru}» только что забрали — карточки заменены бесплатно." : $"[Draft] {taken.En} was just taken - your cards were replaced for free.");
            Log($"native slot {other.Slot}: {taken.Name} was taken by slot {picker.Slot}, free reroll");
            FreeReroll(other, pawn);
        }
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

    /// <summary>A click on a twin card is the client buying that item. Returns true when the command was one.</summary>
    bool NativeBuyClick(ClientConCommandEvent args)
    {
        if (args.Command != "buyitem" || args.Controller is not { } c) return false;
        // A twin is never really bought, whatever state the draft is in (a second click during the pick animation,
        // a card that has just been replaced): the engine must not see the purchase.
        if (!args.Args.Any(a => a.StartsWith(TwinPrefix, StringComparison.OrdinalIgnoreCase))) return false;
        if (!_native.TryGetValue(c.Slot, out var ns) || !ns.Click || ns.Round >= Slots || ns.Offer[0] == null) return true;
        int card = Array.FindIndex(ns.Offer, a => a != null && args.Args.Contains(TwinPrefix + a.Name, StringComparer.OrdinalIgnoreCase));
        if (card < 0) return true;
        Log($"native slot {ns.Slot} clicked card {card + 1} ({ns.Offer[card]!.Name})");
        if (c.GetHeroPawn() is { } pawn) PickNative(ns, pawn, card);
        return true;
    }

    /// <summary>Switches twin cards on or off for a seat; the cards on screen are dealt again to match.</summary>
    void SetClick(CCitadelPlayerController c, bool on)
    {
        if (on) _clickers.Add(c.PlayerSteamId); else _clickers.Remove(c.PlayerSteamId);
        if (!_native.TryGetValue(c.Slot, out var ns) || ns.Click == on) return;
        ns.Click = on;
        if (ns.Round < Slots && ns.Offer[0] != null && c.GetHeroPawn() is { } pawn) FreeReroll(ns, pawn);
    }

    [Command("click", Description = "Clickable draft cards (needs the client addon): /click, /click off")]
    public void CmdClick(CCitadelPlayerController caller, string mode = "")
    {
        bool on = mode.Length == 0 ? !_clickers.Contains(caller.PlayerSteamId) : mode is not ("off" or "0");
        SetClick(caller, on);
        bool ru = !_seats.TryGetValue(caller.Slot, out var s) || s.Ru;
        Chat.PrintToChat(caller, on
            ? ru ? "[Draft] Клик по карточкам включён. Нужен аддон AbilityDraft; если карточки пустые — напиши /click ещё раз." : "[Draft] Clickable cards are on. They need the AbilityDraft addon; if the cards are blank, type /click again."
            : ru ? "[Draft] Клик по карточкам выключен, выбирай цифрой в чате." : "[Draft] Clickable cards are off, pick with a digit in chat.");
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
