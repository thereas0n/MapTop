import { Entity, Instance } from "cs_script/point_script";

const PANEL_ID = "MapTopPanel";
const VISIBLE_CLASS = "MapTopVisible";

const ROWS = [
    "MapTopRow1",
    "MapTopRow2",
    "MapTopRow3"
];

let hudLayout = null;

// Data channel: numeric protocol via RunScriptInput input names.
// Player name is sent by the plugin character-by-character (MapTopNameChar_<code>),
// so disconnected players are shown correctly with the "(left)" mark.
let _viewerSlot = 0;
let _place = 1;
let _kills = 0;
let _nameCodes = [];

function GetHudLayout() {
    if (hudLayout instanceof Entity && hudLayout.IsValid()) {
        return hudLayout;
    }
    hudLayout = Instance.FindEntityByName("maptop_layout");
    if (!(hudLayout instanceof Entity) || !hudLayout.IsValid()) {
        Instance.Msg("MAPTOP_HUD_LAYOUT_NOT_FOUND");
        hudLayout = null;
        return null;
    }
    Instance.Msg("MAPTOP_HUD_LAYOUT_FOUND");
    return hudLayout;
}

function SetRow(viewerSlot, rowIndex, place, name, kills) {
    let layout = GetHudLayout();
    if (layout == null) return;
    let varName = "row" + (rowIndex + 1);
    layout.SetDialogVariableString(PANEL_ID, varName, place + ". " + name + " - " + kills);
    Instance.Msg("MAPTOP_ROW_SET_v" + viewerSlot + "_r" + (rowIndex + 1) + " [" + name + " - " + kills + "]");
}

function ClearRow(viewerSlot, rowIndex) {
    let layout = GetHudLayout();
    if (layout == null) return;
    let varName = "row" + (rowIndex + 1);
    layout.SetDialogVariableString(PANEL_ID, varName, "");
}

function ClearAll(viewerSlot) {
    for (let i = 0; i < ROWS.length; i++) {
        ClearRow(viewerSlot, i);
    }
}

function ShowHud(viewerSlot) {
    let layout = GetHudLayout();
    if (layout == null) return;
    layout.SetHasClassForPlayer(viewerSlot, PANEL_ID, VISIBLE_CLASS, true);
    Instance.Msg("MAPTOP_HUD_SHOW_v" + viewerSlot);
}

function HideHud(viewerSlot) {
    let layout = GetHudLayout();
    if (layout == null) return;
    layout.SetHasClassForPlayer(viewerSlot, PANEL_ID, VISIBLE_CLASS, false);
    Instance.Msg("MAPTOP_HUD_HIDE_v" + viewerSlot);
}

// Register numeric inputs in loops
for (let i = 0; i <= 63; i++) {
    Instance.OnScriptInput("MapTopViewer_" + i, () => { _viewerSlot = i; });
    // Slot is part of the input name, so show/hide for one player never
    // races with another player's show/hide through the shared _viewerSlot.
    Instance.OnScriptInput("MapTopShow_" + i, () => { ShowHud(i); });
    Instance.OnScriptInput("MapTopHide_" + i, () => { HideHud(i); });
}
for (let i = 0; i <= 9; i++) {
    Instance.OnScriptInput("MapTopPlace_" + i, () => { _place = i; });
    Instance.OnScriptInput("MapTopKillDigit_" + i, () => { _kills = _kills * 10 + i; });
}
// Name character codes: latin, cyrillic, punctuation (32..1279)
for (let i = 32; i <= 1279; i++) {
    Instance.OnScriptInput("MapTopNameChar_" + i, () => { _nameCodes.push(i); });
}

Instance.OnScriptInput("MapTopNameReset", () => { _nameCodes = []; });

Instance.OnScriptInput("MapTopRowCommit", () => {
    let name = "";
    for (let i = 0; i < _nameCodes.length; i++) {
        name = name + String.fromCharCode(_nameCodes[i]);
    }
    if (name == "") name = "Player";
    let k = Math.min(_kills, 999);
    SetRow(_viewerSlot, _place - 1, _place, name, k);
    _nameCodes = [];
    _place = 1;
    _kills = 0;
});

Instance.OnScriptInput("MapTopTestStatic", () => {
    Instance.Msg("MAPTOP_TESTSTATIC_START");
    let layout = GetHudLayout();
    if (layout == null) {
        Instance.Msg("MAPTOP_TESTSTATIC_NO_LAYOUT");
        return;
    }
    layout.SetDialogVariableString(PANEL_ID, "row1", "1. TestPlayer - 42");
    layout.SetDialogVariableString(PANEL_ID, "row2", "2. TestPlayer2 - 33");
    layout.SetDialogVariableString(PANEL_ID, "row3", "3. TestPlayer3 - 25");
    Instance.Msg("MAPTOP_TESTSTATIC_VARS_SET");
    layout.SetHasClassForPlayer(0, PANEL_ID, VISIBLE_CLASS, false);
    Instance.Delay(0.1).then(() => {
        layout.SetHasClassForPlayer(0, PANEL_ID, VISIBLE_CLASS, true);
        Instance.Msg("MAPTOP_TESTSTATIC_DONE");
    });
});

Instance.OnScriptInput("MapTopClear", () => { ClearAll(_viewerSlot); });
Instance.OnScriptInput("MapTopShow", () => { ShowHud(_viewerSlot); });
Instance.OnScriptInput("MapTopHide", () => { HideHud(_viewerSlot); });

Instance.Msg("MAPTOP_HUD_SCRIPT_LOADED");