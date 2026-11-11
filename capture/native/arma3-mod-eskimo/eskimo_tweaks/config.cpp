class CfgPatches {
    class eskimo_tweaks {
        name = "Eskimo capture tweaks";
        units[] = {};
        weapons[] = {};
        requiredVersion = 2.18;
        requiredAddons[] = {"A3_Functions_F"};
    };
};

class CfgFunctions {
    class eskimo {
        class tweaks {
            file = "\eskimo_tweaks\functions";
            class init { postInit = 1; };
        };
    };
};
