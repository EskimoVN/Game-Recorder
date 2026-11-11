// Each player starts on the ground at the spawn point of its slot group (initServer.sqf); the unit positions in
// mission.sqm carry no checked height. Slot odx_NN → point floor((NN - 1) / 4), with 10 m of scatter.
ODX_fnc_placeAtSpawn = {
	private _slot = parseNumber ((vehicleVarName player) select [4]);
	private _k = ((floor ((_slot - 1) / 4)) min (count ODX_spawns - 1)) max 0;
	player setPos ((ODX_spawns # _k) vectorAdd [random 10 - 5, random 10 - 5, 0]);
};
waitUntil { !isNil "ODX_spawns" };
[] call ODX_fnc_placeAtSpawn;
