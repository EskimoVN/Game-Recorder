// Each player starts on the ground at the spawn point of its slot group (initServer.sqf); the unit positions in
// mission.sqm carry no checked height.
waitUntil { !isNil "ODX_spawns" };
[] call ODX_fnc_placeAtSpawn;
