using System.Text.RegularExpressions;
using DeadworksManaged.Api;

namespace AbilityDraft;

// Joining through Steam instead of an IP address: "connect [A:1:...]" reaches the server over Steam's relays,
// so the host needs no port forwarding and no public address.
public sealed partial class DraftPlugin
{
    // The engine only opens its Steam listen socket when this is on while the server socket is being created.
    // It cannot be set from the command line (the engine puts it back to false), so it is switched on at runtime
    // and the map is started once more with the "map" command, which rebuilds the socket.
    const string SteamListenCvar = "net_p2p_listen_dedicated";
    const string NoSteamConnectParm = "-ad_nosteamconnect";

    static readonly Regex ServerIdLine = new(@"ServerSteamID=(\[A:\d+:\d+:\d+\])", RegexOptions.Compiled);
    static readonly Regex IdentityLine = new(@"assigned identity steamid:(\d+)", RegexOptions.Compiled);

    string? _steamConnect;          // "[A:1:x:y]" as the engine last announced it; changes on every server start

    void EnableSteamConnect()
    {
        if (Server.HasCommandLineParm(NoSteamConnectParm)) return;
        var listen = ConVar.Find(SteamListenCvar);
        if (listen == null || listen.GetBool()) return;
        if (ConVar.Find("sv_lan")?.GetBool() == true)
        {
            Log("Steam connect is off: sv_lan 1 keeps the server on the local network");
            return;
        }
        if (Players.GetAll().Any(p => !p.IsBot)) return;      // never restart a map somebody is playing on

        var map = string.IsNullOrEmpty(Server.MapName) ? "dl_midtown" : Server.MapName;
        Log($"enabling Steam connect: {SteamListenCvar} 1, restarting {map} once to rebuild the server socket");
        Server.ExecuteCommand($"{SteamListenCvar} 1");
        Timer.Once(1.Seconds(), () => Server.ExecuteCommand($"map {map}"));
    }

    void OnEngineLog(string message)
    {
        if (!message.Contains("teamid", StringComparison.Ordinal)) return;
        string? id = null;
        if (ServerIdLine.Match(message) is { Success: true } a) id = a.Groups[1].Value;
        else if (IdentityLine.Match(message) is { Success: true } b && ulong.TryParse(b.Groups[1].Value, out var raw))
            id = $"[A:1:{raw & 0xFFFFFFFF}:{(raw >> 32) & 0xFFFFF}]";     // account id and instance of an anonymous game server
        if (id == null || id == _steamConnect) return;
        _steamConnect = id;
        Log($"STEAM CONNECT: connect {id}");
    }

    bool SteamConnectReady => _steamConnect != null && ConVar.Find(SteamListenCvar)?.GetBool() == true;

    [Command("id", "connectid", Description = "Show the command friends use to join through Steam, without port forwarding")]
    public void CmdId(CCitadelPlayerController? caller = null)
    {
        var text = SteamConnectReady
            ? $"[Draft] connect {_steamConnect}"
            : "[Draft] Вход через Steam недоступен, используйте connect <ip>:27067 | Steam connect is not available, use connect <ip>:27067";
        if (caller != null) Chat.PrintToChat(caller, text);
        Log(text);
    }
}
