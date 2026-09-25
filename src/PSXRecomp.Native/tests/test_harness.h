#pragma once

// Shared harness for psx_native_tests (Issue #524). test_psx_core.cpp owns
// main() and the counters; each per-slice test_psx_cpu_*_rust.cpp exposes
// one run_psx_cpu_*_rust_tests() entry point that main() calls once.

#include <cstdint>
#include <cstdio>

extern int tests_run;
extern int tests_passed;

#define TEST(name) \
    do { \
        tests_run++; \
        printf("  TEST: %s ... ", name); \
    } while(0)

#define PASS() \
    do { \
        tests_passed++; \
        printf("PASS\n"); \
    } while(0)

#define ASSERT_EQ(a, b) \
    do { \
        if ((a) != (b)) { \
            printf("FAIL (expected %u, got %u)\n", (unsigned)(b), (unsigned)(a)); \
            return; \
        } \
    } while(0)

// Asserts the common post-exception state: Excode, EPC, BD=0 and the general
// exception vector (BEV=0), matching test_syscall_exception's shape.
#define ASSERT_EXCEPTION(core, excode, epc) \
    do { \
        ASSERT_EQ((PSXCore_GetCop0(core, 13) & 0x7Cu) >> 2, (uint32_t)(excode)); \
        ASSERT_EQ(PSXCore_GetCop0(core, 14), (uint32_t)(epc)); \
        ASSERT_EQ(PSXCore_GetCop0(core, 13) & 0x80000000u, 0u); \
        ASSERT_EQ(PSXCore_GetPC(core), 0x80000080u); \
    } while(0)

void run_psx_cpu_decode_rust_tests();
void run_psx_cpu_control_rust_tests();
void run_psx_cpu_memory_access_rust_tests();
void run_psx_cpu_unaligned_rust_tests();
void run_psx_cpu_cop0_rust_tests();
void run_psx_cpu_exception_rust_tests();
void run_psx_cpu_pipeline_rust_tests();
