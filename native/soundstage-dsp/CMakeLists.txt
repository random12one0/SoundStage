cmake_minimum_required(VERSION 3.15)
project(soundstage_dsp LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# The DSP engine core is header-only and portable: an INTERFACE library any consumer (the tests
# here, the Windows engine later) links against for the include path.
add_library(soundstage_dsp INTERFACE)
target_include_directories(soundstage_dsp INTERFACE ${CMAKE_CURRENT_SOURCE_DIR}/include)

enable_testing()
add_executable(soundstage_dsp_tests tests/test_dsp.cpp)
target_link_libraries(soundstage_dsp_tests PRIVATE soundstage_dsp)
if(MSVC)
    target_compile_options(soundstage_dsp_tests PRIVATE /W4)
else()
    target_compile_options(soundstage_dsp_tests PRIVATE -Wall -Wextra)
endif()
add_test(NAME soundstage_dsp_tests COMMAND soundstage_dsp_tests)

# Render tool: run a test signal through the engine and write dry/wet WAVs, so the sound can be
# checked by ear. Demo/preview only — not part of the shipped engine.
add_executable(soundstage_render tools/render.cpp)
target_link_libraries(soundstage_render PRIVATE soundstage_dsp)
target_include_directories(soundstage_render PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/tools)
