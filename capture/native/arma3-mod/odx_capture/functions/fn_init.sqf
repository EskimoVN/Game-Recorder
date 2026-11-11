// Passes the render camera to odx_capture_x64.dll once per frame; the row layout is pinned by
// capture/dotnet/fixtures/arma3_camera.csv (the extension prefixes t_qpc_ms). toFixed because callExtension's string
// form would get str's 6 significant digits, too coarse for Altis coordinates. positionCameraToWorld takes config order
// [right, up, forward] and returns AGL; ASL is the only absolute height (document/games/arma3.md §2, §3).
if (!hasInterface) exitWith {};

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
