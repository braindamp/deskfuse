/*++

Copyright (c) Microsoft Corporation All Rights Reserved

Module Name:

    minipairs.h

Abstract:

    DeskFuse Audio endpoint configuration.

    Derived from the Microsoft SysVad sample (audio/sysvad, upstream
    revision 4d4618400525f9b2ce66fa2e267f58a55043be40, MIT licence).
    See ../../README.md for the attribution and the list of changes.

    DeskFuse Audio is a RENDER-ONLY endpoint provider:

      * exactly one endpoint -- the phone speaker render endpoint
      * no capture endpoints at all, therefore no tone generator, no
        microphone, no keyword detector and no sample data files
      * no HDMI, no S/PDIF, no headset, no Bluetooth HFP/A2DP/USB sideband
      * no APO demonstration effect module (the module list is empty)

    The single render endpoint uses the upstream speaker topology: a wave
    RT filter with a software audio engine node and a software loopback
    pin, bridged to a two pin topology filter. The samples that reach the
    bridge are the real samples the Windows audio engine rendered for this
    endpoint; the driver never synthesises audio.

--*/

#ifndef _DESKFUSEAUDIO_MINIPAIRS_H_
#define _DESKFUSEAUDIO_MINIPAIRS_H_

#include "speakertopo.h"
#include "speakertoptable.h"
#include "speakerwavtable.h"

NTSTATUS
CreateMiniportWaveRTSYSVAD
(
    _Out_       PUNKNOWN *,
    _In_        REFCLSID,
    _In_opt_    PUNKNOWN,
    _In_        POOL_FLAGS,
    _In_        PUNKNOWN,
    _In_opt_    PVOID,
    _In_        PENDPOINT_MINIPAIR
);

NTSTATUS
CreateMiniportTopologySYSVAD
(
    _Out_       PUNKNOWN *,
    _In_        REFCLSID,
    _In_opt_    PUNKNOWN,
    _In_        POOL_FLAGS,
    _In_        PUNKNOWN,
    _In_opt_    PVOID,
    _In_        PENDPOINT_MINIPAIR
);

//
// Describe buffer size constraints for WaveRT buffers
//
static struct
{
    KSAUDIO_PACKETSIZE_CONSTRAINTS2 TransportPacketConstraints;
    KSAUDIO_PACKETSIZE_PROCESSINGMODE_CONSTRAINT AdditionalProcessingConstraints[1];
} SysvadWaveRtPacketSizeConstraintsRender =
{
    {
        2 * HNSTIME_PER_MILLISECOND,                // 2 ms minimum processing interval
        FILE_BYTE_ALIGNMENT,                        // 1 byte packet size alignment
        0,                                          // no maximum packet size constraint
        2,                                          // 2 processing constraints follow
        {
            STATIC_AUDIO_SIGNALPROCESSINGMODE_DEFAULT,          // constraint for default processing mode
            128,                                                // 128 samples per processing frame
            0,                                                  // NA hns per processing frame
        },
    },
    {
        {
            STATIC_AUDIO_SIGNALPROCESSINGMODE_MOVIE,            // constraint for movie processing mode
            1024,                                               // 1024 samples per processing frame
            0,                                                  // NA hns per processing frame
        },
    }
};

const SYSVAD_DEVPROPERTY SysvadWaveFilterInterfacePropertiesRender[] =
{
    {
        &DEVPKEY_KsAudio_PacketSize_Constraints2,           // Key
        DEVPROP_TYPE_BINARY,                                // Type
        sizeof(SysvadWaveRtPacketSizeConstraintsRender),    // BufferSize
        &SysvadWaveRtPacketSizeConstraintsRender,           // Buffer
    },
};

//
// Render miniports.
//

/*********************************************************************
* Topology/Wave bridge connection for the phone speaker endpoint   *
*                                                                    *
*              +------+                +------+                      *
*              | Wave |                | Topo |                      *
*              |      |                |      |                      *
* System   --->|0    2|---> Loopback   |      |                      *
*              |      |                |      |                      *
* Offload  --->|1    3|--------------->|0    1|---> Line Out         *
*              |      |                |      |                      *
*              +------+                +------+                      *
*********************************************************************/
static
PHYSICALCONNECTIONTABLE SpeakerTopologyPhysicalConnections[] =
{
    {
        KSPIN_TOPO_WAVEOUT_SOURCE,  // TopologyIn
        KSPIN_WAVE_RENDER_SOURCE,   // WaveOut
        CONNECTIONTYPE_WAVE_OUTPUT
    }
};

//
// The phone speaker endpoint.
//
// NOTE ON FLAGS: the endpoint keeps ENDPOINT_OFFLOAD_SUPPORTED because the
// upstream speaker topology is built around the software audio engine node,
// and the miniport only exposes IMiniportAudioEngineNode when offload is
// declared (see CMiniportWaveRT::GetMiniportInterface). Removing the flag
// switches the miniport to the KSPIN_WAVE_RENDER2_* pin numbering, which
// this wave table does not implement, and the audio engine node would then
// not be created at all. The offload path itself stays inert: no effect
// offload APO is installed and the endpoint publishes no audio module.
//
// The module list is empty on purpose: the upstream "audio module 0" is the
// sample's APO demonstration effect, which DeskFuse does not ship.
//
static
ENDPOINT_MINIPAIR SpeakerMiniports =
{
    eSpeakerDevice,
    L"TopologySpeaker",                                     // must match KSNAME_TopologySpeaker in the inf's [Strings] section
    NULL,                                                   // optional template name
    CreateMiniportTopologySYSVAD,
    &SpeakerTopoMiniportFilterDescriptor,
    0, NULL,                                                // Interface properties
    L"WaveSpeaker",                                         // must match KSNAME_WaveSpeaker in the inf's [Strings] section
    NULL,                                                   // optional template name
    CreateMiniportWaveRTSYSVAD,
    &SpeakerWaveMiniportFilterDescriptor,
    ARRAYSIZE(SysvadWaveFilterInterfacePropertiesRender),   // Interface properties
    SysvadWaveFilterInterfacePropertiesRender,
    SPEAKER_DEVICE_MAX_CHANNELS,
    SpeakerPinDeviceFormatsAndModes,
    SIZEOF_ARRAY(SpeakerPinDeviceFormatsAndModes),
    SpeakerTopologyPhysicalConnections,
    SIZEOF_ARRAY(SpeakerTopologyPhysicalConnections),
    ENDPOINT_OFFLOAD_SUPPORTED,
    NULL,                                                   // audio module settings: none
    0,
    NULL,                                                   // module notification device id: none
};

//=============================================================================
//
// Render miniport pairs.
//
static
PENDPOINT_MINIPAIR  g_RenderEndpoints[] =
{
    &SpeakerMiniports,
};

#define g_cRenderEndpoints  (SIZEOF_ARRAY(g_RenderEndpoints))

//=============================================================================
//
// Capture miniport pairs.
//
// DeskFuse Audio is render only. The placeholder entry keeps the array a
// valid C++ object while the endpoint count stays zero, so the adapter
// never instantiates a capture miniport, a tone generator or a keyword
// detector.
//
#define DESKFUSEAUDIO_CAPTURE_ENDPOINTS 0

static
PENDPOINT_MINIPAIR  g_CaptureEndpoints[] =
{
    NULL,
};

#define g_cCaptureEndpoints (SIZEOF_ARRAY(g_CaptureEndpoints) - 1)

//=============================================================================
//
// Total miniports = # endpoints * 2 (topology + wave).
//
#define g_MaxMiniports  ((g_cRenderEndpoints + g_cCaptureEndpoints) * 2)

#endif // _DESKFUSEAUDIO_MINIPAIRS_H_
