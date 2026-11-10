// A respawn goes back to the slot group's point (respawn = "BASE" alone would use the single respawn_west marker).
waitUntil { !isNil "ODX_fnc_placeAtSpawn" };
[] call ODX_fnc_placeAtSpawn;
