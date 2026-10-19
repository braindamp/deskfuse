#pragma once
// Minimal Linux stand-in for <windows.h>, ONLY so the pure-logic parts of the driver
// (EDID synthesis, the shared-memory contract) can be compiled and unit-tested without
// a Windows toolchain. It deliberately mimics the real macro behaviour that has bitten
// us before (DEFINE_GUID has no trailing semicolon, exactly like winnt.h).
#include <cstdint>
#include <cstddef>
typedef uint8_t  BYTE;
typedef uint16_t WORD;
typedef uint16_t UINT16;
typedef uint32_t UINT32;
typedef uint64_t UINT64;
typedef uint32_t DWORD;
typedef int32_t  LONG;
typedef int64_t  LONG64;
struct GUID { uint32_t Data1; uint16_t Data2; uint16_t Data3; uint8_t Data4[8]; };
#define DEFINE_GUID(name, l, w1, w2, b1, b2, b3, b4, b5, b6, b7, b8) \
    static const GUID name = { l, w1, w2, { b1, b2, b3, b4, b5, b6, b7, b8 } }
