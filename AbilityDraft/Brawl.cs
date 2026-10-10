using System.Runtime.InteropServices;
using DeadworksManaged.Api;

namespace AbilityDraft;

// The Street Brawl controller inside the game rules: which phase the brawl is in and when the next one starts.
public sealed partial class DraftPlugin
{
    static int Offset(ReadOnlySpan<byte> cls, ReadOnlySpan<byte> field) => (int)new SchemaAccessor<int>(cls, field, 0).GetAddress(IntPtr.Zero);

    static readonly int BrawlInRules = Offset("CCitadelGameRules"u8, "m_tStreetBrawl"u8);
    static readonly int BrawlState = Offset("CStreetBrawlController"u8, "m_eStreetBrawlState"u8);
    static readonly int BrawlStateStart = Offset("CStreetBrawlController"u8, "m_flStreetBrawlStateStartTime"u8);
    static readonly int BrawlNextState = Offset("CStreetBrawlController"u8, "m_flNextStateTime"u8);
    static readonly int BrawlRound = Offset("CStreetBrawlController"u8, "m_iRound"u8);
    static readonly int BrawlCountdown = Offset("CStreetBrawlController"u8, "m_iLastBuyCountDown"u8);
    static readonly bool BrawlKnown = BrawlInRules > 0 && BrawlNextState > 0 && BrawlState > 0;

    static IntPtr Brawl => GameRules.Pointer + BrawlInRules;
    static float ReadFloat(IntPtr at) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(at));

    // ---- no clock on the draft --------------------------------------------------------------------------------
    // The draft sits in the brawl's buy phase, which the engine ends on a clock (a minute on build 6766). While
    // somebody is still drafting, the end of the phase is pushed back every tick, so the timer on the screen stands
    // still at its full length. Once the last ability is taken the pushing stops and the phase runs out as usual -
    // under Street Brawl rules that is the time everyone has for their items.
    const int BrawlBuyPhase = 2;            // EStreetBrawlGameState: 1 = the few seconds before it, 3 = the round
    static readonly SchemaAccessor<float> BrawlNextStateTime = new("CStreetBrawlController"u8, "m_flNextStateTime"u8, 0);
    float _buyLength;

    float DraftDeadline() => _config.DraftTimeLimit > 0 ? Now + StartDelaySeconds + _config.DraftTimeLimit : float.MaxValue;

    void HoldBuyPhase()
    {
        if (!BrawlKnown || Marshal.ReadInt32(Brawl, BrawlState) != BrawlBuyPhase) return;
        if (_buyLength <= 0)
        {
            _buyLength = ReadFloat(Brawl + BrawlNextState) - ReadFloat(Brawl + BrawlStateStart);
            if (_buyLength is not (>= 5f and <= 600f)) _buyLength = 60f;
            Log($"buy phase of {_buyLength:0} s is held until everyone has drafted");
        }
        BrawlNextStateTime.Set(Brawl, Now + _buyLength);
    }

    // ---- spawn walls -------------------------------------------------------------------------------------------
    // The walls that keep both teams in their base until a match starts are brushes the map switches on for the
    // pregame. On a server without a matchmaking lobby the map never does (the engine rushes through that state on
    // load), so the lobby, the wait for everyone after the reload into Standard and the countdown had no walls.
    static readonly string[] SpawnWalls = ["amber_spawn_block_brush", "sapphire_spawn_block_brush"];
    bool _wallsUp;

    void SetSpawnWalls(bool up)
    {
        int count = 0;
        foreach (var name in SpawnWalls)
            foreach (var wall in Entities.ByName(name))
            {
                wall.AcceptInput(up ? "Enable" : "Disable");
                count++;
            }
        if (count == 0) return;             // the map's entities are not there yet: the next tick tries again
        if (_wallsUp != up) Log($"spawn walls {(up ? "up" : "down")} ({count} brushes)");
        _wallsUp = up;
    }

    void TickSpawnWalls()
    {
        if (!_wallsUp && _phase != Phase.Match && GameRules.GameState == EGameState.PreGameWait) SetSpawnWalls(true);
    }

    readonly HashSet<int> _testNoBot = new();       // test bridge only
    bool _testKeepEmpty;

    // ---- what every hero was dealt, once per buy phase ------------------------------------------------------------
    // For reports of "the draft screen did not open": the log then says whether the server had cards out for that
    // player (the screen is the client's to open) or not.
    int _loggedBrawlState;

    void TickBrawlLog()
    {
        if (!BrawlKnown || _phase != Phase.Match || !GameRules.IsValid) return;
        int state = Marshal.ReadInt32(Brawl, BrawlState);
        if (state == _loggedBrawlState) return;
        _loggedBrawlState = state;
        Log($"brawl state {state}, round {Marshal.ReadInt32(Brawl, BrawlRound)}");
        if (state == BrawlBuyPhase) Timer.Once(2.Seconds(), LogDraftStates);
    }

    void LogDraftStates()
    {
        foreach (var pawn in Players.GetAllPawns())
        {
            var st = DraftState.GetAddress(pawn.Handle);
            Log($"  draft slot={pawn.Controller?.Slot} {pawn.Controller?.PlayerName} team={pawn.TeamNum} alive={pawn.IsAlive} cards={Marshal.ReadInt32(st, StateOptionCount)} id={Marshal.ReadInt32(st, StateId)} " +
                $"picks={Marshal.ReadInt32(st, RoundsLeftOffset)}/{Marshal.ReadInt32(st, RoundsTotalOffset)} rerolls={pawn.GetCurrency(ECurrencyType.EItemDraftRerolls)} " +
                $"items={pawn.AbilityComponent.Abilities.Count(x => x.IsItem)} addon={(pawn.Controller is { } c && _trainHud.ContainsKey(c.Slot) ? 1 : 0)}");
        }
    }

    void BrawlBridge(string cmd, string[] a)
    {
        switch (cmd)
        {
            case "brawl":
                Log($"  BRAWL known={BrawlKnown} offs rules+{BrawlInRules} state+{BrawlState} start+{BrawlStateStart} next+{BrawlNextState} round+{BrawlRound} cd+{BrawlCountdown}");
                if (!BrawlKnown) break;
                Log($"  BRAWL now={Now:F1} state={Marshal.ReadInt32(Brawl, BrawlState)} start={ReadFloat(Brawl + BrawlStateStart):F1} next={ReadFloat(Brawl + BrawlNextState):F1} " +
                    $"round={Marshal.ReadInt32(Brawl, BrawlRound)} countdown={Marshal.ReadInt32(Brawl, BrawlCountdown)} " +
                    $"gs={GameRules.GameState} gsStart={GameRules.GameStateStartTime:F1} gsEnd={GameRules.GameStateEndTime:F1} gameStart={GameRules.GameStartTime:F1}");
                break;
            case "brawlnext":
                // brawlnext <seconds from now> [raw]: move the end of the current brawl phase
                {
                    float at = Now + float.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture);
                    if (a.Length > 1) Marshal.WriteInt32(Brawl, BrawlNextState, BitConverter.SingleToInt32Bits(at));
                    else new SchemaAccessor<float>("CStreetBrawlController"u8, "m_flNextStateTime"u8, 0).Set(Brawl, at);
                    Log($"  brawl next state time := {at:F1}");
                    break;
                }
            case "mods":
                foreach (var pawn in Players.GetAllPawns())
                    Log($"  mods slot={pawn.Controller?.Slot} pos={pawn.Position} [{string.Join(", ", pawn.ModifierProp?.Modifiers.Select(m => m.SubclassVData?.Name ?? "?") ?? [])}]");
                break;
            case "named":
                // named <designer name part>: entity names, for finding what the map switches at a state change
                foreach (var g in Entities.All.Where(e => e.DesignerName.Contains(a[0], StringComparison.OrdinalIgnoreCase)).GroupBy(e => $"{e.DesignerName} '{e.Name}'").OrderBy(g => g.Key))
                    Log($"  named {g.Key} x{g.Count()}");
                break;
            case "walls":
                foreach (var e in Entities.ByDesignerName("func_brush").Where(e => (e.Name ?? "").Contains(a.Length > 0 ? a[0] : "spawn_block")))
                    // 0x20 in the effects is "not drawn", which is how a switched-off brush reads
                    Log($"  wall {e.Name} #{e.EntityIndex} pos={e.Position} effects=0x{e.GetField<uint>("CBaseEntity"u8, "m_fEffects"u8):x}");
                break;
            case "nobot":
                // nobot <slot>: this bot waits to be picked for, as a player would (npick), in the next draft
                _testNoBot.Add(int.Parse(a[0]));
                _testKeepEmpty = true;
                Log($"  slot {a[0]} drafts by hand; an empty match is kept");
                break;
            case "npick":
                // npick <slot> <1-3>: take a card on the stock screen for that seat
                if (_native.TryGetValue(int.Parse(a[0]), out var seat) && Players.FromSlot(seat.Slot)?.GetHeroPawn() is { } picker) PickNative(seat, picker, int.Parse(a[1]) - 1);
                break;
            case "kill":
                // kill <slot>: for playing a brawl round to its end with bots
                Players.FromSlot(int.Parse(a[0]))?.GetHeroPawn()?.Hurt(100000f);
                break;
            case "closedraft":
                foreach (var pawn in Players.GetAllPawns()) CloseDraft(pawn);
                break;
            case "hurtents":
                // hurtents <designer name> <team>: destroy a team's objectives
                foreach (var e in Entities.ByDesignerName(a[0]).Where(e => e.TeamNum == int.Parse(a[1])).ToList())
                {
                    Log($"  hurting {e.DesignerName} '{e.Name}' team={e.TeamNum} hp={e.Health}");
                    e.Hurt(10000000f);
                }
                break;
            case "ds":
                LogDraftStates();
                break;
            case "input":
                // input <entity name> <input> [value]
                foreach (var e in Entities.ByName(a[0])) e.AcceptInput(a[1], value: a.Length > 2 ? a[2] : null);
                Log($"  input {a[1]} -> {a[0]}");
                break;
            case "endtime":
                GameRules.SetGameStateEndTime(Now + float.Parse(a[0], System.Globalization.CultureInfo.InvariantCulture));
                Log($"  game state end time := now+{a[0]}");
                break;
        }
    }
}
