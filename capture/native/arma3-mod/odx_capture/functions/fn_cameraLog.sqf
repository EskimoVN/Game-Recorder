// The render camera every rendered frame, one diag_log line in the .rpt (raw log only, working rule 3; odx-app's
// Arma3Camera converts it). Pinned by capture/dotnet/fixtures/arma3_camera.csv:
//   ODXCAM,<utc Y-M-DTH:M:S.ms>,<view>,right xyz,up xyz,fwd xyz,pos xyz,vfov_deg
// utc is systemTimeUTC to the millisecond (SQF numbers are 32-bit floats, so the date is kept in parts, never as one
// number); view: 0 first person, 1 third person, 2 optics or gunner sight, 3 group, -1 other. The axes are the world
// directions of the camera's right, up and forward (positionCameraToWorld takes camera space as [right, up, forward]);
// the position is ASL in metres, Arma's world being X east, Y north, Z up (the spec's RFU). The vertical FOV comes
// from where points 0.5° above and below the view axis land on screen (zoom and the profile FOV included); the profile
// FOV alone when either is off screen.
if (!hasInterface) exitWith {};
if (!isNil "ODX_cameraLog") exitWith {};

ODX_cameraLog = addMissionEventHandler ["Draw3D", {
	private _t = systemTimeUTC;
	private _o = positionCameraToWorld [0, 0, 0];
	private _r = (positionCameraToWorld [1, 0, 0]) vectorDiff _o;
	private _u = (positionCameraToWorld [0, 1, 0]) vectorDiff _o;
	private _f = (positionCameraToWorld [0, 0, 1]) vectorDiff _o;
	private _p = AGLToASL _o;
	private _a = worldToScreen (positionCameraToWorld [0, tan 0.5, 1]);
	private _b = worldToScreen (positionCameraToWorld [0, -(tan 0.5), 1]);
	private _fov = if (count _a > 1 && {count _b > 1} && {(_b select 1) - (_a select 1) > 1e-6}) then {
		2 * atan ((tan 0.5) * safeZoneH / ((_b select 1) - (_a select 1)))
	} else {
		2 * atan (getResolution select 5)
	};
	private _view = switch (cameraView) do {
		case "INTERNAL": { 0 };
		case "EXTERNAL": { 1 };
		case "GUNNER": { 2 };
		case "GROUP": { 3 };
		default { -1 };
	};
	private _fields = [format ["ODXCAM,%1-%2-%3T%4:%5:%6.%7", _t # 0, _t # 1, _t # 2, _t # 3, _t # 4, _t # 5, _t # 6], str _view];
	_fields append ((_r + _u + _f) apply { _x toFixed 6 });
	_fields append (_p apply { _x toFixed 3 });
	_fields pushBack (_fov toFixed 3);
	diag_log (_fields joinString ",");
}];
