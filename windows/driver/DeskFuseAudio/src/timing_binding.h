#pragma once
#include "timing_abi.h"
// Private, in-driver interfaces: only exact-pair installation can bind topology.
static const GUID IID_IDeskFuseTimingBinding =
    {0xa2f42134,0x9916,0x4906,{0xbc,0x75,0x73,0x06,0x16,0xd3,0x29,0xe1}};
DECLARE_INTERFACE_(IDeskFuseTimingBinding, IUnknown) {
    STDMETHOD_(NTSTATUS, GetTiming)(DeskFuseTimingCapabilities* result) PURE;
    STDMETHOD_(NTSTATUS, BindTiming)(const GUID& source) PURE;
    STDMETHOD_(void, RetireTiming)() PURE;
};
