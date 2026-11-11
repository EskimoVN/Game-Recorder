#define DOCTEST_CONFIG_IMPLEMENT_WITH_MAIN
#include <doctest/doctest.h>

#include <filesystem>
#include <fstream>
#include <sstream>

#include "odx/arma3_log.hpp"
#include "odx/config.hpp"
#include "odx/fake_game.hpp"
#include "odx/memory.hpp"
#include "odx/profile.hpp"
#include "odx/recorder.hpp"

using namespace odx;

namespace {
std::string profile_path() { return std::string(ODX_PROFILES_DIR) + "/synthetic.lua"; }

std::string read_file(const std::filesystem::path& p) {
    std::stringstream s;
    s << std::ifstream(p, std::ios::binary).rdbuf();
    return s.str();
}
}  // namespace

TEST_CASE("pattern parsing, scan and RIP resolution") {
    CHECK_FALSE(parse_pattern("48 8B zz").has_value());
    CHECK(parse_pattern("48 ?? 05")->size() == 3);

    FakeGame game;
    auto span = game.memory().module("game.exe");
    REQUIRE(span);
    auto hit = scan(game.memory(), *span, *parse_pattern("48 8B 05 ?? ?? ?? ?? 48 85 C0 74 ??"));
    REQUIRE(hit);
    CHECK(*hit == span->base + 0x100);
    auto global = resolve_rip(game.memory(), *hit, 3, 7);
    REQUIRE(global);
    CHECK(*global == span->base + 0x1000);
    CHECK_FALSE(scan(game.memory(), *span, *parse_pattern("DE AD BE EF")).has_value());
}

TEST_CASE("Lua profile finds the fake game's camera by signature and reads it") {
    FakeGame game;
    std::string error;
    auto profile = Profile::load(profile_path(), game.memory(), error);
    REQUIRE_MESSAGE(profile, error);
    REQUIRE_MESSAGE(profile->init(), profile->last_error());
    CHECK(profile->info().convention == "unreal");
    CHECK(profile->info().units_to_world == 0.01);
    CHECK(profile->info().fov_axis == "horizontal");

    CameraSample written{{450, -200, 170}, {0.6, 0.8, 0}, {-0.8, 0.6, 0}, {0, 0, 1}, 90};
    game.set_camera(written);
    auto read = profile->sample();
    REQUIRE_MESSAGE(read, profile->last_error());
    CHECK(read->position.x == 450);
    CHECK(read->position.y == -200);
    CHECK(read->forward.x == doctest::Approx(0.6));  // stored as float32
    CHECK(read->right.x == doctest::Approx(-0.8));
    CHECK(read->up.z == 1);
    CHECK(read->fov_deg == 90);
}

TEST_CASE("profile errors are reported, not thrown") {
    BufferMemory empty;
    std::string error;
    auto profile = Profile::load(profile_path(), empty, error);
    REQUIRE(profile);
    CHECK_FALSE(profile->init());  // module game.exe missing -> Lua error
    CHECK(profile->last_error().find("module not found") != std::string::npos);
    CHECK_FALSE(Profile::load("/nonexistent.lua", empty, error));
}

TEST_CASE("config resolves relative paths against its directory") {
    auto dir = std::filesystem::temp_directory_path() / "odx_config_test";
    std::filesystem::create_directories(dir);
    std::ofstream(dir / "odx_config.lua") << "return { profile = 'profiles/x.lua' }";
    std::string error;
    auto cfg = load_config((dir / "odx_config.lua").string(), error);
    REQUIRE_MESSAGE(cfg, error);
    CHECK(cfg->profile == (dir / "profiles/x.lua").string());
    CHECK(cfg->toggle_key == "F9");
}

TEST_CASE("recorder output matches the camera_samples.csv fixture read by odx build") {
    auto dir = std::filesystem::temp_directory_path() / "odx_recorder_test";
    std::filesystem::remove_all(dir);
    ProfileInfo info;
    info.game = "Test";
    info.convention = "unreal";
    info.units_to_world = 0.01;
    info.fov_axis = "horizontal";
    Recorder rec({dir.string(), info});
    std::string error;
    REQUIRE(rec.start(error));
    rec.on_frame(1000.5, {{100, -50.25, 170}, {1, 0, 0}, {0, 1, 0}, {0, 0, 1}, 90});
    rec.on_frame(1017.25, {{101.5, -50.25, 170}, {0.6, 0.8, 0}, {-0.8, 0.6, 0}, {0, 0, 1}, 90});
    CHECK(rec.stop() == 2);

    CHECK(read_file(dir / "camera_samples.csv") == read_file(std::string(ODX_FIXTURES_DIR) + "/camera_samples.csv"));
    std::string meta = read_file(dir / "extractor.json");
    CHECK(meta.find("\"convention\": \"unreal\"") != std::string::npos);
    CHECK(meta.find("\"units_to_world\": 0.01") != std::string::npos);
    CHECK(meta.find("\"fov_axis\": \"horizontal\"") != std::string::npos);
}

TEST_CASE("Arma 3 log follows control.txt and matches the arma3_camera.csv fixture read by odx build") {
    auto dir = std::filesystem::temp_directory_path() / "odx_arma3_test";
    std::filesystem::remove_all(dir);
    std::filesystem::create_directories(dir);
    auto control = dir / "control.txt", out = dir / "arma3_camera.csv";
    Arma3Log log(control);
    log.poll();
    CHECK_FALSE(log.recording());

    std::ofstream(control) << out.string() << "\n";
    log.poll();
    REQUIRE(log.recording());
    std::string fixture = read_file(std::string(ODX_FIXTURES_DIR) + "/arma3_camera.csv");
    std::istringstream rows(fixture);
    std::string row;
    std::getline(rows, row);
    while (std::getline(rows, row) && row != kArma3EndLine) {
        auto comma = row.find(',');
        std::string payload = row.substr(comma + 1);
        REQUIRE(arma3_frame_ok(payload));
        log.write(std::stod(row.substr(0, comma)), payload);
    }
    log.poll();
    std::filesystem::remove(control);
    log.poll();
    CHECK_FALSE(log.recording());
    CHECK(read_file(out) == fixture);
}

TEST_CASE("Arma 3 frame payloads need 15 fields on one line") {
    CHECK(arma3_frame_ok("INTERNAL,1,2,3,1,0,0,0,0,1,0,1,0,0.75,0.75"));
    CHECK_FALSE(arma3_frame_ok("INTERNAL,1,2,3"));
    CHECK_FALSE(arma3_frame_ok("INTERNAL,1,2,3,1,0,0,0,0,1,0,1,0,0.75,0.75,1"));
    CHECK_FALSE(arma3_frame_ok("INTERNAL,1,2,3,1,0,0,0,0,1,0,1,0,0.75,0.75\nx"));
}

TEST_CASE("format_double keeps full precision and a decimal point") {
    CHECK(format_double(0.0) == "0.0");
    CHECK(format_double(3.0) == "3.0");
    CHECK(format_double(-1.5) == "-1.5");
    CHECK(std::stod(format_double(0.1 + 0.2)) == 0.1 + 0.2);
}
