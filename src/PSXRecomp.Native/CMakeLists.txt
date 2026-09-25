cmake_minimum_required(VERSION 3.20)
project(PSXRecomp.Native LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

# --- Rust coexistence substrate (Issue #473, ADR-023) ------------------------
# The Rust crate is compiled by cargo as a `staticlib` and linked into the same
# PSXRecomp.Native shared library the managed side already loads, so no second
# native artifact and no second native-loading path is introduced. cargo always
# builds the `release` profile: the crate is tiny, and pinning one profile keeps
# the output path identical under single- and multi-config generators.
find_program(CARGO_EXECUTABLE cargo REQUIRED)

set(PSX_RUST_DIR ${CMAKE_CURRENT_SOURCE_DIR}/rust)
set(PSX_RUST_TARGET_DIR ${CMAKE_CURRENT_BINARY_DIR}/rust)

# cargo builds for rustc's host triple by default, which matches the C++
# toolchain everywhere except Windows: there the host default is the MSVC
# triple, and a MinGW/GCC front-end cannot link an MSVC static library. Select
# the GNU triple explicitly in that case. Passing --target also moves the
# output under a per-triple directory, so the two paths differ.
set(PSX_RUST_CARGO_TARGET_ARGS)
set(PSX_RUST_PROFILE_DIR ${PSX_RUST_TARGET_DIR}/release)
if(MINGW)
    set(PSX_RUST_TARGET_TRIPLE x86_64-pc-windows-gnu)
    set(PSX_RUST_CARGO_TARGET_ARGS --target ${PSX_RUST_TARGET_TRIPLE})
    set(PSX_RUST_PROFILE_DIR ${PSX_RUST_TARGET_DIR}/${PSX_RUST_TARGET_TRIPLE}/release)

    # Because the GNU triple is not rustc's Windows host, its standard library
    # is usually absent — on a developer machine and on a CI runner alike,
    # where it surfaces as a confusing "can't find crate for `std`". Install it
    # at configure time: `rustup target add` is idempotent and a no-op once the
    # target is present. The working directory matters, because it is what
    # rustup resolves rust-toolchain.toml (and therefore the toolchain to add
    # the target to) from.
    find_program(RUSTUP_EXECUTABLE rustup)
    if(RUSTUP_EXECUTABLE)
        execute_process(
            COMMAND ${RUSTUP_EXECUTABLE} target add ${PSX_RUST_TARGET_TRIPLE}
            WORKING_DIRECTORY ${PSX_RUST_DIR}
            RESULT_VARIABLE PSX_RUST_TARGET_ADD_RESULT
            OUTPUT_VARIABLE PSX_RUST_TARGET_ADD_OUTPUT
            ERROR_VARIABLE PSX_RUST_TARGET_ADD_OUTPUT
        )
        if(NOT PSX_RUST_TARGET_ADD_RESULT EQUAL 0)
            message(WARNING
                "rustup could not add the ${PSX_RUST_TARGET_TRIPLE} target; the Rust "
                "build will fail if it is not already installed. Output:\n"
                "${PSX_RUST_TARGET_ADD_OUTPUT}")
        endif()
    else()
        message(STATUS
            "rustup not found; assuming the ${PSX_RUST_TARGET_TRIPLE} Rust target "
            "is provided by the environment.")
    endif()
endif()

set(PSX_RUST_STATICLIB
    ${PSX_RUST_PROFILE_DIR}/${CMAKE_STATIC_LIBRARY_PREFIX}psxrecomp_rust${CMAKE_STATIC_LIBRARY_SUFFIX})

add_custom_command(
    OUTPUT ${PSX_RUST_STATICLIB}
    COMMAND ${CARGO_EXECUTABLE} build --release
            ${PSX_RUST_CARGO_TARGET_ARGS}
            --manifest-path ${PSX_RUST_DIR}/Cargo.toml
            --target-dir ${PSX_RUST_TARGET_DIR}
    # rustup reads rust-toolchain.toml from the working directory, not from the
    # manifest path, so the pin only applies when cargo runs inside the crate.
    WORKING_DIRECTORY ${PSX_RUST_DIR}
    DEPENDS ${PSX_RUST_DIR}/src/lib.rs
            ${PSX_RUST_DIR}/src/cpu_alu.rs
            ${PSX_RUST_DIR}/src/interrupt.rs
            ${PSX_RUST_DIR}/src/cpu_ops.rs
            ${PSX_RUST_DIR}/src/cpu_hilo.rs
            ${PSX_RUST_DIR}/src/dma.rs
            ${PSX_RUST_DIR}/src/memory.rs
            ${PSX_RUST_DIR}/src/sio0.rs
            # PSXCpu migration slots, pre-registered once (Issue #524) so the
            # per-slice PRs (#525-#531) never edit this list.
            ${PSX_RUST_DIR}/src/cpu_decode.rs
            ${PSX_RUST_DIR}/src/cpu_control.rs
            ${PSX_RUST_DIR}/src/cpu_memory_access.rs
            ${PSX_RUST_DIR}/src/cpu_unaligned.rs
            ${PSX_RUST_DIR}/src/cpu_cop0.rs
            ${PSX_RUST_DIR}/src/cpu_exception.rs
            ${PSX_RUST_DIR}/src/cpu_pipeline.rs
            ${PSX_RUST_DIR}/Cargo.toml
            ${PSX_RUST_DIR}/Cargo.lock
            ${PSX_RUST_DIR}/rust-toolchain.toml
    COMMENT "Building Rust substrate (cargo build --release)"
    VERBATIM
)
add_custom_target(psx_rust_substrate DEPENDS ${PSX_RUST_STATICLIB})

# PSXCpu translation units, one per Rust migration slice (Issue #524). Shared
# by the library and the native test executable.
set(PSX_CPU_SOURCES
    src/psx_cpu.cpp
    src/psx_cpu_decode.cpp
    src/psx_cpu_control.cpp
    src/psx_cpu_memory_access.cpp
    src/psx_cpu_unaligned.cpp
    src/psx_cpu_cop0.cpp
    src/psx_cpu_exception.cpp
    src/psx_cpu_pipeline.cpp
)

add_library(PSXRecomp.Native SHARED
    src/psx_api.cpp
    ${PSX_CPU_SOURCES}
    src/psx_memory.cpp
    src/psx_rust_abi.cpp
)

add_dependencies(PSXRecomp.Native psx_rust_substrate)
target_link_libraries(PSXRecomp.Native PRIVATE ${PSX_RUST_STATICLIB})

if(UNIX)
    # Rust's std pulls in pthread and dlopen symbols; on Windows the equivalent
    # system libraries are requested by the linker directives rustc embeds in
    # the archive's objects.
    find_package(Threads REQUIRED)
    target_link_libraries(PSXRecomp.Native PRIVATE Threads::Threads ${CMAKE_DL_LIBS})
endif()

if(WIN32)
    # `cargo rustc -- --print native-static-libs` for the staticlib reports
    # these system libraries (Issue #492's heap-allocating memory.rs module
    # pulls in more of std than the previous POD-only DMA/Timer/Interrupt
    # modules did, which is what first surfaced this). A `staticlib` archive
    # carries no linker-flag metadata the way `cargo build`'s own rustc
    # invocation would auto-apply for an executable/cdylib, so the consuming
    # CMake target must list them explicitly. This applies to both Windows
    # toolchains this project builds with (MinGW/GCC and MSVC, the CI
    # `native-windows` job's default generator): the required Windows API
    # surface (Winsock, userenv, ntdll, dbghelp) is the same either way, and
    # CMake resolves the plain names to each toolchain's own import library.
    target_link_libraries(PSXRecomp.Native PRIVATE ws2_32 userenv dbghelp ntdll)
endif()

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
    ${PSX_CPU_SOURCES}
    src/psx_memory.cpp
    tests/test_psx_core.cpp
    # Per-slice PSXCpu migration tests (Issue #524); each exposes one runner
    # that test_psx_core.cpp's main() calls.
    tests/test_psx_cpu_decode_rust.cpp
    tests/test_psx_cpu_control_rust.cpp
    tests/test_psx_cpu_memory_access_rust.cpp
    tests/test_psx_cpu_unaligned_rust.cpp
    tests/test_psx_cpu_cop0_rust.cpp
    tests/test_psx_cpu_exception_rust.cpp
    tests/test_psx_cpu_pipeline_rust.cpp
)
target_include_directories(psx_native_tests PRIVATE src include)
target_link_libraries(psx_native_tests PRIVATE PSXRecomp.Native)
# psx_cpu.cpp now routes the hardware-register window to the Rust controllers
# (Issue #386); the shared library's import library does not re-export those
# symbols, so the test executable links the Rust staticlib itself, exactly as
# the shared library does.
add_dependencies(psx_native_tests psx_rust_substrate)
target_link_libraries(psx_native_tests PRIVATE ${PSX_RUST_STATICLIB})
if(UNIX)
    target_link_libraries(psx_native_tests PRIVATE Threads::Threads ${CMAKE_DL_LIBS})
endif()
if(WIN32)
    target_link_libraries(psx_native_tests PRIVATE ws2_32 userenv dbghelp ntdll)
endif()
add_test(NAME psx_native_tests COMMAND psx_native_tests)
