using System.Runtime.InteropServices;
using System.Text;
using DeadworksManaged.Api;

namespace AbilityDraft;

// The drafted kit as the hero's OWN kit. The server keeps, per hero, a table "slot -> ability"
// (CitadelHeroData_t.m_mapBoundAbilities, read from heroes.vdata). Everything that works "by the hero's slot" goes
// through it: unlocking and upgrading, items that attach to one ability, the abilities a hero is given when it is
// created. The plugin used to put drafted abilities on top and catch each of those paths separately; writing the
// kit into this table makes the engine do all of it by itself, for every player, with nothing on the client.
// (Binger4/Deadlock-Ability-Draft gets the same effect by generating a heroes.vdata per draft.)
// The table is per hero, not per player: of two players on the same hero only the first one gets it, the other
// stays on the old path. The change lives in server memory only and is undone when the lobby comes back.
// Bridge commands for looking at the memory:
//   herodata <Hero>            where the hero's data is and what the table looks like
//   peek <hex address> [bytes] the same annotated dump for any address found that way
//   herobind <Hero> <slot 0-3> <ability>
public sealed partial class DraftPlugin
{
    [StructLayout(LayoutKind.Sequential)]
    struct MemoryInfo
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll")]
    static extern IntPtr VirtualQuery(IntPtr address, out MemoryInfo info, IntPtr length);

    /// <summary>True when the whole range is committed, readable memory: a wild pointer must not take the server down.</summary>
    static bool Readable(IntPtr address, int length)
    {
        if ((long)address < 0x10000) return false;
        if (VirtualQuery(address, out var info, (IntPtr)Marshal.SizeOf<MemoryInfo>()) == IntPtr.Zero) return false;
        const uint Commit = 0x1000, NoAccess = 0x01, Guard = 0x100;
        if (info.State != Commit || (info.Protect & (NoAccess | Guard)) != 0) return false;
        return (long)address + length <= (long)info.BaseAddress + (long)info.RegionSize;
    }

    static Dictionary<uint, string>? _tokenNames;

    /// <summary>Every ability, hero and slot name the plugin knows, by the engine's string token.</summary>
    static Dictionary<uint, string> TokenNames()
    {
        if (_tokenNames != null) return _tokenNames;
        var names = AbilityPool.All.Select(a => a.Name).Concat(AbilityPool.All.Select(a => a.Hero)).Distinct()
            .Concat(["ESlot_Signature_1", "ESlot_Signature_2", "ESlot_Signature_3", "ESlot_Signature_4", "ESlot_Weapon_Primary", "ESlot_Weapon_Melee", "ESlot_Ability_Mantle", "ESlot_Ability_Jump"]);
        _tokenNames = new();
        foreach (var n in names) _tokenNames[MurmurHash2.HashLowerCase(n, TokenSeed)] = n;
        return _tokenNames;
    }

    static string? StringAt(IntPtr p)
    {
        if (!Readable(p, 64)) return null;
        var sb = new StringBuilder();
        for (int i = 0; i < 64; i++)
        {
            byte b = Marshal.ReadByte(p, i);
            if (b == 0) break;
            if (b < 0x20 || b > 0x7e) return null;
            sb.Append((char)b);
        }
        return sb.Length >= 4 ? sb.ToString() : null;
    }

    void Peek(IntPtr address, int length)
    {
        if (!Readable(address, length)) { Log($"  0x{(long)address:x}: not readable for {length} bytes"); return; }
        var tokens = TokenNames();
        for (int off = 0; off < length; off += 8)
        {
            long q = Marshal.ReadInt64(address, off);
            uint lo = (uint)q, hi = (uint)(q >> 32);
            var notes = new List<string>();
            if (tokens.TryGetValue(lo, out var n1)) notes.Add($"lo={n1}");
            if (tokens.TryGetValue(hi, out var n2)) notes.Add($"hi={n2}");
            if (StringAt((IntPtr)q) is { } s) notes.Add($"-> \"{s}\"");
            else if (Readable((IntPtr)q, 8)) notes.Add("-> ptr");
            Log($"  +0x{off:x3}  {q:x16}  {string.Join("  ", notes)}");
        }
    }

    // What the dumps showed (build 6759): m_mapBoundAbilities is an ordered map; +0x10 points at its nodes and the
    // high half of +0x18 is their count. A node is 0x28 bytes: tree links (0x10), the slot in the low 16 bits of
    // +0x10 (signature abilities are slots 0-3), then the value: +0x18 a pointer to the ability's name, +0x20 its token.
    const int MapNodes = 0x10, MapCount = 0x1c, NodeSize = 0x28, NodeSlot = 0x10, NodeName = 0x18, NodeToken = 0x20;
    static readonly int BoundAbilitiesOffset = (int)(long)new SchemaAccessor<int>("CitadelHeroData_t"u8, "m_mapBoundAbilities"u8, 0).GetAddress(IntPtr.Zero);

    /// <summary>The node that binds a signature slot (0-3) of this hero, or zero when the layout is not as expected.</summary>
    static IntPtr BoundAbilityNode(Heroes hero, int slot)
    {
        if (hero == 0 || BoundAbilitiesOffset <= 0) return IntPtr.Zero;
        CitadelHeroData? data;
        try { data = hero.GetHeroData(); } catch (KeyNotFoundException) { return IntPtr.Zero; }     // not a hero the engine knows
        if (data == null || !data.IsValid) return IntPtr.Zero;
        var map = data.Pointer + BoundAbilitiesOffset;
        if (!Readable(map, 0x20)) return IntPtr.Zero;
        var nodes = Marshal.ReadIntPtr(map, MapNodes);
        int count = Marshal.ReadInt32(map, MapCount);
        if (count is < 4 or > 64 || !Readable(nodes, count * NodeSize)) return IntPtr.Zero;
        for (int i = 0; i < count; i++)
        {
            var node = nodes + i * NodeSize;
            if ((ushort)Marshal.ReadInt16(node, NodeSlot) != slot) continue;
            // Only a node that reads back as "name + that name's token" is trusted.
            var name = StringAt(Marshal.ReadIntPtr(node, NodeName));
            if (name != null && MurmurHash2.HashLowerCase(name, TokenSeed) == (uint)Marshal.ReadInt32(node, NodeToken)) return node;
        }
        return IntPtr.Zero;
    }

    readonly Dictionary<Heroes, int> _heroOwner = new();                    // hero -> the player slot whose kit its table holds
    readonly Dictionary<(Heroes Hero, int Slot), string> _heroOriginal = new();       // the hero's own ability, by name

    /// <summary>True when the engine itself treats this player's drafted kit as the hero's own.</summary>
    bool HasNativeKit(CCitadelPlayerController c) =>
        c.GetHeroPawn() is { } pawn && _heroOwner.TryGetValue(pawn.HeroID, out int owner) && owner == c.Slot;

    /// <summary>Writes one drafted ability into the hero's own table, if this player may have the hero's table.</summary>
    bool BindHeroSlot(Heroes hero, int playerSlot, int slot, string ability)
    {
        if (hero == 0 || _heroOwner.TryGetValue(hero, out int owner) && owner != playerSlot) return false;
        var node = BoundAbilityNode(hero, slot);
        if (node == IntPtr.Zero)
        {
            Log($"hero table: no trusted entry for {hero} slot {slot} - the kit stays on the plugin's own path");
            return false;
        }
        _heroOwner[hero] = playerSlot;
        _heroOriginal.TryAdd((hero, slot), StringAt(Marshal.ReadIntPtr(node, NodeName))!);
        uint token = MurmurHash2.HashLowerCase(ability, TokenSeed);
        if ((uint)Marshal.ReadInt32(node, NodeToken) == token) return true;
        // The engine keeps pointing at this string, so it is never freed (a few bytes per pick).
        Marshal.WriteIntPtr(node, NodeName, Marshal.StringToHGlobalAnsi(ability));
        Marshal.WriteInt32(node, NodeToken, (int)token);
        return true;
    }

    void BindHeroKit(Heroes hero, int playerSlot, string[] kit)
    {
        for (int slot = 0; slot < kit.Length && slot < Slots; slot++) BindHeroSlot(hero, playerSlot, slot, kit[slot]);
    }

    /// <summary>Gives every hero its own abilities back.</summary>
    void RestoreHeroTables()
    {
        foreach (var ((hero, slot), original) in _heroOriginal)
        {
            var node = BoundAbilityNode(hero, slot);
            if (node == IntPtr.Zero) continue;
            // Written as a fresh string: the engine's own one may be gone if it reloaded the hero data meanwhile.
            Marshal.WriteIntPtr(node, NodeName, Marshal.StringToHGlobalAnsi(original));
            Marshal.WriteInt32(node, NodeToken, (int)MurmurHash2.HashLowerCase(original, TokenSeed));
        }
        if (_heroOriginal.Count > 0) Log($"hero tables restored ({_heroOriginal.Count} entries)");
        _heroOriginal.Clear();
        _heroOwner.Clear();
    }

    void BindHeroAbility(string[] a)
    {
        if (a.Length < 3 || !Enum.TryParse<Heroes>(a[0], true, out var hero) || !int.TryParse(a[1], out int slot) || slot is < 0 or > 3) { Log("herobind <Hero> <slot 0-3> <ability>"); return; }
        Log($"herobind {hero} slot {slot} -> {a[2]}: {BindHeroSlot(hero, -1, slot, a[2])}");
    }

    void ProbeHeroData(string[] a)
    {
        if (a.Length == 0 || !Enum.TryParse<Heroes>(a[0], true, out var hero)) { Log($"herodata: unknown hero '{string.Join(" ", a)}'"); return; }
        var data = hero.GetHeroData();
        if (data == null || !data.IsValid) { Log($"herodata: no data for {hero}"); return; }
        long field = (long)new SchemaAccessor<int>("CitadelHeroData_t"u8, "m_mapBoundAbilities"u8, 0).GetAddress(IntPtr.Zero);
        Log($"herodata {hero}: data=0x{(long)data.Pointer:x} id={data.HeroID} m_mapBoundAbilities at +0x{field:x}");
        if (field <= 0) { Log("  the schema does not know m_mapBoundAbilities"); return; }
        Peek(data.Pointer + (int)field, a.Length > 1 && int.TryParse(a[1], out int len) ? len : 96);
    }
}
