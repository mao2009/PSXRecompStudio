cmake_minimum_required(VERSION 3.20)
project(PSXRecomp.Native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

add_library(PSXRecomp.Native SHARED
    src/psx_api.cpp
    src/psx_cpu.cpp
    src/psx_memory.cpp
    src/psx_dma.cpp
    src/psx_timer.cpp
    src/psx_interrupt.cpp
)

target_include_directories(PSXRecomp.Native
    PUBLIC  include
    PRIVATE src
)

target_compile_definitions(PSXRecomp.Native PRIVATE PSX_RECOMP_NATIVE_EXPORTS)

if(UNIX AND NOT APPLE)
    set_target_properties(PSXRecomp.Native PROPERTIES
        INSTALL_RPATH "$ORIGIN"
        BUILD_WITH_INSTALL_RPATH TRUE
    )
endif()

if(WIN32)
    # .NET's default P/Invoke probing on Windows looks up "PSXRecomp.Native.dll"
    # (no "lib" prefix) for [LibraryImport("PSXRecomp.Native")]. MinGW/GCC
    # toolchains default to prefixing shared libraries with "lib" (a Unix
    # convention some Windows GCC front-ends still apply), which would produce
    # "libPSXRecomp.Native.dll" and break that lookup. Force an empty prefix so
    # the artifact name matches the P/Invoke library name on every Windows
    # toolchain (MSVC already defaults to no prefix).
    set_target_properties(PSXRecomp.Native PROPERTIES PREFIX "")
endif()

enable_testing()
add_executable(psx_native_tests
    src/psx_cpu.cpp
    src/psx_memory.cpp
    src/psx_dma.cpp
    src/psx_timer.cpp
    src/psx_interrupt.cpp
    tests/test_psx_core.cpp
)
target_include_directories(psx_native_tests PRIVATE src include)
target_link_libraries(psx_native_tests PRIVATE PSXRecomp.Native)
add_test(NAME psx_native_tests COMMAND psx_native_tests)
