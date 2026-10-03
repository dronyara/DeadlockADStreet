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

    int _patchSlot = -1;
    uint[] _patchTokens = [];

    /// <summary>Research: rewrites the item ids in a pawn's stock Street Brawl draft options, every frame.</summary>
    void PatchNativeDraft()
    {
        if (_patchSlot < 0 || Players.FromSlot(_patchSlot)?.GetHeroPawn() is not { } pawn) return;
        var state = new SchemaAccessor<int>("CCitadelPlayerPawn"u8, "m_ItemDraftRoundState"u8, 0).GetAddress(pawn.Handle);
        int count = System.Runtime.InteropServices.Marshal.ReadInt32(state, 8);
        var data = System.Runtime.InteropServices.Marshal.ReadIntPtr(state, 16);
        if (data == IntPtr.Zero) return;
        for (int i = 0; i < Math.Min(count, _patchTokens.Length); i++)
        {
            int at = i * 248 + 96;
            if ((uint)System.Runtime.InteropServices.Marshal.ReadInt32(data, at) == _patchTokens[i]) continue;
            System.Runtime.InteropServices.Marshal.WriteInt32(data, at, (int)_patchTokens[i]);
            Log($"autopatch wrote option {i} tick={GlobalVars.TickCount}");
        }
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
            case "autopatch":
                // autopatch <slot> <a> <b> <c>: keep that player's native draft options pointed at these abilities.
                _patchSlot = int.Parse(a[0]);
                _patchTokens = a.Skip(1).Select(n => MurmurHash2.HashLowerCase(n, 0x31415926)).ToArray();
                Log($"autopatch slot={_patchSlot} tokens={string.Join(",", _patchTokens.Select(t => t.ToString("x8")))}");
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
