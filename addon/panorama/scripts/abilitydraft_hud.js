// Ability Draft: makes the TAB upgrade view work for a drafted kit.
// The stock view is built for the hero's ORIGINAL abilities: a click asks the server to train one of those, and
// the upgrade pips show their state. A drafted kit no longer has them. So this
//   - puts a button over each of the four ability icons that asks for "the ability in slot N" instead (the plugin
//     answers with the engine's own training function, so points, requirements and effects stay stock), and
//   - hides the stock pips and draws its own from the state the server sends ("skills": per slot "<bits>:<can>",
//     bit 0 = unlocked, bits 1-3 = the three tiers, can = the next step is affordable right now).
// Loaded by the server through the Deadworks UI bridge, and only for players who hold a drafted kit.
// The approach and the pip drawing are from Binger4/Deadlock-Ability-Draft (MIT).
(function () {
	'use strict';
	var COST = [1, 2, 5];
	var bindings = [], boundHud = null, enabled = false, alive = false, warned = false, skills = '';

	function root() { var p = $.GetContextPanel(); while (p.GetParent()) p = p.GetParent(); return p; }

	function descendants(p, type, out) {
		if (p.paneltype === type) out.push(p);
		p.Children().forEach(function (c) { descendants(c, type, out); });
		return out;
	}

	function style(p, values) { Object.keys(values).forEach(function (k) { p.style[k] = values[k]; }); }

	function train(slot) { $.DispatchEvent('CitadelConCommand', 'trainorupgradeability ' + slot); }

	function nextTier(bits) { for (var t = 1; t <= 3; t++) if ((bits & (1 << t)) === 0) return t; return 4; }

	function unbind() {
		bindings.forEach(function (b) {
			b.saved.forEach(function (s) { if (s.panel.IsValid()) s.panel.style.visibility = s.visibility || null; });
			if (b.container && b.container.IsValid()) b.container.DeleteAsync(0);
			if (b.click && b.click.IsValid()) b.click.DeleteAsync(0);
		});
		bindings = [];
		boundHud = null;
	}

	function bindOne(pips, icon, slot) {
		var b = { pips: pips, slot: slot, saved: [], rows: [] };
		pips.Children().forEach(function (child) {
			b.saved.push({ panel: child, visibility: child.style.visibility });
			child.style.visibility = 'collapse';
		});
		b.container = $.CreatePanel('Panel', pips, 'AbilityDraftPips');
		style(b.container, { width: '100%', height: '100%', flowChildren: 'up' });
		for (var tier = 1; tier <= 3; tier++) {
			var row = $.CreatePanel('Button', b.container, 'AbilityDraftTier' + tier);
			style(row, { width: '100%', height: '40px', marginTop: '5px', borderRadius: '100px' });
			var label = $.CreatePanel('Label', row, '');
			style(label, { horizontalAlign: 'center', verticalAlign: 'center', fontSize: '22px', fontWeight: 'bold' });
			row.SetPanelEvent('onactivate', function () { train(slot); });
			b.rows.push({ panel: row, label: label, tier: tier });
		}
		var parent = icon.FindChildTraverse('button_container') || icon;
		b.click = $.CreatePanel('Button', parent, 'AbilityDraftTrainClick' + slot);
		b.click.BLoadLayout('file://{resources}/layout/abilitydraft_click.xml', false, false);
		b.click.SetPanelEvent('onactivate', function () { train(slot); });
		return b;
	}

	function bind() {
		var hud = root().FindChildTraverse('hud_signature');
		if (!hud) return false;
		if (hud === boundHud && bindings.length === 4 && bindings.every(function (b) { return b.pips.IsValid() && b.click.IsValid(); })) return true;
		unbind();
		var pips = descendants(hud, 'CitadelHudAbilityUpgradePips', []);
		var icons = descendants(hud, 'CitadelAbilityIcon', []);
		if (pips.length !== 4 || icons.length !== 4) {
			if (!warned) { $.Msg('[AbilityDraft] ability HUD not ready: pips=' + pips.length + ' icons=' + icons.length); warned = true; }
			return false;
		}
		for (var i = 0; i < 4; i++) bindings.push(bindOne(pips[i], icons[i], i + 1));
		boundHud = hud;
		$.Msg('[AbilityDraft] upgrade view bound to the four ability slots');
		return true;
	}

	function draw() {
		var parts = skills.split(',');
		if (parts.length !== 4) return;
		bindings.forEach(function (b, i) {
			var pair = parts[i].split(':'), bits = parseInt(pair[0], 10) || 0, can = pair[1] === '1';
			// The game re-shows its own pips now and then; keep them hidden while ours are up.
			b.saved.forEach(function (s) { if (s.panel.IsValid()) s.panel.style.visibility = 'collapse'; });
			b.rows.forEach(function (row) {
				var learned = (bits & (1 << row.tier)) !== 0;
				var next = row.tier === nextTier(bits);
				var ready = !learned && next && can;
				row.panel.enabled = ready;
				row.panel.style.opacity = learned || ready ? '1' : '0.45';
				row.panel.style.backgroundColor = learned ? '#c8b0f5' : ready ? '#69d897' : '#191c1b';
				row.label.style.color = learned || ready ? '#17221c' : '#c3c6c3';
				row.label.text = learned ? '✓' : String(COST[row.tier - 1]);
			});
		});
	}

	// The HUD is rebuilt on respawn and hero change, so the binding is checked again and again, not made once.
	function tick() {
		if (!alive) return;
		if (enabled) { if (bind()) draw(); } else if (bindings.length) unbind();
		$.Schedule(0.25, tick);
	}

	function read(p) { enabled = p.get('train', '0') === '1'; skills = p.get('skills', ''); }

	DW.registerPanel({
		init: function (p) { alive = true; read(p); tick(); },
		render: function (p) { read(p); },
		onDestroy: function () { alive = false; unbind(); }
	});
}());
