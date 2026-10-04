using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace AbilityDraft;

/// <summary>
/// Ability Draft for Deadlock (server side, Deadworks plugin).
/// Players join and pick heroes as usual; the host types /draft; everyone drafts four abilities, one slot at a
/// time, from three random offers (each offer can be rerolled, Street Brawl style); then the lobby votes for the
/// rules (Standard or Street Brawl) and the match starts with the drafted kits.
/// </summary>
public sealed partial class DraftPlugin : DeadworksPluginBase
{
    public override string Name => "Ability Draft";

    // ---- rules -----------------------------------------------------------------------------------------------
    const int Slots = 4;                 // Signature1..4; the last one is the ultimate
    const int Offers = 3;
    const int RerollsPerOffer = 2;
    const float DraftSeconds = 120f;
    const float VoteSeconds = 25f;
    const float StartDelaySeconds = 5f;
    const float BotThinkSeconds = 1.5f;

    enum Phase { Lobby, Drafting, Voting, Starting, Match, Restoring }
    enum Rules { None, Standard, StreetBrawl }

    sealed class Seat
    {
        public int Slot;
        public bool Bot;
        public bool Ru = true;
        public readonly string?[] Kit = new string?[Slots];
        public int Round;                                   // which ability slot is being drafted (0-3)
        public readonly AbilityDef?[] Offer = new AbilityDef?[Offers];
        public readonly int[] Rerolls = new int[Offers];
        public readonly HashSet<string> Seen = new();       // offered this round, so a reroll never repeats
        public Rules Vote;
        public float NextBotAt;
        public CPointWorldText? Billboard;
        public bool Prev1, Prev2, Prev3;
        public bool PanelUp;
        public bool Done => Round >= Slots;
    }

    // ---- state -----------------------------------------------------------------------------------------------
    Phase _phase = Phase.Lobby;
    float _phaseEnd;
    int _hostSlot = -1;
    Rules _rules = Rules.None;
    readonly Dictionary<int, Seat> _seats = new();
    // Finished kits, re-applied whenever the engine rebuilds a hero (respawn-time resets, a Street Brawl match reset).
    readonly Dictionary<int, string[]> _kits = new();
    IHandle? _loop;
    float _nextTimerText;

    static float Now => GlobalVars.CurTime;

    /// <summary>The host is the first human on the server; after a plugin hot reload it is found again here.</summary>
    int Host()
    {
        if (_hostSlot < 0 || Players.FromSlot(_hostSlot) is not { IsBot: false })
            _hostSlot = Players.GetAll().Where(p => !p.IsBot).Select(p => p.Slot).DefaultIfEmpty(-1).Min();
        return _hostSlot;
    }

    // ---- lifecycle ------------------------------------------------------------------------------------------
    public override void OnLoad(bool isReload)
    {
        Log($"=== Ability Draft loaded (reload={isReload}) pool={AbilityPool.All.Length} abilities ===");
        Log(Native.Load(Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "managed", "plugins", "AbilityDraft.signatures.json")));
        Server.AddEngineLogListener(OnEngineLog);
        if (isReload) BeginMap();
    }

    public override void OnUnload()
    {
        _loop?.Cancel();
        Server.RemoveEngineLogListener(OnEngineLog);
        foreach (var s in _seats.Values) HidePanel(s);
        if (_panelWired) UI.ClientResync -= OnClientResync;
    }

    public override void OnPrecacheResources()
    {
        // Any hero may end up casting any other hero's ability, so all of their particles and sounds must be loaded.
        foreach (var hero in AbilityPool.HeroNames) Precache.AddHero(hero);
    }

    public override void OnStartupServer()
    {
        BeginMap();
        EnableSteamConnect();
    }

    void BeginMap()
    {
        _loop?.Cancel();
        _seats.Clear();
        Server.ExecuteCommand("sv_hibernate_when_empty 0");
        Server.ExecuteCommand("citadel_allow_duplicate_heroes 1");
        Server.ExecuteCommand($"{BrawlCvar} 0");          // every map starts as a Standard lobby; the vote decides
        Server.ExecuteCommand($"{ActiveLaneCvar} 0");
        LoadConfig();
        if (_phase == Phase.Restoring && _restore.Count > 0)
        {
            // The reload into Standard after a draft on the Street Brawl screen: hold the lobby until everyone is back.
            _restoreDeadline = Now + RestoreSeconds;
            _kits.Clear();
            Log($"map reloaded for Standard, waiting for {_restore.Count} players");
        }
        else
        {
            _phase = Phase.Lobby;
            _kits.Clear();
            _restore.Clear();
            _rules = Rules.None;
        }
        _loop = Timer.Every(0.25.Seconds(), Tick);
    }

    // The engine runs straight from map load into GameInProgress on a server without a matchmaking lobby.
    // The lobby and the draft are held in PreGameWait so no troopers spawn and no clock runs until the vote is done.
    public override bool OnGameStateChanging(EGameState currentState, EGameState newState)
    {
        // Asked again every tick while the hold lasts, so nothing is logged here.
        return newState != EGameState.GameInProgress || _phase == Phase.Match;
    }

    public override void OnGameStateChanged(EGameState newState) => Log($"game state -> {newState}");

    // ---- players ---------------------------------------------------------------------------------------------
    public override void OnClientFullConnect(ClientFullConnectEvent args)
    {
        var c = args.Controller;
        if (c == null) return;
        Log($"player connected slot={args.Slot} name={c.PlayerName} bot={c.IsBot} mapchange={args.IsMapChangeReconnect}");
        if (c.IsBot) return;
        if (_phase == Phase.Lobby)
            Chat.PrintToChat(c, args.Slot == Host()
                ? "[Draft] Ты хост. Когда все выберут героев, напиши /draft. | You are the host: type /draft when everyone has a hero."
                : "[Draft] Выбери героя и жди, пока хост начнёт драфт. | Pick a hero and wait for the host to start the draft.");
        if (_phase == Phase.Lobby && args.Slot == Host() && SteamConnectReady)
            Chat.PrintToChat(c, $"[Draft] Друзья заходят без проброса портов: connect {_steamConnect} | Friends join with: connect {_steamConnect}");
    }

    public override void OnClientDisconnect(ClientDisconnectedEvent args)
    {
        if (args.IsMapChange) return;
        if (_seats.Remove(args.Slot, out var gone)) ClearBillboard(gone);
        _kits.Remove(args.Slot);
        if (args.Slot == _hostSlot)
        {
            _hostSlot = Players.GetAll().FirstOrDefault(p => !p.IsBot && p.Slot != args.Slot)?.Slot ?? -1;
            if (_hostSlot >= 0) Chat.PrintToChat(_hostSlot, "[Draft] Теперь ты хост. | You are the host now.");
        }
    }

    public override void OnPawnHeroInitialized(CCitadelPlayerPawn pawn)
    {
        var c = pawn.Controller;
        if (c == null || !_kits.TryGetValue(c.Slot, out var kit)) return;
        ApplyKit(pawn, kit);
    }

    // ---- main loop -------------------------------------------------------------------------------------------
    void Tick()
    {
        PollBridge();
        switch (_phase)
        {
            case Phase.Drafting: TickDraft(); break;
            case Phase.Voting: TickVote(); break;
            case Phase.Starting: if (Now >= _phaseEnd) StartMatch(); break;
            case Phase.Restoring: TickRestore(); break;
        }
    }

    // ---- draft -----------------------------------------------------------------------------------------------
    [Command("draft", Description = "Host: start the ability draft for everyone on the server")]
    public void CmdDraft(CCitadelPlayerController? caller = null)
    {
        if (caller != null && caller.Slot != Host()) throw new CommandException("Only the host can start the draft.");
        if (_phase != Phase.Lobby) throw new CommandException($"Draft is already running ({_phase}).");
        StartDraft();
    }

    void StartDraft()
    {
        _seats.Clear();
        _kits.Clear();
        foreach (var c in Players.GetAll())
        {
            if (c.GetHeroPawn() is not { } pawn || pawn.HeroID == 0)
            {
                if (!c.IsBot) Chat.PrintToChat(c, "[Draft] Нет героя — ты пропускаешь драфт. | No hero picked, you sit this draft out.");
                continue;
            }
            _seats[c.Slot] = new Seat { Slot = c.Slot, Bot = c.IsBot };
        }
        if (_seats.Count == 0) throw new CommandException("Nobody has a hero yet.");
        _native.Clear();
        _taken.Clear();
        LoadConfig();
        Log($"DRAFT START seats={string.Join(",", _seats.Keys)}");
        StartVote();
    }

    /// <summary>The draft as a menu in front of the player, before the match: used for Standard rules,
    /// which have no stock draft screen, and whenever the native functions could not be found.</summary>
    void BeginTextDraft()
    {
        foreach (var s in _seats.Values)
        {
            s.NextBotAt = Now + BotThinkSeconds;
            DealRound(s);
        }
        _phase = Phase.Drafting;
        _phaseEnd = Now + DraftSeconds;
        _nextTimerText = 0;
        Announce("ABILITY DRAFT", "Выбери 4 способности · Pick 4 abilities");
        foreach (var s in _seats.Values) ShowOffer(s);
    }

    void BeginCountdown()
    {
        foreach (var s in _seats.Values) HidePanel(s);
        _phase = Phase.Starting;
        _phaseEnd = Now + StartDelaySeconds;
        Announce(RulesName(_rules).ToUpperInvariant(), $"Матч начнётся через {StartDelaySeconds:0} с · Match starts in {StartDelaySeconds:0} s");
    }

    void DealRound(Seat s)
    {
        s.Seen.Clear();
        for (int i = 0; i < Offers; i++)
        {
            s.Offer[i] = null;
            s.Rerolls[i] = RerollsPerOffer;
        }
        for (int i = 0; i < Offers; i++) s.Offer[i] = Roll(s);
    }

    /// <summary>A random ability for the current round: ultimates only in the last slot, never one already owned or shown.</summary>
    AbilityDef? Roll(Seat s)
    {
        bool ult = s.Round == Slots - 1;
        var pool = Pool.Where(a => a.Ult == ult && !_taken.Contains(a.Name) && !s.Kit.Contains(a.Name) && !s.Seen.Contains(a.Name)).ToList();
        if (pool.Count == 0) return null;
        var pick = pool[Random.Shared.Next(pool.Count)];
        s.Seen.Add(pick.Name);
        return pick;
    }

    bool Reroll(Seat s, int i)
    {
        if (_phase != Phase.Drafting || s.Done || i < 0 || i >= Offers || s.Rerolls[i] <= 0) return false;
        var next = Roll(s);
        if (next == null) return false;
        s.Rerolls[i]--;
        Log($"slot {s.Slot} round {s.Round + 1} reroll offer {i + 1}: {s.Offer[i]?.Name} -> {next.Name} (left {s.Rerolls[i]})");
        s.Offer[i] = next;
        ShowOffer(s);
        return true;
    }

    bool Pick(Seat s, int i)
    {
        if (_phase != Phase.Drafting || s.Done || i < 0 || i >= Offers || s.Offer[i] == null) return false;
        var a = s.Offer[i]!;
        if (_taken.Contains(a.Name))
        {
            // Somebody took it while it sat on this menu: swap the card for free instead of handing out a duplicate.
            if (Roll(s) is { } other) s.Offer[i] = other;
            ShowOffer(s);
            return false;
        }
        s.Kit[s.Round] = a.Name;
        _taken.Add(a.Name);
        Log($"slot {s.Slot} round {s.Round + 1} picked {a.Name} ({a.En}, {a.HeroEn})");
        s.Round++;
        s.NextBotAt = Now + BotThinkSeconds;
        if (s.Done) FinishSeat(s);
        else
        {
            DealRound(s);
            ShowOffer(s);
        }
        return true;
    }

    void FinishSeat(Seat s)
    {
        var kit = s.Kit.Select(k => k!).ToArray();
        _kits[s.Slot] = kit;
        var c = Players.FromSlot(s.Slot);
        var pawn = c?.GetHeroPawn();
        if (pawn != null) ApplyKit(pawn, kit);
        if (c != null && !s.Bot)
        {
            Chat.PrintToChat(c, "[Draft] " + (s.Ru ? "Твой набор: " : "Your kit: ") + string.Join(", ", kit.Select(k => AbilityPool.Find(k)?.Title(s.Ru) ?? k)));
            ShowWaiting(s);
        }
    }

    void TickDraft()
    {
        foreach (var s in _seats.Values.Where(s => !s.Done).ToList())
        {
            if (Players.FromSlot(s.Slot) == null) { ClearBillboard(s); _seats.Remove(s.Slot); continue; }
            if ((s.Bot || Now >= _phaseEnd) && Now >= s.NextBotAt)
            {
                // Bots, and humans who ran out of time, take a random offer.
                while (!s.Done && (Now >= _phaseEnd || s.Bot))
                {
                    if (!Pick(s, Random.Shared.Next(Offers))) break;
                    if (s.Bot && Now < _phaseEnd) break;          // bots pick one slot per think so the log reads in order
                }
            }
        }
        if (Now >= _nextTimerText)
        {
            _nextTimerText = Now + 1f;
            int left = Math.Max(0, (int)MathF.Ceiling(_phaseEnd - Now));
            foreach (var s in _seats.Values.Where(s => s.PanelUp)) UI.Panel(PanelId).Set(RecipientFilter.Single(s.Slot), "ad_timer", $"{left}");
        }
        if (_seats.Count == 0) { CancelDraft("everyone left"); return; }
        if (_seats.Values.All(s => s.Done)) BeginCountdown();
    }

    void CancelDraft(string why)
    {
        Log($"draft cancelled: {why}");
        foreach (var s in _seats.Values) HidePanel(s);
        _seats.Clear();
        _phase = Phase.Lobby;
    }

    [Command("draftcancel", Description = "Host: cancel the running draft and go back to the lobby")]
    public void CmdCancel(CCitadelPlayerController? caller = null)
    {
        if (caller != null && caller.Slot != Host()) throw new CommandException("Only the host can cancel the draft.");
        if (_phase is not (Phase.Drafting or Phase.Voting)) throw new CommandException("No draft is running.");
        CancelDraft("host");
        Chat.PrintToChatAll("[Draft] Драфт отменён. | Draft cancelled.");
    }

    // ---- applying a kit --------------------------------------------------------------------------------------
    void ApplyKit(CCitadelPlayerPawn pawn, string[] kit)
    {
        try
        {
            var before = KitOf(pawn);
            if (before.SequenceEqual(kit)) return;
            // Clear all four slots first: a drafted ability may already sit in another slot of this hero.
            var bits = new int[Slots];
            for (int i = 0; i < Slots; i++)
            {
                if (pawn.GetAbilityBySlot((EAbilitySlot)i) is not CCitadelBaseAbility old) continue;
                bits[i] = old.UpgradeBits;
                if (!pawn.RemoveAbility(old)) Log($"  could not remove {old.AbilityName} from slot {i + 1}");
            }
            for (int i = 0; i < Slots; i++)
                if (!PutAbility(pawn, i, kit[i], bits[i]))
                    Log($"  could not add {kit[i]} to slot {i + 1}");
            Log($"kit applied slot={pawn.Controller?.Slot} hero={pawn.HeroID}: [{string.Join(", ", before)}] -> [{string.Join(", ", KitOf(pawn))}]");
        }
        catch (Exception ex)
        {
            Log($"ApplyKit failed: {ex}");
        }
    }

    /// <summary>
    /// Adds an ability to a slot and gives it the unlock and upgrade state the slot had. A fresh ability arrives
    /// locked, and the ability points already spent on the slot are gone with the old one - in Street Brawl, where
    /// points are handed out and spent before the draft is over, that left drafted abilities locked for good.
    /// </summary>
    static bool PutAbility(CCitadelPlayerPawn pawn, int slot, string name, int upgradeBits)
    {
        if (pawn.AddAbility(name, (ushort)slot) == null) return false;
        if (upgradeBits != 0 && pawn.GetAbilityBySlot((EAbilitySlot)slot) is CCitadelBaseAbility fresh && fresh.UpgradeBits != upgradeBits)
            fresh.UpgradeBits = upgradeBits;
        return true;
    }

    /// <summary>Swaps the ability in one slot, keeping the slot's unlock and upgrade state.</summary>
    static bool ReplaceAbility(CCitadelPlayerPawn pawn, int slot, string name)
    {
        int bits = 0;
        if (pawn.GetAbilityBySlot((EAbilitySlot)slot) is CCitadelBaseAbility old)
        {
            bits = old.UpgradeBits;
            pawn.RemoveAbility(old);
        }
        return PutAbility(pawn, slot, name, bits);
    }

    static string[] KitOf(CCitadelPlayerPawn pawn) =>
        Enumerable.Range(0, Slots).Select(i => (pawn.GetAbilityBySlot((EAbilitySlot)i) as CCitadelBaseAbility)?.AbilityName ?? "-").ToArray();

    // ---- vote ------------------------------------------------------------------------------------------------
    void StartVote()
    {
        _phase = Phase.Voting;
        _phaseEnd = Now + VoteSeconds;
        Log("VOTE START");
        Announce("ГОЛОСОВАНИЕ · VOTE", "1 — Standard   2 — Street Brawl");
        foreach (var s in _seats.Values)
        {
            s.Vote = Rules.None;
            s.Prev1 = s.Prev2 = s.Prev3 = true;     // a key still held from the last pick must not count as a vote
            if (s.Bot) continue;
            ShowVote(s);
        }
        if (_seats.Values.All(s => s.Bot)) _phaseEnd = Now + 1f;
    }

    void CastVote(int slot, Rules r)
    {
        if (_phase != Phase.Voting || !_seats.TryGetValue(slot, out var s) || r == Rules.None) return;
        s.Vote = r;
        Log($"slot {slot} votes {r}");
        var name = Players.FromSlot(slot)?.PlayerName ?? $"#{slot}";
        Chat.PrintToChatAll($"[Draft] {name}: {RulesName(r)}");
        ShowVote(s);
    }

    void TickVote()
    {
        var humans = _seats.Values.Where(s => !s.Bot).ToList();
        if (Now < _phaseEnd && humans.Any(s => s.Vote == Rules.None)) return;

        int std = humans.Count(s => s.Vote == Rules.Standard), brawl = humans.Count(s => s.Vote == Rules.StreetBrawl);
        // A tie goes to the host's vote, and to Standard when the host did not vote.
        _rules = brawl > std ? Rules.StreetBrawl
            : std > brawl ? Rules.Standard
            : _seats.TryGetValue(Host(), out var h) && h.Vote != Rules.None ? h.Vote : Rules.Standard;
        if (_forcedRules != Rules.None) (_rules, _forcedRules) = (_forcedRules, Rules.None);
        Log($"VOTE RESULT standard={std} brawl={brawl} -> {_rules}");

        Chat.PrintToChatAll($"[Draft] Правила: {RulesName(_rules)} ({std}:{brawl}).");
        // The draft runs on the stock Street Brawl screen once the match is up; the text menu is the fallback when it cannot.
        if (Native.Ready) BeginCountdown();
        else BeginTextDraft();
    }

    static string RulesName(Rules r) => r == Rules.StreetBrawl ? "Street Brawl" : "Standard";

    [Command("vote", Description = "Vote for the rules: /vote standard | /vote brawl")]
    public void CmdVote(CCitadelPlayerController caller, string rules)
    {
        if (_phase != Phase.Voting) throw new CommandException("There is no vote right now.");
        var r = rules.ToLowerInvariant() switch
        {
            "1" or "s" or "std" or "standard" or "стандарт" => Rules.Standard,
            "2" or "b" or "sb" or "brawl" or "street" or "streetbrawl" => Rules.StreetBrawl,
            _ => throw new CommandException("Use /vote standard or /vote brawl."),
        };
        CastVote(caller.Slot, r);
    }

    // ---- input (works without the Deadworks launcher UI) ------------------------------------------------------
    // While drafting, ability keys 1-3 take the offer and holding Reload (R) with 1-3 rerolls it.
    // While voting, 1 = Standard and 2 = Street Brawl. Hero abilities stay blocked the whole time.
    public override void OnAbilityAttempt(AbilityAttemptEvent args)
    {
        if (NativeInput(args)) return;
        if (_phase is not (Phase.Drafting or Phase.Voting)) return;
        if (!_seats.TryGetValue(args.PlayerSlot, out var s) || s.Bot) return;
        args.BlockAll();

        bool k1 = args.IsHeld(InputButton.Ability1), k2 = args.IsHeld(InputButton.Ability2), k3 = args.IsHeld(InputButton.Ability3);
        int pressed = k1 && !s.Prev1 ? 0 : k2 && !s.Prev2 ? 1 : k3 && !s.Prev3 ? 2 : -1;
        (s.Prev1, s.Prev2, s.Prev3) = (k1, k2, k3);
        if (pressed < 0) return;

        if (_phase == Phase.Voting)
        {
            if (pressed < 2) CastVote(s.Slot, pressed == 0 ? Rules.Standard : Rules.StreetBrawl);
        }
        else if (args.IsHeld(InputButton.Reload)) Reroll(s, pressed);
        else Pick(s, pressed);
    }

    [Command("pick", Description = "Draft: take offer 1-3")]
    public void CmdPick(CCitadelPlayerController caller, int offer)
    {
        if (!_seats.TryGetValue(caller.Slot, out var s) || !Pick(s, offer - 1)) throw new CommandException("Nothing to pick.");
    }

    [Command("reroll", "rr", Description = "Draft: replace offer 1-3 (2 per offer)")]
    public void CmdReroll(CCitadelPlayerController caller, int offer)
    {
        if (!_seats.TryGetValue(caller.Slot, out var s) || !Reroll(s, offer - 1)) throw new CommandException("No rerolls left for that offer.");
    }

    [Command("lang", Description = "Draft text language: /lang ru | /lang en")]
    public void CmdLang(CCitadelPlayerController caller, string lang)
    {
        if (!_seats.TryGetValue(caller.Slot, out var s)) throw new CommandException("You are not in the draft.");
        s.Ru = lang.StartsWith("ru", StringComparison.OrdinalIgnoreCase);
        if (_phase == Phase.Drafting && !s.Done) ShowOffer(s);
    }

    static void Announce(string title, string text)
    {
        foreach (var c in Players.GetAll().Where(c => !c.IsBot)) c.HudAnnounce(title, text);
    }
}
