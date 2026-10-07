using DeadworksManaged.Api;

namespace AbilityDraft;

// The lobby as a session: how many may join, one hero per player, what happens when a match is over, and /chaos.
public sealed partial class DraftPlugin
{
    // ---- player cap ------------------------------------------------------------------------------------------
    /// <summary>Turns away a player the lobby has no room for. Returns true when they were.</summary>
    bool LobbyIsFull(CCitadelPlayerController c)
    {
        int cap = Math.Max(_config.MaxPlayersStandard, _config.MaxPlayersStreetBrawl);
        int humans = Players.GetAll().Count(p => !p.IsBot);
        if (cap <= 0 || humans <= cap) return false;
        Log($"lobby is full ({humans - 1}/{cap}): turning away {c.PlayerName}");
        Chat.PrintToChat(c, L($"[Draft] Лобби заполнено ({cap} игроков).", $"[Draft] The lobby is full ({cap} players)."));
        int slot = c.Slot;
        Timer.Once(2.Seconds(), () => { if (Players.FromSlot(slot) is { IsBot: false }) Server.Kick(slot); });
        return true;
    }

    // ---- one hero per player ---------------------------------------------------------------------------------
    static string HeroKey(string name) => name.StartsWith("hero_", StringComparison.OrdinalIgnoreCase) ? name[5..].ToLowerInvariant() : name.ToLowerInvariant();

    /// <summary>True when another player already has this hero (and the config asks for unique heroes).</summary>
    bool HeroTaken(string heroName, int exceptSlot)
    {
        if (!_config.UniqueHeroes) return false;
        string key = HeroKey(heroName);
        return Players.GetAll().Any(p => p.Slot != exceptSlot && p.GetHeroPawn() is { } pawn && pawn.HeroID != 0 && HeroKey(pawn.HeroID.ToHeroName()) == key);
    }

    // ---- after the match -------------------------------------------------------------------------------------
    float _matchOverAt = -1f;

    /// <summary>A finished match starts the countdown to a fresh lobby.</summary>
    void MatchOver(EGameState state)
    {
        if (_phase != Phase.Match || _matchOverAt >= 0 || _config.SecondsAfterMatch <= 0) return;
        if (state is not (EGameState.PostGame or EGameState.PostGamePlayOfTheGame or EGameState.End or EGameState.Abandoned)) return;
        _matchOverAt = Now + _config.SecondsAfterMatch;
        Log($"MATCH OVER ({state}): new lobby in {_config.SecondsAfterMatch} s");
        Chat.PrintToChatAll(L($"[Draft] Матч окончен. Через {_config.SecondsAfterMatch} с лобби откроется заново, останется только лидер лобби.",
            $"[Draft] The match is over. In {_config.SecondsAfterMatch} s the lobby reopens and only the lobby leader stays."));
    }

    void TickMatchOver()
    {
        if (_matchOverAt < 0 || Now < _matchOverAt) return;
        _matchOverAt = -1f;
        int leader = Host();
        var leaving = Players.GetAll().Where(p => !p.IsBot && p.Slot != leader).Select(p => p.Slot).ToList();
        Log($"lobby reset after the match: leader stays (slot {leader}), kicking {leaving.Count}");
        foreach (int slot in leaving) Server.Kick(slot);
        _phase = Phase.Lobby;
        Server.ChangeLevel(Server.MapName);
    }

    /// <summary>
    /// Heroes a player can be: the ones the ability pool was built from, minus those not released yet. The API's own
    /// "available in game" flag reads false for every hero on build 6759, so the generated pool is the source.
    /// </summary>
    static IEnumerable<Heroes> PlayableHeroes()
    {
        var known = AbilityPool.HeroNames.Except(AbilityPool.Unreleased).Select(HeroKey).ToHashSet();
        return Enum.GetValues<Heroes>().Distinct().Where(h => known.Contains(HeroKey(h.ToHeroName())));
    }

    // ---- /chaos ----------------------------------------------------------------------------------------------
    [Command("chaos", Description = "Lobby leader: everyone gets a random hero and four random abilities, nobody picks anything")]
    public void CmdChaos(CCitadelPlayerController? caller = null)
    {
        if (caller != null && caller.Slot != Host()) throw new CommandException("Only the lobby leader can start it.");
        if (_phase != Phase.Lobby) throw new CommandException($"A draft is already running ({_phase}).");
        StartChaos();
    }

    void StartChaos()
    {
        LoadConfig();
        var players = Players.GetAll().ToList();
        if (players.Count == 0) throw new CommandException("Nobody is here.");

        var heroes = PlayableHeroes().ToList();
        var normals = Pool.Where(a => !a.Ult).ToList();
        var ults = Pool.Where(a => a.Ult).ToList();
        if (heroes.Count < players.Count || normals.Count < players.Count * (Slots - 1) || ults.Count < players.Count)
            throw new CommandException($"Not enough for {players.Count} players: {heroes.Count} heroes, {normals.Count} abilities and {ults.Count} ultimates are available.");
        Shuffle(heroes);
        Shuffle(normals);
        Shuffle(ults);

        _seats.Clear();
        _kits.Clear();
        _native.Clear();
        _taken.Clear();
        RestoreHeroTables();
        _chaosHeroes.Clear();
        _chaosSpare.Clear();
        _chaosSpare.AddRange(heroes.Skip(players.Count));
        _chaosCheckAt = Now + ChaosCheckSeconds;
        for (int i = 0; i < players.Count; i++)
        {
            var c = players[i];
            var kit = normals.Skip(i * (Slots - 1)).Take(Slots - 1).Append(ults[i]).ToArray();
            var names = kit.Select(a => a.Name).ToArray();
            var seat = new Seat { Slot = c.Slot, Bot = c.IsBot, Round = Slots };
            names.CopyTo(seat.Kit, 0);
            _seats[c.Slot] = seat;
            _kits[c.Slot] = names;
            foreach (var n in names) _taken.Add(n);
            // The hero's own table first: the hero is then created with the kit as its own (OnPawnHeroInitialized
            // covers the case where it is not).
            BindHeroKit(heroes[i], c.Slot, names);
            if (c.TeamNum is not (2 or 3)) c.ChangeTeam(SmallerTeam());
            c.SelectHero(heroes[i]);
            _chaosHeroes[c.Slot] = (heroes[i], 0);
            if (!c.IsBot)
                Chat.PrintToChat(c, "[Draft] " + heroes[i].ToDisplayName() + ": " + string.Join(", ", kit.Select(a => a.Title(seat.Ru))));
            Log($"chaos slot {c.Slot} ({c.PlayerName}): {heroes[i]} [{string.Join(", ", names)}]");
        }
        Announce("CHAOS", L("Случайные герои и способности", "Random heroes and abilities"));
        StartVote();
    }

    // A hero change does not always land (a team change may still be in flight), and a few heroes cannot be selected
    // on a given build at all. Until the match starts every assignment is checked: asked for again, then swapped
    // for a spare hero.
    const float ChaosCheckSeconds = 1.5f;
    const int ChaosTries = 4;
    readonly Dictionary<int, (Heroes Hero, int Tries)> _chaosHeroes = new();
    readonly List<Heroes> _chaosSpare = new();
    float _chaosCheckAt;

    void TickChaosHeroes()
    {
        if (_chaosHeroes.Count == 0 || Now < _chaosCheckAt) return;
        _chaosCheckAt = Now + ChaosCheckSeconds;
        foreach (var (slot, want) in _chaosHeroes.ToList())
        {
            if (Players.FromSlot(slot) is not { } c) { _chaosHeroes.Remove(slot); continue; }
            if (c.GetHeroPawn() is { } pawn && pawn.HeroID == want.Hero) { _chaosHeroes.Remove(slot); continue; }
            var hero = want.Hero;
            int tries = want.Tries + 1;
            if (tries > ChaosTries && _chaosSpare.Count > 0)
            {
                // Give the hero's table back and move the kit to a spare hero.
                foreach (var key in _heroOriginal.Keys.Where(k => k.Hero == want.Hero).ToList())
                {
                    if (BoundAbilityNode(key.Hero, key.Slot) is var node && node != IntPtr.Zero)
                    {
                        System.Runtime.InteropServices.Marshal.WriteIntPtr(node, NodeName, System.Runtime.InteropServices.Marshal.StringToHGlobalAnsi(_heroOriginal[key]));
                        System.Runtime.InteropServices.Marshal.WriteInt32(node, NodeToken, (int)MurmurHash2.HashLowerCase(_heroOriginal[key], TokenSeed));
                    }
                    _heroOriginal.Remove(key);
                }
                _heroOwner.Remove(want.Hero);
                hero = _chaosSpare[0];
                _chaosSpare.RemoveAt(0);
                tries = 0;
                Log($"chaos slot {slot}: {want.Hero} cannot be selected, trying {hero}");
                if (_kits.TryGetValue(slot, out var kit)) BindHeroKit(hero, slot, kit);
            }
            if (c.TeamNum is not (2 or 3)) c.ChangeTeam(SmallerTeam());
            c.SelectHero(hero);
            _chaosHeroes[slot] = (hero, tries);
        }
    }

    static void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}
