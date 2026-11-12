// Eskimo capture tweaks for the local player: god mode, infinite ammo, no HUD. Only on Odx/Eskimo private play
// (single player, this machine hosting, or a server whose name starts with "Eskimo"); never on other servers, the
// company's Odx servers included: their rules stay as the company set them.
// The HUD itself is the company addon's (odx_capture ODX_fnc_hideHud, showHUD all off, applied at start and after a
// respawn); this only fills its gaps: it is applied again every frame, as a mission may turn the HUD back on mid-round.
if (!hasInterface) exitWith {};

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
                    if (_weapon in ["Throw", "Put"]) exitWith { _unit addMagazine _magazine; };
                    _unit setVehicleAmmo 1;
                }]];
            };
        };
        showSubtitles false;
        sleep 1;
    };
};
