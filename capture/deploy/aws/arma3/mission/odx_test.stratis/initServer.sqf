// Spawn points spread over the island, as the Reforger server's spawn_points.txt: 16 land positions at least 400 m
// apart, found by the engine (no water, gentle slope, clear of objects), one per group of 4 slots (odx_01–04 → point 0,
// odx_05–08 → point 1, …). Published for the clients (initPlayerLocal.sqf, onPlayerRespawn.sqf) and logged for the record.
private _points = [];
private _blacklist = [];
for "_k" from 0 to 15 do {
	private _pos = [[3500, 5500, 0], 0, 3200, 15, 0, 0.25, 0, _blacklist, [[2000, 5650, 0], [2000, 5650, 0]]] call BIS_fnc_findSafePos;
	_points pushBack _pos;
	_blacklist pushBack [_pos, 400];
	diag_log format ["ODXSPAWN,%1,%2,%3", _k, (_pos # 0) toFixed 1, (_pos # 1) toFixed 1];
};
ODX_spawns = _points;
publicVariable "ODX_spawns";
