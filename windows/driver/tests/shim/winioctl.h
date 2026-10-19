#pragma once
// Values and macro copied from the Windows SDK's winioctl.h / devioctl.h.
#define METHOD_BUFFERED   0
#define FILE_ANY_ACCESS   0
#define FILE_READ_ACCESS  0x0001
#define FILE_WRITE_ACCESS 0x0002
#define CTL_CODE(DeviceType, Function, Method, Access) \
    (((DeviceType) << 16) | ((Access) << 14) | ((Function) << 2) | (Method))
