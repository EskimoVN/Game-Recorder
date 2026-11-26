// SYNC-A3 capture test, per client (its own player is local here -> correct MP locality, community.bistudio.com/wiki/allowDamage).
// Same god-mode / no-AI setup as the capture missions; adds binoculars so round 3 can test the binocular view, and a
// clear midday sky for steady lighting so the camera-vs-video correlation (research/arma-sync-test) is clean.
// 1) Move into the nearest named town so the flicks sweep across buildings (high-contrast detail = strong correlation),
//    on ANY terrain, without hard-coding per-map coordinates. Small random spread so players don't stack.
private _l = nearestLocations [getPosATL player, ["NameCityCapital", "NameCity", "NameVillage", "NameLocal"], worldSize];
if !(_l isEqualTo []) then {
    private _p = locationPosition (_l select 0);
    player setPosATL [(_p select 0) + (random 40) - 20, (_p select 1) + (random 40) - 20, 0];
};
// 2) Binoculars for round 3 (the base loadout has none); hasWeapon so an ownership change does not add a second pair.
if (!(player hasWeapon "Binocular")) then { player addWeapon "Binocular"; };
// 3) Steady midday, clear sky, no wind, so lighting does not drift across a 90 s round (only the host's change takes on others).
if (isServer) then {
    0 setOvercast 0; 0 setRain 0; 0 setFog 0; forceWeatherChange;
    skipTime ((12 - dayTime + 24) % 24); setDate [2035, 7, 6, 12, 0];
};
// 4) God mode; the loop reapplies after respawn or ownership change (vehicle entry, team switch), which drop the flag.
player allowDamage false;
[] spawn {
    while {true} do {
        if (!alive player) then { waitUntil { alive player }; };
        player allowDamage false;
        sleep 5;
    };
};
