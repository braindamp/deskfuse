/*++

Copyright (c) Microsoft Corporation All Rights Reserved

Module Name:

    speakerwavtable.h

Abstract:

    Shared-mode phone renderer. Windows mixes application audio and supplies
    WASAPI software loopback; this filter advertises no hardware loopback or
    offload audio engine.

--*/

#ifndef _SYSVAD_SPEAKERWAVTABLE_H_
#define _SYSVAD_SPEAKERWAVTABLE_H_

#define SPEAKER_DEVICE_MAX_CHANNELS                 2
#define SPEAKER_HOST_MAX_CHANNELS                   2
#define SPEAKER_HOST_MIN_BITS_PER_SAMPLE            16
#define SPEAKER_HOST_MAX_BITS_PER_SAMPLE            16
#define SPEAKER_HOST_MIN_SAMPLE_RATE                24000
#define SPEAKER_HOST_MAX_SAMPLE_RATE                96000

// Keep the existing capacity for concurrent Windows processing modes.
#define SPEAKER_MAX_INPUT_SYSTEM_STREAMS            6

static 
KSDATAFORMAT_WAVEFORMATEXTENSIBLE SpeakerHostPinSupportedDeviceFormats[] =
{
    { // 0
        {
            sizeof(KSDATAFORMAT_WAVEFORMATEXTENSIBLE),
            0,
            0,
            0,
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        {
            {
                WAVE_FORMAT_EXTENSIBLE,
                2,
                24000,
                96000,
                4,
                16,
                sizeof(WAVEFORMATEXTENSIBLE)-sizeof(WAVEFORMATEX)
            },
            16,
            KSAUDIO_SPEAKER_STEREO,
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM)
        }
    },
    { // 1
        {
            sizeof(KSDATAFORMAT_WAVEFORMATEXTENSIBLE),
            0,
            0,
            0,
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        {
            {
                WAVE_FORMAT_EXTENSIBLE,
                2,
                32000,
                128000,
                4,
                16,
                sizeof(WAVEFORMATEXTENSIBLE)-sizeof(WAVEFORMATEX)
            },
            16,
            KSAUDIO_SPEAKER_STEREO,
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM)
        }
    },
    { // 2
        {
            sizeof(KSDATAFORMAT_WAVEFORMATEXTENSIBLE),
            0,
            0,
            0,
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        {
            {
                WAVE_FORMAT_EXTENSIBLE,
                2,
                44100,
                176400,
                4,
                16,
                sizeof(WAVEFORMATEXTENSIBLE)-sizeof(WAVEFORMATEX)
            },
            16,
            KSAUDIO_SPEAKER_STEREO,
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM)
        }
    },
    { // 3
        {
            sizeof(KSDATAFORMAT_WAVEFORMATEXTENSIBLE),
            0,
            0,
            0,
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        {
            {
                WAVE_FORMAT_EXTENSIBLE,
                2,
                48000,
                192000,
                4,
                16,
                sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX)
            },
            16,
            KSAUDIO_SPEAKER_STEREO,
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM)
        }
    },
    { // 4
        {
            sizeof(KSDATAFORMAT_WAVEFORMATEXTENSIBLE),
            0,
            0,
            0,
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        {
            {
                WAVE_FORMAT_EXTENSIBLE,
                2,
                88200,
                352800,
                4,
                16,
                sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX)
            },
            16,
            KSAUDIO_SPEAKER_STEREO,
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM)
        }
    },
    { // 5
        {
            sizeof(KSDATAFORMAT_WAVEFORMATEXTENSIBLE),
            0,
            0,
            0,
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        {
            {
                WAVE_FORMAT_EXTENSIBLE,
                2,
                96000,
                384000,
                4,
                16,
                sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX)
            },
            16,
            KSAUDIO_SPEAKER_STEREO,
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM)
        }
    }
};

static
MODE_AND_DEFAULT_FORMAT SpeakerHostPinSupportedDeviceModes[] =
{
    {
        STATIC_AUDIO_SIGNALPROCESSINGMODE_RAW,
        &SpeakerHostPinSupportedDeviceFormats[3].DataFormat  // 48KHz
    },
    {
        STATIC_AUDIO_SIGNALPROCESSINGMODE_DEFAULT,
        &SpeakerHostPinSupportedDeviceFormats[3].DataFormat  // 48KHz
    },
    {
        STATIC_AUDIO_SIGNALPROCESSINGMODE_MEDIA,
        &SpeakerHostPinSupportedDeviceFormats[3].DataFormat  // 48KHz
    },
    {
        STATIC_AUDIO_SIGNALPROCESSINGMODE_MOVIE,
        &SpeakerHostPinSupportedDeviceFormats[3].DataFormat  // 48KHz
    },
    {
        STATIC_AUDIO_SIGNALPROCESSINGMODE_COMMUNICATIONS,
        &SpeakerHostPinSupportedDeviceFormats[0].DataFormat  // 24KHz
    },
    {
        STATIC_AUDIO_SIGNALPROCESSINGMODE_NOTIFICATION,
        &SpeakerHostPinSupportedDeviceFormats[3].DataFormat  // 48KHz
    }
};

// Pin types and format entries follow the two-pin filter descriptor exactly.
static
PIN_DEVICE_FORMATS_AND_MODES SpeakerPinDeviceFormatsAndModes[] =
{
    {
        SystemRenderPin,
        SpeakerHostPinSupportedDeviceFormats,
        SIZEOF_ARRAY(SpeakerHostPinSupportedDeviceFormats),
        SpeakerHostPinSupportedDeviceModes,
        SIZEOF_ARRAY(SpeakerHostPinSupportedDeviceModes)
    },
    {
        BridgePin,
        NULL,
        0,
        NULL,
        0
    }
};

static
KSDATARANGE_AUDIO SpeakerPinDataRangesStream[] =
{
    {
        {
            sizeof(KSDATARANGE_AUDIO),
            KSDATARANGE_ATTRIBUTES,
            0,
            0,
            STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
            STATICGUIDOF(KSDATAFORMAT_SUBTYPE_PCM),
            STATICGUIDOF(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX)
        },
        SPEAKER_HOST_MAX_CHANNELS,
        SPEAKER_HOST_MIN_BITS_PER_SAMPLE,
        SPEAKER_HOST_MAX_BITS_PER_SAMPLE,
        SPEAKER_HOST_MIN_SAMPLE_RATE,
        SPEAKER_HOST_MAX_SAMPLE_RATE
    }
};

static
PKSDATARANGE SpeakerPinDataRangePointersStream[] =
{
    PKSDATARANGE(&SpeakerPinDataRangesStream[0]),
    PKSDATARANGE(&PinDataRangeAttributeList)
};

static
KSDATARANGE SpeakerPinDataRangesBridge[] =
{
    {
        sizeof(KSDATARANGE),
        0,
        0,
        0,
        STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
        STATICGUIDOF(KSDATAFORMAT_SUBTYPE_ANALOG),
        STATICGUIDOF(KSDATAFORMAT_SPECIFIER_NONE)
    }
};

static
PKSDATARANGE SpeakerPinDataRangePointersBridge[] =
{
    &SpeakerPinDataRangesBridge[0]
};

static
PCPIN_DESCRIPTOR SpeakerWaveMiniportPins[] =
{
    // KSPIN_WAVE_RENDER3_SINK_SYSTEM: Windows shared audio engine input.
    {
        SPEAKER_MAX_INPUT_SYSTEM_STREAMS,
        SPEAKER_MAX_INPUT_SYSTEM_STREAMS,
        0,
        NULL,
        {
            0,
            NULL,
            0,
            NULL,
            SIZEOF_ARRAY(SpeakerPinDataRangePointersStream),
            SpeakerPinDataRangePointersStream,
            KSPIN_DATAFLOW_IN,
            KSPIN_COMMUNICATION_SINK,
            &KSCATEGORY_AUDIO,
            NULL,
            0
        }
    },
    // KSPIN_WAVE_RENDER3_SOURCE: physical connection to speaker topology.
    {
        0,
        0,
        0,
        NULL,
        {
            0,
            NULL,
            0,
            NULL,
            SIZEOF_ARRAY(SpeakerPinDataRangePointersBridge),
            SpeakerPinDataRangePointersBridge,
            KSPIN_DATAFLOW_OUT,
            KSPIN_COMMUNICATION_NONE,
            &KSCATEGORY_AUDIO,
            NULL,
            0
        }
    }
};

static
PCCONNECTION_DESCRIPTOR SpeakerWaveMiniportConnections[] =
{
    {
        PCFILTER_NODE, KSPIN_WAVE_RENDER3_SINK_SYSTEM,
        PCFILTER_NODE, KSPIN_WAVE_RENDER3_SOURCE
    }
};

static
PCPROPERTY_ITEM PropertiesSpeakerWaveFilter[] =
{
    {
        &KSPROPSETID_Pin,
        KSPROPERTY_PIN_PROPOSEDATAFORMAT,
        KSPROPERTY_TYPE_SET | KSPROPERTY_TYPE_BASICSUPPORT,
        PropertyHandler_WaveFilter
    },
    {
        &KSPROPSETID_Pin,
        KSPROPERTY_PIN_PROPOSEDATAFORMAT2,
        KSPROPERTY_TYPE_GET | KSPROPERTY_TYPE_BASICSUPPORT,
        PropertyHandler_WaveFilter
    }
};

DEFINE_PCAUTOMATION_TABLE_PROP(AutomationSpeakerWaveFilter, PropertiesSpeakerWaveFilter);

static
PCFILTER_DESCRIPTOR SpeakerWaveMiniportFilterDescriptor =
{
    0,
    &AutomationSpeakerWaveFilter,
    sizeof(PCPIN_DESCRIPTOR),
    SIZEOF_ARRAY(SpeakerWaveMiniportPins),
    SpeakerWaveMiniportPins,
    sizeof(PCNODE_DESCRIPTOR),
    0,
    NULL,
    SIZEOF_ARRAY(SpeakerWaveMiniportConnections),
    SpeakerWaveMiniportConnections,
    0,
    NULL
};

#endif // _SYSVAD_SPEAKERWAVTABLE_H_

