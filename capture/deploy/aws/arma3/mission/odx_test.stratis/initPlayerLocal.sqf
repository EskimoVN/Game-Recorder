// Each player starts on the ground at the respawn marker; the unit positions in mission.sqm carry no checked height.
waitUntil { !isNull player };
player setPos ((getMarkerPos "respawn_west") vectorAdd [random 10, random 10, 0]);
