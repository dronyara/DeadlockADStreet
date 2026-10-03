using DeadworksManaged.Api;
using DeadworksManaged.Api.UI;

namespace AbilityDraft;

// What the player sees. Players who joined through the Deadworks launcher get a clickable panel;
// everyone gets the same offer in chat, so the draft is fully playable with keys 1-3 and R alone.
public sealed partial class DraftPlugin
{
    const string PanelId = "abilitydraft";
    const float BillboardDistance = 260f;    // in front of the hero's eyes, clear of the third-person camera
    const float BillboardRaise = 70f;        // lifted so the hero does not stand in the middle of the text
    const float BillboardScale = 0.2f;

    static readonly (string, string)[] CardStyle =
    [
        ("width", "290px"), ("margin", "0px 12px"), ("padding", "18px"), ("flow-children", "down"),
        ("background-color", "#17140ff2"), ("border", "2px solid #b08d57"), ("border-radius", "10px"),
    ];

    static readonly (string, string)[] ButtonStyle =
    [
        ("width", "100%"), ("height", "46px"), ("margin-top", "10px"), ("border-radius", "6px"),
        ("background-color", "#3d6b3a"), ("border", "1px solid #7fbf6f"),
    ];

    static readonly (string, string)[] ButtonText =
    [
        ("font-size", "22px"), ("color", "#ffffff"), ("horizontal-align", "center"), ("vertical-align", "center"),
    ];

    bool _panelWired;

    // The panel channel is only touched once a launcher client is actually present.
    bool HasUi(Seat s)
    {
        if (s.Bot || !UI.HasClientBootstrap(s.Slot)) return false;
        if (!_panelWired)
        {
            _panelWired = true;
            var panel = UI.Panel(PanelId);
            panel.On("pick", e => OnUiOffer(e, reroll: false));
            panel.On("reroll", e => OnUiOffer(e, reroll: true));
            panel.On("vote", e => { if (e.Caller != null) CastVote(e.Caller.Slot, e.ArgAt(0) == "brawl" ? Rules.StreetBrawl : Rules.Standard); });
            UI.ClientResync += OnClientResync;
        }
        return true;
    }

    void ShowOffer(Seat s)
    {
        if (s.Bot) return;
        var c = Players.FromSlot(s.Slot);
        if (c == null) return;

        string Line(int i) => s.Offer[i] is { } a ? $"[{i + 1}]  {a.Title(s.Ru)}  ·  {a.HeroTitle(s.Ru)}   (x{s.Rerolls[i]})" : $"[{i + 1}]  —";
        bool ult = s.Round == Slots - 1;
        var title = s.Ru ? $"СПОСОБНОСТЬ {s.Round + 1}/{Slots}{(ult ? " · УЛЬТА" : "")}" : $"ABILITY {s.Round + 1}/{Slots}{(ult ? " · ULTIMATE" : "")}";
        var keys = s.Ru ? "1-3 — взять    R+1-3 — заменить" : "1-3 pick    R+1-3 reroll";
        Chat.PrintToChat(c, $"{title}: {string.Join("  |  ", Enumerable.Range(0, Offers).Select(Line))}  ·  {keys}");

        if (!HasUi(s))
        {
            // Stock clients have no panel, so the same menu floats over their own hero as world text.
            var lines = string.Join("\n", Enumerable.Range(0, Offers).Select(Line));
            SetBillboard(s, $"{title}\n \n{lines}\n \n{keys}");
            return;
        }
        var to = RecipientFilter.Single(s.Slot);
        UI.Panel(PanelId).BuildLayout(to, OfferLayout(s));
        if (!s.PanelUp) UI.Panel(PanelId).RequestCursor(to);
        s.PanelUp = true;
    }

    UINode OfferLayout(Seat s)
    {
        bool ru = s.Ru;
        var row = UI.Horizontal("ad_row").WithStyles(("horizontal-align", "center"), ("margin-top", "24px"));
        for (int i = 0; i < Offers; i++)
        {
            var a = s.Offer[i];
            var card = UI.Vertical($"ad_card{i}").WithStyles(CardStyle);
            if (a != null)
                card.Add(
                    UI.Image($"ad_icon{i}", a.Icon).WithStyles(("width", "120px"), ("height", "120px"), ("horizontal-align", "center")),
                    UI.Label($"ad_name{i}", a.Title(ru)).WithStyles(("font-size", "26px"), ("color", "#f5e6c8"), ("horizontal-align", "center"), ("margin-top", "12px"), ("text-align", "center")),
                    UI.Label($"ad_hero{i}", a.HeroTitle(ru)).WithStyles(("font-size", "18px"), ("color", "#9c9484"), ("horizontal-align", "center")),
                    UI.Button($"ad_pick{i}", ru ? $"Взять [{i + 1}]" : $"Pick [{i + 1}]", "pick", i.ToString())
                        .WithStyles(ButtonStyle).WithHoverStyle("background-color", "#4f8a4a").WithTextStyles(ButtonText));
            var rr = UI.Button($"ad_rr{i}", (ru ? "Заменить" : "Reroll") + $" ({s.Rerolls[i]})", "reroll", i.ToString())
                .WithStyles(ButtonStyle).WithStyles(("background-color", s.Rerolls[i] > 0 ? "#5a4a2c" : "#2b2b2b"), ("border", "1px solid #b08d57"))
                .WithTextStyles(ButtonText);
            card.Add(rr);
            row.Add(card);
        }

        var kit = string.Join("  ·  ", Enumerable.Range(0, Slots).Select(i =>
            s.Kit[i] is { } k ? AbilityPool.Find(k)?.Title(ru) ?? k : i == s.Round ? "?" : "—"));
        return Frame(
            UI.Label("ad_title", ru
                ? $"ВЫБОР СПОСОБНОСТИ {s.Round + 1}/{Slots}{(s.Round == Slots - 1 ? " — УЛЬТА" : "")}"
                : $"ABILITY DRAFT {s.Round + 1}/{Slots}{(s.Round == Slots - 1 ? " — ULTIMATE" : "")}").WithStyles(TitleStyle),
            UI.Label("ad_timer", "").WithStyles(("font-size", "30px"), ("color", "#e8b04a"), ("horizontal-align", "center")),
            row,
            UI.Label("ad_kit", kit).WithStyles(("font-size", "20px"), ("color", "#c9c2b3"), ("horizontal-align", "center"), ("margin-top", "22px")));
    }

    static readonly (string, string)[] TitleStyle =
    [
        ("font-size", "40px"), ("color", "#f5e6c8"), ("horizontal-align", "center"), ("letter-spacing", "3px"),
    ];

    static UINode Frame(params UINode[] children)
    {
        var box = UI.Vertical("ad_box").WithStyles(("horizontal-align", "center"), ("vertical-align", "center"));
        box.Add(children);
        return UI.Container("ad_root").WithStyles(("width", "100%"), ("height", "100%"), ("background-color", "#000000b8")).Add(box);
    }

    void ShowWaiting(Seat s)
    {
        if (!HasUi(s))
        {
            SetBillboard(s, (s.Ru ? "НАБОР ГОТОВ\n \n" : "KIT READY\n \n") + string.Join("\n", s.Kit.Select(k => AbilityPool.Find(k!)?.Title(s.Ru) ?? k))
                + (s.Ru ? "\n \nЖдём остальных…" : "\n \nWaiting for the others…"));
            return;
        }
        var to = RecipientFilter.Single(s.Slot);
        var kit = string.Join("  ·  ", s.Kit.Select(k => AbilityPool.Find(k!)?.Title(s.Ru) ?? k));
        UI.Panel(PanelId).BuildLayout(to, Frame(
            UI.Label("ad_title", s.Ru ? "НАБОР ГОТОВ" : "KIT READY").WithStyles(TitleStyle),
            UI.Label("ad_kit", kit).WithStyles(("font-size", "24px"), ("color", "#c9c2b3"), ("horizontal-align", "center"), ("margin-top", "16px")),
            UI.Label("ad_wait", s.Ru ? "Ждём остальных игроков…" : "Waiting for the other players…")
                .WithStyles(("font-size", "20px"), ("color", "#9c9484"), ("horizontal-align", "center"), ("margin-top", "16px"))));
    }

    void ShowVote(Seat s)
    {
        var c = Players.FromSlot(s.Slot);
        if (c == null) return;
        if (s.Vote == Rules.None)
            Chat.PrintToChat(c, s.Ru
                ? "[Draft] Правила матча: клавиша 1 — Standard, клавиша 2 — Street Brawl (или /vote standard, /vote brawl)"
                : "[Draft] Match rules: key 1 = Standard, key 2 = Street Brawl (or /vote standard, /vote brawl)");
        if (!HasUi(s))
        {
            string Mark(Rules r) => s.Vote == r ? "  ✔" : "";
            SetBillboard(s, (s.Ru ? "ПРАВИЛА МАТЧА" : "MATCH RULES") + $"\n \n[1]  Standard{Mark(Rules.Standard)}\n[2]  Street Brawl{Mark(Rules.StreetBrawl)}");
            return;
        }

        UIButton Choice(string id, string text, string arg, Rules r) =>
            UI.Button(id, text, "vote", arg)
                .WithStyles(("width", "340px"), ("height", "150px"), ("margin", "0px 14px"), ("border-radius", "10px"),
                    ("background-color", s.Vote == r ? "#4f8a4a" : "#17140ff2"), ("border", "2px solid #b08d57"))
                .WithHoverStyle("background-color", "#3d6b3a")
                .WithTextStyles(("font-size", "34px"), ("color", "#f5e6c8"), ("horizontal-align", "center"), ("vertical-align", "center"));

        var to = RecipientFilter.Single(s.Slot);
        UI.Panel(PanelId).BuildLayout(to, Frame(
            UI.Label("ad_title", s.Ru ? "ПРАВИЛА МАТЧА" : "MATCH RULES").WithStyles(TitleStyle),
            UI.Horizontal("ad_row").WithStyles(("horizontal-align", "center"), ("margin-top", "24px"))
                .Add(Choice("ad_std", "Standard [1]", "standard", Rules.Standard), Choice("ad_brawl", "Street Brawl [2]", "brawl", Rules.StreetBrawl))));
        if (!s.PanelUp) UI.Panel(PanelId).RequestCursor(to);
        s.PanelUp = true;
    }

    // ---- world-text menu for clients without the panel ----------------------------------------------------------
    void SetBillboard(Seat s, string text)
    {
        if (s.Billboard is { IsValid: true }) { s.Billboard.SetMessage(text); return; }
        var pawn = Players.FromSlot(s.Slot)?.GetHeroPawn();
        if (pawn == null) return;
        var t = CPointWorldText.Create(text, pawn.EyePosition, fontSize: 64, worldUnitsPerPx: BillboardScale);
        if (t == null) { Log($"slot {s.Slot}: world text could not be created"); return; }
        t.JustifyHorizontal = HorizontalJustify.Center;
        t.JustifyVertical = VerticalJustify.Center;
        t.Fullbright = true;
        s.Billboard = t;
        PlaceBillboard(s);
    }

    /// <summary>Holds the menu in front of the player's view, upright and facing them, wherever they look.</summary>
    void PlaceBillboard(Seat s)
    {
        if (s.Billboard is not { IsValid: true } t || Players.FromSlot(s.Slot)?.GetHeroPawn() is not { } pawn) return;
        var view = pawn.CameraAngles;
        float pitch = view.X * MathF.PI / 180f, yaw = view.Y * MathF.PI / 180f;
        var forward = new System.Numerics.Vector3(MathF.Cos(pitch) * MathF.Cos(yaw), MathF.Cos(pitch) * MathF.Sin(yaw), -MathF.Sin(pitch));
        var up = new System.Numerics.Vector3(MathF.Sin(pitch) * MathF.Cos(yaw), MathF.Sin(pitch) * MathF.Sin(yaw), MathF.Cos(pitch));
        // point_worldtext reads along its local axes; this yaw/roll pair turns its face back towards the viewer.
        t.Teleport(pawn.EyePosition + forward * BillboardDistance + up * BillboardRaise, new System.Numerics.Vector3(0, view.Y + 270f, 90f - view.X));
    }

    public override void OnGameFrame(bool simulating, bool firstTick, bool lastTick)
    {
        TickNativeDraft();
        if (_phase is not (Phase.Drafting or Phase.Voting)) return;
        foreach (var s in _seats.Values) PlaceBillboard(s);
    }

    static void ClearBillboard(Seat s)
    {
        if (s.Billboard is { IsValid: true }) s.Billboard.Remove();
        s.Billboard = null;
    }

    // A menu is private: everybody else's billboard is kept off the wire.
    public override void OnCheckTransmit(CheckTransmitEvent args)
    {
        foreach (var s in _seats.Values)
            if (s.Slot != args.PlayerSlot && s.Billboard is { IsValid: true } b) args.Hide(b);
    }

    void HidePanel(Seat s)
    {
        ClearBillboard(s);
        if (!s.PanelUp) return;
        var to = RecipientFilter.Single(s.Slot);
        UI.Panel(PanelId).ReleaseCursor(to);
        UI.Panel(PanelId).DestroyLayout(to);
        s.PanelUp = false;
    }

    void OnUiOffer(UIEvent e, bool reroll)
    {
        if (e.Caller == null || !_seats.TryGetValue(e.Caller.Slot, out var s) || !int.TryParse(e.ArgAt(0), out int i)) return;
        if (reroll) Reroll(s, i); else Pick(s, i);
    }

    // A client that hitched drops its UI and cannot rebuild it: layouts live on the server.
    void OnClientResync(int slot)
    {
        if (!_seats.TryGetValue(slot, out var s) || !s.PanelUp) return;
        s.PanelUp = false;
        if (_phase == Phase.Voting) ShowVote(s);
        else if (_phase == Phase.Drafting) { if (s.Done) ShowWaiting(s); else ShowOffer(s); }
    }
}
