// Eskimo capture tweaks for the local player: god mode, infinite magazines, double stamina, no HUD. Only on Odx/Eskimo private play
// (single player, this machine hosting, or a server whose name starts with "Eskimo"); never on other servers, the
// company's Odx servers included: their rules stay as the company set them.
// The HUD itself is the company addon's (odx_capture ODX_fnc_hideHud, showHUD all off, applied at start and after a
// respawn); this only fills its gaps: it is applied again every frame, as a mission may turn the HUD back on mid-round.
if (!hasInterface) exitWith {};

// Keys check for odx-app, on every server: the capture maps each physical key to the action of its profile
// (capture/profiles/arma3.json), so a key rebound in Options > Controls records the wrong action. Every 30 s the actions
// whose profile key no longer triggers them go to the .rpt ("ESKIMO,keys,ok" or "ESKIMO,keys,bad,<actions>"); odx-app
// reads the last line and refuses ready while one is listed. An action the game does not know (no keys) is skipped, so a
// wrong name here never blocks a player. Keys are DIK codes; 65536 is the left mouse button.
[] spawn {
    private _expected = [
        ["MoveForward", "Forward", 17], ["MoveBack", "Backward", 31], ["TurnLeft", "Strafe Left", 30],
        ["TurnRight", "Strafe Right", 32], ["LeanLeft", "Lean Left", 16], ["LeanRight", "Lean Right", 18],
        ["ReloadMagazine", "Reload", 19], ["Throw", "Throw", 34], ["PersonView", "Toggle Third Person", 156],
        ["DefaultAction", "Fire", 65536]
    ];
    while {true} do {
        private _bad = [];
        {
            _x params ["_action", "_name", "_key"];
            private _keys = actionKeys _action;
            if (count _keys > 0 && {!(_key in _keys)}) then { _bad pushBack _name; };
        } forEach _expected;
        diag_log (if (_bad isEqualTo []) then { "ESKIMO,keys,ok" } else { "ESKIMO,keys,bad," + (_bad joinString ";") });
        sleep 30;
    };
};

[] spawn {
    waitUntil { !isNull player && {time > 0} };

    private _server = toLower serverName;
    private _allowed = !isMultiplayer || {isServer} || {(_server find "eskimo") == 0};
    if (!_allowed) exitWith { diag_log format ["ESKIMO,off,server %1", serverName]; };
    diag_log format ["ESKIMO,on,server %1,mission %2", serverName, missionName];

    // Every frame, so nothing a mission shows survives into a recorded frame. Besides the company's showHUD: hints (not
    // covered by it, see odx_capture fn_init.sqf), chat, the cut and title layers missions put their own HUDs, notifications
    // and tips on (Warlords' CP and income), and controls scripts add to the mission display 46.
    addMissionEventHandler ["EachFrame", {
        if (!isNil "ODX_fnc_hideHud") then { call ODX_fnc_hideHud; };
        showChat false;
        hintSilent "";
        { _x cutFadeOut 0; } forEach allCutLayers;
        for "_layer" from 0 to 20 do { _layer cutFadeOut 0; };
        titleFadeOut 0;
        { if (ctrlShown _x) then { _x ctrlShow false; }; } forEach allControls (findDisplay 46);
    }];

    // Reapplied every second: respawn gives a new unit, and missions may turn damage back on.
    while {true} do {
        if (alive player) then {
            player allowDamage false;
            if (isNil {player getVariable "eskimo_ammo"}) then {
                player setVariable ["eskimo_ammo", player addEventHandler ["FiredMan", {
                    params ["_unit", "_weapon", "", "", "", "_magazine", "", "_vehicle"];
                    if (!isNull _vehicle) exitWith { if (local _vehicle) then { _vehicle setVehicleAmmo 1; }; };
                    if (_weapon in ["Throw", "Put"]) then { _unit addMagazine _magazine; };
                }]];
                // Infinite magazines, not infinite rounds: the magazine empties and is reloaded as usual, and each reload
                // puts a full one of the same type back in the inventory, so the count never goes down.
                player setVariable ["eskimo_mags", player addEventHandler ["Reloaded", {
                    params ["_unit", "", "", "_new"];
                    _unit addMagazine (if (_new isEqualType []) then { _new select 0 } else { _new });
                }]];
            };
            // Backstop when a full inventory refused that magazine: the weapon in hand always keeps one spare.
            private _mag = currentMagazine player;
            if (_mag != "" && {vehicle player == player} && {!(_mag in magazines player)}) then { player addMagazine _mag; };
            // Double stamina: half of each second's drain is given back (runs last twice as long) and each second's
            // recovery is added again (recovers twice as fast). Measured per unit, so a respawn starts fresh.
            private _stamina = getStamina player;
            private _change = _stamina - (player getVariable ["eskimo_stamina", _stamina]);
            if (_change < 0) then { player setStamina (_stamina - _change / 2); };
            if (_change > 0) then { player setStamina (_stamina + _change); };
            player setVariable ["eskimo_stamina", getStamina player];
        };
        showSubtitles false;
        sleep 1;
    };
};
