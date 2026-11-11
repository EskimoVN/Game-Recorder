// God mode for capture: runs on each client where its own player is local (correct MP locality,
// community.bistudio.com/wiki/allowDamage). The loop reapplies after respawn or ownership change
// (vehicle entry, team switch), which drop the flag.
player allowDamage false;
[] spawn {
    while {true} do {
        if (!alive player) then { waitUntil { alive player }; };
        player allowDamage false;
        sleep 5;
    };
};
