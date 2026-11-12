// Passes the render camera to odx_capture_x64.dll once per frame; the row layout is pinned by
// capture/dotnet/fixtures/arma3_camera.csv (the extension prefixes t_qpc_ms). toFixed because callExtension's string
// form would get str's 6 significant digits, too coarse for Altis coordinates. positionCameraToWorld takes config order
// [right, up, forward] and returns AGL; ASL is the only absolute height (document/games/arma3.md §2, §3).
if (!hasInterface) exitWith {};

// HUD-free video whatever difficulty the host picked (document/games/arma3.md §5): showHUD hides what the Custom
// difficulty flags leave (action menu, command bar, panels, cursors, group icons); the game turns it back on when the
// player respawns, so it is hidden again then. Hint popups are not covered by either.
ODX_fnc_hideHud = { showHUD [false, false, false, false, false, false, false, false, false, false, false]; };
call ODX_fnc_hideHud;
addMissionEventHandler ["EntityRespawned", { if ((_this select 0) isEqualTo player) then { call ODX_fnc_hideHud; }; }];

addMissionEventHandler ["Draw3D", {
    private _origin = AGLToASL positionCameraToWorld [0, 0, 0];
    private _fields = [cameraView];
    _fields append (_origin apply { _x toFixed 4 });
    {
        _fields append (((AGLToASL positionCameraToWorld _x) vectorDiff _origin) apply { _x toFixed 6 });
    } forEach [[1, 0, 0], [0, 1, 0], [0, 0, 1]];
    _fields pushBack ((getResolution select 6) toFixed 6);
    _fields pushBack ((getObjectFOV cameraOn) toFixed 6);
    "odx_capture" callExtension (_fields joinString ",");
}];
