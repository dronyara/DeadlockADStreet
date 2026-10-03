using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DeadworksManaged.Api;

namespace AbilityDraft;

/// <summary>
/// Two server.dll functions the stock Street Brawl draft uses, called directly so the server can drive the native
/// draft screen: Advance uses up a draft pick (and closes the screen after the last one), Reroll deals three new options.
/// They are located by byte signature; tools/find_sigs.py regenerates the signatures after a game patch.
/// </summary>
static unsafe class Native
{
    static delegate* unmanaged<IntPtr, void> _advance, _reroll;

    public static bool Ready => _advance != null && _reroll != null;

    public static string Load(string signatureFile)
    {
        _advance = _reroll = null;
        if (!File.Exists(signatureFile)) return $"{signatureFile} is missing - run tools/find_sigs.py";
        var sigs = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(signatureFile)) ?? new();
        ProcessModule? server = null;
        foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            if (m.ModuleName.Equals("server.dll", StringComparison.OrdinalIgnoreCase)) server = m;
        if (server == null) return "server.dll is not loaded";

        var image = new ReadOnlySpan<byte>((void*)server.BaseAddress, server.ModuleMemorySize);
        var advance = Find(image, sigs.GetValueOrDefault("ItemDraftAdvance"));
        var reroll = Find(image, sigs.GetValueOrDefault("ItemDraftReroll"));
        if (advance < 0 || reroll < 0)
            return $"signature not found (advance={advance >= 0}, reroll={reroll >= 0}) - the game was patched, run tools/find_sigs.py";
        _advance = (delegate* unmanaged<IntPtr, void>)(server.BaseAddress + advance);
        _reroll = (delegate* unmanaged<IntPtr, void>)(server.BaseAddress + reroll);
        return $"native draft functions resolved: advance=server.dll+0x{advance:x} reroll=server.dll+0x{reroll:x}";
    }

    /// <summary>Offset of the only match of a "48 8B ? ?" style pattern, or -1 when it is missing or ambiguous.</summary>
    static int Find(ReadOnlySpan<byte> image, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return -1;
        var parts = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bytes = parts.Select(p => p == "?" ? (short)-1 : Convert.ToInt16(p, 16)).ToArray();
        byte first = (byte)bytes[0];
        int found = -1;
        for (int i = 0; i <= image.Length - bytes.Length; i++)
        {
            if (image[i] != first) continue;
            int j = 1;
            while (j < bytes.Length && (bytes[j] < 0 || image[i + j] == bytes[j])) j++;
            if (j < bytes.Length) continue;
            if (found >= 0) return -1;
            found = i;
        }
        return found;
    }

    /// <summary>Uses up one of the hero's draft picks: deals the next one, or ends the draft and closes the client's screen.</summary>
    public static void Advance(CCitadelPlayerPawn pawn) => _advance(pawn.Handle);
    public static void Reroll(CCitadelPlayerPawn pawn) => _reroll(pawn.Handle);
}
