// Logs the render camera to the game's .rpt once per rendered frame, for odx-app's Arma3Camera to read after the round.
// The row layout is pinned by capture/dotnet/fixtures/arma3_camera.csv: a line "ODXCAM,<utc>,<view>,right,up,fwd,pos,vfov"
// where <utc> is systemTimeUTC to the millisecond. positionCameraToWorld takes config order [right, up, forward] and
// returns AGL; ASL is the only absolute height, and the axes are differences of float32 world positions, so odx-app
// re-orthonormalizes them (document/games/arma3.md §3, §5). cameraView is logged as its index in [INTERNAL, EXTERNAL,
// GUNNER, GROUP]. vfov is the vertical FOV in degrees: the profile FOV (getResolution's fovTop) scaled by the optics
// zoom (getObjectFOV over its 0.75 default).
if (!hasInterface) exitWith {};

addMissionEventHandler ["Draw3D", {
    private _origin = AGLToASL positionCameraToWorld [0, 0, 0];
    private _t = systemTimeUTC;
    private _fields = [
        format ["%1-%2-%3T%4:%5:%6.%7", _t#0, _t#1, _t#2, _t#3, _t#4, _t#5, _t#6],
        str (["INTERNAL", "EXTERNAL", "GUNNER", "GROUP"] find cameraView)
    ];
    {
        _fields append (((AGLToASL positionCameraToWorld _x) vectorDiff _origin) apply { _x toFixed 6 });
    } forEach [[1, 0, 0], [0, 1, 0], [0, 0, 1]];
    _fields append (_origin apply { _x toFixed 3 });
    _fields pushBack ((2 * atan ((getResolution select 6) * (getObjectFOV cameraOn) / 0.75)) toFixed 3);
    diag_log ("ODXCAM," + (_fields joinString ","));
}];
