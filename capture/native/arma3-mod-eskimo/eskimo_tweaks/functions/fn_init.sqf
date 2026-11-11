// Eskimo capture tweaks for the local player: god mode, infinite ammo, no HUD. Only on Odx/Eskimo private play
// (single player, this machine hosting, or a server whose name starts with "Odx" or "Eskimo"); never on other servers.
// Separate from odx_capture: its Draw3D camera logger is left alone. showHUD keeps element 0 (scripted HUD) on because
// off would also disable the action menu (doors, vehicles); the HUDs missions draw themselves are removed instead.
if (!hasInterface) exitWith {};

[] spawn {
    waitUntil { !isNull player && {time > 0} };

    private _server = toLower serverName;
    private _allowed = !isMultiplayer || {isServer} || {(_server find "odx") == 0} || {(_server find "eskimo") == 0};
    if (!_allowed) exitWith { diag_log format ["ESKIMO,off,server %1", serverName]; };
    diag_log format ["ESKIMO,on,server %1,mission %2", serverName, missionName];

    // Every frame, so nothing a mission shows survives into a recorded frame. showHUD elements: [scriptedHUD, info, radar,
    // compass, direction, menu, group, cursors, panels, kills, showIcon3D]; cursors off hides the action menu but keeps it
    // usable. Cut layers (cutRsc HUDs such as Warlords' CP and income, notifications, tips), hints and controls scripts add
    // to the mission display 46 are cleared as well.
    addMissionEventHandler ["EachFrame", {
        showHUD [true, false, false, false, false, false, false, false, false, false, false];
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
