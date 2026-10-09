// Ability Draft: the client half of what the stock UI only does for the hero's ORIGINAL abilities.
// Loaded by the server through the Deadworks UI bridge, for a player in a draft or holding a drafted kit. It is told:
//   train  = 1   the kit is live: a button over each of the four ability icons (TAB view) asks the server to train
//                "the ability in slot N"
//   skills       "<bits>:<can>" per slot (bit 0 unlocked, bits 1-3 the tiers, can = the next step is affordable):
//                shown on the game's OWN upgrade pips by setting the classes its stylesheet already has, so the
//                view looks exactly as in a normal match
//   buy          "<n>|<item>": send an ordinary purchase of an item the server will attach to a drafted ability
//   pick         "<n>|<card 0-2>": the player has just taken that card on the draft screen. The screen has a look
//                for a taken card and for the ones passed over, but only shows it for a real purchase; here the
//                same looks are put on by class. "dealt" = <n> says the next cards are out and takes them off
// The approach is from Binger4/Deadlock-Ability-Draft (MIT).
(function () {
	'use strict';
	var COST = [1, 2, 5];
	var slots = [], boundHud = null, enabled = false, alive = false, warned = false;
	var skills = '', lastBuy = '', pickSeq = 0, dealtSeq = 0, marked = [], handover = false, handoverUntil = 0;

	function root() { var p = $.GetContextPanel(); while (p.GetParent()) p = p.GetParent(); return p; }

	function descendants(p, type, out) {
		if (p.paneltype === type) out.push(p);
		p.Children().forEach(function (c) { descendants(c, type, out); });
		return out;
	}

	function unbind() {
		slots.forEach(function (s) { if (s.click.IsValid()) s.click.DeleteAsync(0); });
		slots = [];
		boundHud = null;
	}

	function bind() {
		var hud = root().FindChildTraverse('hud_signature');
		if (!hud) return false;
		if (hud === boundHud && slots.length === 4 && slots.every(function (s) { return s.click.IsValid() && s.pips.IsValid(); })) return true;
		unbind();
		var icons = descendants(hud, 'CitadelAbilityIcon', []);
		var pips = descendants(hud, 'CitadelHudAbilityUpgradePips', []);
		if (icons.length !== 4 || pips.length !== 4) {
			if (!warned) { $.Msg('[AbilityDraft] ability HUD not ready: icons=' + icons.length + ' pips=' + pips.length); warned = true; }
			return false;
		}
		icons.forEach(function (icon, i) {
			var parent = icon.FindChildTraverse('button_container') || icon;
			var click = $.CreatePanel('Button', parent, 'AbilityDraftTrainClick' + (i + 1));
			click.BLoadLayout('file://{resources}/layout/abilitydraft_click.xml', false, false);
			click.SetPanelEvent('onactivate', function () {
				$.DispatchEvent('CitadelConCommand', 'trainorupgradeability ' + (i + 1));
			});
			slots.push({ click: click, pips: pips[i] });
		});
		boundHud = hud;
		$.Msg('[AbilityDraft] upgrade view bound to the four ability slots');
		return true;
	}

	// The game sets these classes itself, for the hero's original ability; they are put back every frame.
	function paint() {
		var parts = skills.split(',');
		if (parts.length !== 4) return;
		slots.forEach(function (s, i) {
			var pair = parts[i].split(':'), bits = parseInt(pair[0], 10) || 0, can = pair[1] === '1';
			var unlocked = (bits & 1) !== 0, next = 4;
			for (var t = 3; t >= 1; t--) if ((bits & (1 << t)) === 0) next = t;
			s.pips.SetHasClass('isUnlocked', unlocked);
			s.pips.SetHasClass('not_trained', !unlocked);
			s.pips.SetHasClass('canUnlock', !unlocked && can);
			s.pips.SetHasClass('ability_upgrade_available', unlocked && can && next <= 3);
			for (var tier = 1; tier <= 3; tier++) {
				var row = s.pips.FindChildTraverse('AbilityUnlock' + tier);
				if (!row) continue;
				var has = (bits & (1 << tier)) !== 0;
				row.SetHasClass('hasAbilityUpgrade', has);
				row.SetHasClass('canAffordUpgrade', unlocked && !has && tier === next && can);
				row.SetDialogVariableInt('ability_point_cost', COST[tier - 1]);
			}
		});
	}

	// The HUD is rebuilt on respawn and hero change, so the binding is checked again and again, not made once.
	function tick() {
		if (!alive) return;
		if (enabled) { if (bind()) paint(); } else if (slots.length) unbind();
		if (handover) handOver();
		$.Schedule(0, tick);
	}

	function unmark() {
		marked.forEach(function (m) { if (m.panel.IsValid()) m.panel.RemoveClass(m.cls); });
		marked = [];
		handover = false;
	}

	// The pick, part one: the taken card grows, the other two fade.
	function showPick(card) {
		var container = root().FindChildTraverse('OptionsContainer');
		if (!container) return;
		unmark();
		container.Children().forEach(function (option, i) {
			var cls = i === card ? 'selected' : 'dismiss';
			option.AddClass(cls);
			marked.push({ panel: option, cls: cls });
		});
		// Tell the server the pick is on screen: it holds the next deal until then.
		$.DispatchEvent('CitadelConCommand', 'ad_ack ' + pickSeq);
		// Never leave a card faded out if the server's "dealt" gets lost.
		var mine = pickSeq;
		$.Schedule(4.0, function () { if (pickSeq === mine && !handover) unmark(); });
	}

	// Part two, once the next cards are out. The screen itself keeps the old cards up through the wheel spin, swaps
	// their content and only then plays each card's reveal: it puts the class "reveal" on the card for the length
	// of that animation (a card at rest has none of the state classes). So all three stay faded here until the
	// screen starts a card's reveal; from then on that card is the screen's again.
	function startHandover() {
		marked.forEach(function (m) {
			if (!m.panel.IsValid() || m.cls === 'dismiss') return;
			m.panel.RemoveClass(m.cls);
			m.panel.AddClass('dismiss');
			m.cls = 'dismiss';
		});
		handover = true;
		handoverUntil = Date.now() + 4000;
	}

	function handOver() {
		var late = Date.now() > handoverUntil;
		marked = marked.filter(function (m) {
			if (!m.panel.IsValid()) return false;
			if (!late && !m.panel.BHasClass('reveal') && !m.panel.BHasClass('anticipation')) return true;
			m.panel.RemoveClass(m.cls);
			return false;
		});
		if (late) report('handover timed out');
		if (marked.length === 0) handover = false;
	}

	function report(text) { $.DispatchEvent('CitadelConCommand', 'ad_dbg ' + text.replace(/[^A-Za-z0-9+\/ _-]/g, '')); }

	function read(p) {
		enabled = p.get('train', '0') === '1';
		skills = p.get('skills', '');
		var dealtParts = p.get('dealt', '0').split('|'), dealt = parseInt(dealtParts[0], 10) || 0;
		var pick = p.get('pick', '').split('|'), seq = parseInt(pick[0], 10) || 0;
		if (dealt > dealtSeq) {
			dealtSeq = dealt;
			// "|1": the server waited for this pick to show, so the cards stay away until the new ones come in.
			if (dealt >= pickSeq && marked.length) { if (dealtParts[1] === '1') startHandover(); else unmark(); }
		}
		// A pick whose deal is already out arrived too late to be shown.
		if (seq > pickSeq) { pickSeq = seq; if (seq > dealtSeq) showPick(parseInt(pick[1], 10) || 0); }
		var buy = p.get('buy', '');
		if (buy !== lastBuy) {
			lastBuy = buy;
			var item = buy.split('|')[1] || '';
			if (enabled && /^upgrade_[a-z0-9_]+$/.test(item)) $.DispatchEvent('CitadelConCommand', 'buyitem ' + item);
		}
	}

	DW.registerPanel({
		// Requests that were already there when the panel (re)loaded are old: do not act on them again.
		init: function (p) {
			alive = true;
			lastBuy = p.get('buy', '');
			pickSeq = parseInt(p.get('pick', '').split('|')[0], 10) || 0;
			read(p);
			tick();
		},
		render: function (p) { read(p); },
		onDestroy: function () { alive = false; unbind(); unmark(); }
	});
}());
