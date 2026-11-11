// Capture setup, per client (its own player is local here -> correct MP locality, community.bistudio.com/wiki/allowDamage).
// 1) Move into the nearest named town so the round starts in a built-up area on land, on ANY terrain, without
//    hard-coding per-map coordinates (Tanoa etc. are mostly water). Small random spread so players don't stack.
private _l = nearestLocations [getPosATL player, ["NameCityCapital", "NameCity", "NameVillage", "NameLocal"], worldSize];
if !(_l isEqualTo []) then {
    private _p = locationPosition (_l select 0);
    player setPosATL [(_p select 0) + (random 40) - 20, (_p select 1) + (random 40) - 20, 0];
};
// 2) God mode; the loop reapplies after respawn or ownership change (vehicle entry, team switch), which drop the flag.
player allowDamage false;
[] spawn {
    while {true} do {
        if (!alive player) then { waitUntil { alive player }; };
        player allowDamage false;
        sleep 5;
    };
};
