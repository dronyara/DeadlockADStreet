using DeadworksManaged.Api;

namespace AbilityDraft;

// Log file and a file-driven test bridge: lines written to %TEMP%\abilitydraft.cmd are run on the server.
// It lets the draft be exercised end to end with bots, without a game client.
public sealed partial class DraftPlugin
{
    static readonly string LogPath = Path.Combine(Path.GetTempPath(), "abilitydraft.log");
    static readonly string BridgePath = Path.Combine(Path.GetTempPath(), "abilitydraft.cmd");

    static void Log(string s)
    {
        Console.WriteLine("[AD] " + s);
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {s}{Environment.NewLine}"); } catch { }
    }

    // ---- research: the stock Street Brawl draft screen showing abilities -----------------------------------------
    // The server rewrites the item ids of the three native draft options to ability ids as soon as they are rolled.
    // Clicking such a card crashes the client, so picks come from ability keys 1-3 and are applied here;
    // itemdraftskip then moves the stock screen on to its next roll, which is patched again.
    int _nativeSlot = -1, _nativeRound;
    readonly string?[] _nativeKit = new string?[Slots];
    readonly AbilityDef?[] _nativeOffer = new AbilityDef?[Offers];
    readonly uint[] _nativeWritten = new uint[Offers];
    bool _nativePrev1, _nativePrev2, _nativePrev3;
    bool _nativeMarkDrafted;      // experiment: does a "drafted" card stop being clickable?

    static uint Token(string name) => MurmurHash2.HashLowerCase(name, 0x31415926);

    void PatchNativeDraft()
    {
        if (_nativeSlot < 0 || _nativeRound >= Slots || Players.FromSlot(_nativeSlot)?.GetHeroPawn() is not { } pawn) return;
        var state = new SchemaAccessor<int>("CCitadelPlayerPawn"u8, "m_ItemDraftRoundState"u8, 0).GetAddress(pawn.Handle);
        int count = System.Runtime.InteropServices.Marshal.ReadInt32(state, 8);
        var data = System.Runtime.InteropServices.Marshal.ReadIntPtr(state, 16);
        if (data == IntPtr.Zero || count < Offers) return;
        bool fresh = false;
        for (int i = 0; i < Offers; i++)
            fresh |= (uint)System.Runtime.InteropServices.Marshal.ReadInt32(data, i * 248 + 96) != _nativeWritten[i] || _nativeWritten[i] == 0;
        if (!fresh) return;

        bool ult = _nativeRound == Slots - 1;
        var pool = AbilityPool.All.Where(x => x.Ult == ult && !_nativeKit.Contains(x.Name)).OrderBy(_ => Random.Shared.Next()).Take(Offers).ToArray();
        for (int i = 0; i < Offers; i++)
        {
            _nativeOffer[i] = pool[i];
            _nativeWritten[i] = Token(pool[i].Name);
            System.Runtime.InteropServices.Marshal.WriteInt32(data, i * 248 + 96, (int)_nativeWritten[i]);
            System.Runtime.InteropServices.Marshal.WriteByte(data, i * 248 + 241, 0);      // m_bRare
            if (_nativeMarkDrafted) System.Runtime.InteropServices.Marshal.WriteByte(data, i * 248 + 240, 1);   // m_bHasBeenDrafted
        }
        Log($"native round {_nativeRound + 1}: offered {string.Join(", ", pool.Select(x => x.Name))} tick={GlobalVars.TickCount}");
    }

    bool NativeInput(AbilityAttemptEvent args)
    {
        if (_nativeSlot != args.PlayerSlot || _nativeRound >= Slots) return false;
        args.BlockAll();
        bool k1 = args.IsHeld(InputButton.Ability1), k2 = args.IsHeld(InputButton.Ability2), k3 = args.IsHeld(InputButton.Ability3);
        int pressed = k1 && !_nativePrev1 ? 0 : k2 && !_nativePrev2 ? 1 : k3 && !_nativePrev3 ? 2 : -1;
        (_nativePrev1, _nativePrev2, _nativePrev3) = (k1, k2, k3);
        if (pressed < 0 || _nativeOffer[pressed] is not { } pick) return true;

        var pawn = args.Controller?.GetHeroPawn();
        if (pawn == null) return true;
        if (pawn.GetAbilityBySlot((EAbilitySlot)_nativeRound) is CCitadelBaseAbility old) pawn.RemoveAbility(old);
        var added = pawn.AddAbility(pick.Name, (ushort)_nativeRound);
        _nativeKit[_nativeRound] = pick.Name;
        Log($"native round {_nativeRound + 1}: key {pressed + 1} -> {pick.Name} (added={added != null}); kit now [{string.Join(", ", KitOf(pawn))}]");
        _nativeRound++;
        Array.Clear(_nativeOffer);
        // Dealing again is the only thing that makes the client redraw the cards, so every pick ends with a
        // server-side reroll: the next three abilities, or - after the fourth pick - the player's real item options.
        pawn.SetCurrency(ECurrencyType.EItemDraftRerolls, pawn.GetCurrency(ECurrencyType.EItemDraftRerolls) + 1);
        Native.Reroll(pawn);
        if (_nativeRound >= Slots) Log("native draft finished, stock item draft resumes");
        return true;
    }

    void PollBridge()
    {
        string[] lines;
        try
        {
            if (!File.Exists(BridgePath)) return;
            lines = File.ReadAllLines(BridgePath);
            File.Delete(BridgePath);
        }
        catch { return; }

        foreach (var line in lines.Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            var p = line.Split(' ', 2);
            var arg = p.Length > 1 ? p[1] : "";
            try { RunBridge(p[0], arg); }
            catch (Exception ex) { Log($"bridge '{line}' failed: {ex.Message}"); }
        }
    }

    void RunBridge(string cmd, string arg)
    {
        var a = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (cmd)
        {
            case "sv":
                Log($"> {arg}");
                Server.ExecuteCommand(arg);
                break;
            case "cvars":
                foreach (var v in Server.EnumerateConVars().Where(v => v.Name.Contains(arg, StringComparison.OrdinalIgnoreCase)))
                    Log($"  cvar {v.Name} = '{v.Value}' (def '{v.DefaultValue}', {v.Type}, flags 0x{v.Flags:x}) {v.Description}");
                foreach (var v in Server.EnumerateConCommands().Where(v => v.Name.Contains(arg, StringComparison.OrdinalIgnoreCase)))
                    Log($"  cmd  {v.Name} (flags 0x{v.Flags:x}) {v.Description}");
                break;
            case "state":
                Log($"STATE phase={_phase} rules={_rules} host={_hostSlot} gamestate={GameRules.GameState} mode={GameRules.GameMode} match={GameRules.MatchMode} " +
                    $"clock={GameRules.GameClock:F0} map={Server.MapName} players={string.Join(",", Players.GetAll().Select(c => $"{c.Slot}:{c.PlayerName}:t{c.TeamNum}:{c.GetHeroPawn()?.HeroID.ToString() ?? "nohero"}"))}");
                break;
            case "kits":
                foreach (var pawn in Players.GetAllPawns())
                    Log($"  kit slot={pawn.Controller?.Slot} hero={pawn.HeroID} lvl={pawn.Level} [{string.Join(", ", KitOf(pawn))}]");
                break;
            case "bot":
                Log($"fake client '{arg}' -> slot {Server.CreateFakeClient(arg.Length > 0 ? arg : "DraftBot")}");
                break;
            case "hero":
                {
                    // hero <slot> <team 2|3> <Heroes name>: seats a fake client so it can take part in the draft.
                    var c = Players.FromSlot(int.Parse(a[0])) ?? throw new Exception("no such slot");
                    c.ChangeTeam(int.Parse(a[1]));
                    c.SelectHero(Enum.Parse<Heroes>(a[2], true));
                    Log($"slot {a[0]} -> team {a[1]} hero {a[2]}");
                    break;
                }
            case "go":
                _phase = Phase.Match;
                GameRules.ChangeGameState(EGameState.GameInProgress);
                break;
            case "ents":
                foreach (var g in Entities.All.Where(e => e.DesignerName.Contains(arg, StringComparison.OrdinalIgnoreCase)).GroupBy(e => e.DesignerName))
                    Log($"  ent {g.Key} x{g.Count()}");
                break;
            case "draft":
                StartDraft();
                break;
            case "pick":
                Log($"bridge pick -> {Pick(_seats[int.Parse(a[0])], int.Parse(a[1]) - 1)}");
                break;
            case "reroll":
                Log($"bridge reroll -> {Reroll(_seats[int.Parse(a[0])], int.Parse(a[1]) - 1)}");
                break;
            case "vote":
                CastVote(int.Parse(a[0]), a[1].StartsWith('b') ? Rules.StreetBrawl : Rules.Standard);
                break;
            case "forcevote":
                // Bots do not vote; this stands in for a human majority in an all-bot test.
                _forcedRules = a[0].StartsWith('b') ? Rules.StreetBrawl : Rules.Standard;
                Log($"forced rules = {_forcedRules}");
                break;
            case "angles":
                foreach (var pawn in Players.GetAllPawns())
                    Log($"  slot={pawn.Controller?.Slot} view={pawn.ViewAngles} eye={pawn.EyeAngles} cam={pawn.CameraAngles} eyepos={pawn.EyePosition}");
                break;
            case "draftdump":
                {
                    // Raw view of the pawn's native Street Brawl item-draft state (research for reusing the stock draft screen).
                    var pawn = Players.FromSlot(int.Parse(a[0]))?.GetHeroPawn() ?? throw new Exception("no pawn");
                    var state = new SchemaAccessor<int>("CCitadelPlayerPawn"u8, "m_ItemDraftRoundState"u8, 0).GetAddress(pawn.Handle);
                    long Off(ReadOnlySpan<byte> cls, ReadOnlySpan<byte> f) => new SchemaAccessor<int>(cls, f, 0).GetAddress(IntPtr.Zero).ToInt64();
                    Log($"  state@pawn+0x{state.ToInt64() - pawn.Handle.ToInt64():x} offs: vecOptions={Off("ItemDraftRoundState_t"u8, "m_vecOptions"u8)} id={Off("ItemDraftRoundState_t"u8, "m_nID"u8)} " +
                        $"total={Off("ItemDraftRoundState_t"u8, "m_nDraftsTotal"u8)} remaining={Off("ItemDraftRoundState_t"u8, "m_nDraftsRemaining"u8)} | option: item={Off("ItemDraftOption_t"u8, "m_Item"u8)} " +
                        $"bonus1={Off("ItemDraftOption_t"u8, "m_BonusItem1"u8)} bonus2={Off("ItemDraftOption_t"u8, "m_BonusItem2"u8)} rare={Off("ItemDraftOption_t"u8, "m_bRare"u8)} drafted={Off("ItemDraftOption_t"u8, "m_bHasBeenDrafted"u8)} " +
                        $"| item: id={Off("ItemDraftItem_t"u8, "m_unItemID"u8)} lvl={Off("ItemDraftItem_t"u8, "m_nAbilityLevel"u8)} bits={Off("ItemDraftItem_t"u8, "m_nUpgradeBits"u8)}");
                    var raw = new byte[0xA0];
                    System.Runtime.InteropServices.Marshal.Copy(state, raw, 0, raw.Length);
                    Log("  raw " + Convert.ToHexString(raw));
                    foreach (var n in a.Skip(1)) Log($"  token {n} = 0x{MurmurHash2.HashLowerCase(n, 0x31415926):x8}");
                    int count = System.Runtime.InteropServices.Marshal.ReadInt32(state, 8);
                    var data = System.Runtime.InteropServices.Marshal.ReadIntPtr(state, 16);
                    var el = new byte[0x300];
                    System.Runtime.InteropServices.Marshal.Copy(data, el, 0, el.Length);
                    Log($"  options count={count} data " + Convert.ToHexString(el));
                    break;
                }
            case "draftset":
                {
                    // draftset <slot> <option> <stride> <ability>: overwrite a native draft option's item token.
                    var pawn = Players.FromSlot(int.Parse(a[0]))?.GetHeroPawn() ?? throw new Exception("no pawn");
                    var state = new SchemaAccessor<int>("CCitadelPlayerPawn"u8, "m_ItemDraftRoundState"u8, 0).GetAddress(pawn.Handle);
                    var data = System.Runtime.InteropServices.Marshal.ReadIntPtr(state, 16);
                    int at = int.Parse(a[1]) * int.Parse(a[2]) + 96;
                    uint old = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(data, at), tok = MurmurHash2.HashLowerCase(a[3], 0x31415926);
                    System.Runtime.InteropServices.Marshal.WriteInt32(data, at, (int)tok);
                    Log($"  option {a[1]}: 0x{old:x8} -> 0x{tok:x8} ({a[3]})");
                    break;
                }
            case "abil":
                foreach (var pawn in Players.GetAllPawns())
                    Log($"  slot={pawn.Controller?.Slot} all=[{string.Join(", ", pawn.AbilityComponent.Abilities.Where(x => x.IsItem || x.IsSignature).Select(x => $"{x.AbilityName}@{x.AbilitySlot}"))}]");
                break;
            case "cc":
                Server.ClientCommand(int.Parse(a[0]), string.Join(' ', a.Skip(1)));
                Log($"sent to client {a[0]}: {string.Join(' ', a.Skip(1))}");
                break;
            case "rerolls":
                {
                    var pawn = Players.FromSlot(int.Parse(a[0]))?.GetHeroPawn() ?? throw new Exception("no pawn");
                    Log($"rerolls {pawn.GetCurrency(ECurrencyType.EItemDraftRerolls)} -> {a[1]}");
                    pawn.SetCurrency(ECurrencyType.EItemDraftRerolls, int.Parse(a[1]));
                    break;
                }
            case "nskip":
                Native.Skip(Players.FromSlot(int.Parse(a[0]))?.GetHeroPawn() ?? throw new Exception("no pawn"));
                break;
            case "nreroll":
                Native.Reroll(Players.FromSlot(int.Parse(a[0]))?.GetHeroPawn() ?? throw new Exception("no pawn"));
                break;
            case "native":
                if (!Native.Ready) throw new Exception("native functions not resolved");
                _nativeSlot = int.Parse(a[0]);
                _nativeRound = 0;
                _nativeMarkDrafted = a.Length > 1 && a[1] == "drafted";
                Array.Clear(_nativeKit); Array.Clear(_nativeOffer); Array.Clear(_nativeWritten);
                Log($"native draft armed for slot {_nativeSlot}");
                break;
            case "unlock":
                foreach (var pawn in Players.GetAllPawns())
                    foreach (var ab in pawn.AbilityComponent.Abilities.Where(ab => ab.IsSignature))
                        ab.UpgradeBits = 1;
                Log("all signature abilities unlocked");
                break;
            case "cast":
                {
                    var pawn = Players.FromSlot(int.Parse(a[0]))?.GetHeroPawn();
                    var slot = (EAbilitySlot)(int.Parse(a[1]) - 1);
                    Log($"cast slot={a[0]} ability={(pawn?.GetAbilityBySlot(slot) as CCitadelBaseAbility)?.AbilityName} -> {pawn?.ExecuteAbilityBySlot(slot)}");
                    break;
                }
            default:
                Log($"unknown bridge command '{cmd}'");
                break;
        }
    }
}
