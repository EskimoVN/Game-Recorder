class CfgPatches {
    class odx_capture {
        name = "Odx Capture";
        units[] = {};
        weapons[] = {};
        requiredVersion = 2.18;
        requiredAddons[] = {"A3_Functions_F"};
    };
};

class CfgFunctions {
    class odx {
        class capture {
            file = "\odx_capture\functions";
            class init { postInit = 1; };
        };
    };
};
