cmake_minimum_required(VERSION 3.24)
project(odx_extractor LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
add_compile_options($<$<C_COMPILER_ID:MSVC>:/utf-8> $<$<CXX_COMPILER_ID:MSVC>:/utf-8>)

include(FetchContent)

# Lua 5.4 (64-bit integers are needed for addresses; LuaJIT lacks them).
FetchContent_Declare(lua
  URL https://www.lua.org/ftp/lua-5.4.7.tar.gz
  URL_HASH SHA256=9fbf5e28ef86c69858f6d3d34eccc32e911c1a28b4120ff3e84aaa70cfbf1e30
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
FetchContent_MakeAvailable(lua)
file(GLOB LUA_SOURCES ${lua_SOURCE_DIR}/src/*.c)
list(REMOVE_ITEM LUA_SOURCES ${lua_SOURCE_DIR}/src/lua.c ${lua_SOURCE_DIR}/src/luac.c)
add_library(lua_static STATIC ${LUA_SOURCES})
target_include_directories(lua_static PUBLIC ${lua_SOURCE_DIR}/src)
if(UNIX)
  target_compile_definitions(lua_static PRIVATE LUA_USE_POSIX)
endif()

# Portable core: memory/signatures, Lua profiles, camera sample recorder. Canonical geometry, input aggregation and
# frames.csv / session.json live in dotnet/ only (odx build), which consumes the recorder's output.
add_library(odx_core STATIC
  src/memory.cpp src/profile.cpp src/config.cpp src/recorder.cpp src/fake_game.cpp src/arma3_log.cpp)
target_include_directories(odx_core PUBLIC include)
target_link_libraries(odx_core PUBLIC lua_static)

add_executable(odx_synth tools/synth_session.cpp)
target_link_libraries(odx_synth PRIVATE odx_core)

# Windows: the injectable extractor DLL (Present hook + Raw Input + in-process memory).
if(WIN32)
  FetchContent_Declare(minhook
    GIT_REPOSITORY https://github.com/TsudaKageyu/minhook.git
    GIT_TAG v1.3.3
    SOURCE_SUBDIR none)  # upstream CMakeLists predates CMake 4; build sources directly
  FetchContent_MakeAvailable(minhook)
  file(GLOB MINHOOK_SOURCES ${minhook_SOURCE_DIR}/src/*.c ${minhook_SOURCE_DIR}/src/hde/*.c)
  add_library(minhook STATIC ${MINHOOK_SOURCES})
  target_include_directories(minhook PUBLIC ${minhook_SOURCE_DIR}/include)
  add_library(odx_extractor SHARED
    src/win/dllmain.cpp src/win/present_hook.cpp src/win/process_memory.cpp)
  target_link_libraries(odx_extractor PRIVATE odx_core minhook d3d11 dxgi psapi)
  target_compile_definitions(odx_extractor PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)

  # Arma 3 loads <name>_x64.dll for `"odx_capture" callExtension ...` from the addon's folder.
  add_library(odx_capture SHARED src/win/arma3_extension.cpp)
  set_target_properties(odx_capture PROPERTIES OUTPUT_NAME odx_capture_x64)
  target_link_libraries(odx_capture PRIVATE odx_core)
  target_compile_definitions(odx_capture PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)

  add_executable(odx_inject tools/win/inject_main.cpp)
  target_compile_definitions(odx_inject PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)

  # Ship the default config and profiles next to the DLL.
  add_custom_command(TARGET odx_extractor POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy ${CMAKE_CURRENT_SOURCE_DIR}/odx_config.lua $<TARGET_FILE_DIR:odx_extractor>
    COMMAND ${CMAKE_COMMAND} -E copy_directory ${CMAKE_CURRENT_SOURCE_DIR}/profiles $<TARGET_FILE_DIR:odx_extractor>/profiles)
endif()

# Tests
option(ODX_BUILD_TESTS "Build tests" ON)
if(ODX_BUILD_TESTS)
  enable_testing()
  FetchContent_Declare(doctest
    GIT_REPOSITORY https://github.com/doctest/doctest.git
    GIT_TAG v2.4.11
    SOURCE_SUBDIR none)  # header-only; upstream CMakeLists predates CMake 4
  FetchContent_MakeAvailable(doctest)
  add_executable(odx_tests tests/test_core.cpp)
  target_include_directories(odx_tests PRIVATE ${doctest_SOURCE_DIR})
  target_link_libraries(odx_tests PRIVATE odx_core)
  target_compile_definitions(odx_tests PRIVATE ODX_PROFILES_DIR="${CMAKE_CURRENT_SOURCE_DIR}/profiles"
                                              ODX_FIXTURES_DIR="${CMAKE_CURRENT_SOURCE_DIR}/../../dotnet/fixtures")
  add_test(NAME core COMMAND odx_tests)

  # End to end: fake game -> Lua profile -> camera samples + synthetic capture logs -> odx build -> odx validate.
  # The last two need dotnet/ built first; a Debug build is preferred over Release (CI builds Release only).
  set(ODX_SYNTH_DIR ${CMAKE_CURRENT_BINARY_DIR}/synth_capture)
  add_test(NAME synth_capture
    COMMAND odx_synth ${CMAKE_CURRENT_SOURCE_DIR}/profiles/synthetic.lua ${ODX_SYNTH_DIR} 300)
  set(ODX_CLI_BIN ${CMAKE_CURRENT_SOURCE_DIR}/../../dotnet/src/Apps/Odx.Cli/bin)
  find_program(ODX odx HINTS ${ODX_CLI_BIN}/Debug/net10.0 ${ODX_CLI_BIN}/Release/net10.0)
  if(ODX)
    add_test(NAME synth_capture_builds
      COMMAND ${ODX} build ${ODX_SYNTH_DIR} --profile ${CMAKE_CURRENT_SOURCE_DIR}/tests/synthetic-game.json)
    set_tests_properties(synth_capture_builds PROPERTIES DEPENDS synth_capture)
    add_test(NAME synth_capture_validates COMMAND ${ODX} validate --no-video ${ODX_SYNTH_DIR})
    set_tests_properties(synth_capture_validates PROPERTIES DEPENDS synth_capture_builds)
  endif()
endif()
