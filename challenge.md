# Challenge: per-phone Windows playback endpoints (DeskFuse)

You are taking over a hard Windows kernel/driver interoperability problem.
Everything below is measured on the target machine, not recalled.

## The goal

A phone that is enrolled on a Windows PC must appear in the Windows sound
list as **its own playback device**, so that the user can select it and the
PC's audio is sent to that phone.

The design that is already 90% in place:

* **one installed driver package** — `windows/driver/DeskFuseAudio` — that
  publishes a **render endpoint** (a WaveRT/PortCls miniport, derived from
  the Microsoft SysVad sample);
* **one device instance of that driver per enrolled phone**, created at
  runtime by a small elevated helper (`windows/audio/DeskFuse.AudioBroker`),
  so that each phone gets its own driver load, its own PortCls adapter and
  its own endpoint, and so that the phone's identity is a **stable PnP
  instance id** derived from the authenticated enrollment hash
  (`DevicePeerRegistry.Identities(deviceId).Speakers`, a 32 character hex
  string), never an IP address or a random per-connection value;
* the endpoint is **removed** when that phone's connection ends.

The **only** thing missing is the runtime creation of the device instance.
Everything else is done and verified (below).

## Target machine and toolchain

* Windows 10 build 19045 (22H2), `HANGBAK\Arne` is the interactive user,
  `codexlab` is the account the automation runs as. Secure Boot is already
  **off** (`HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State` →
  `UEFISecureBootEnabled = 0`).
* Visual Studio 2022 Community + Windows Driver Kit 10.0.26100.0.
  MSBuild: `C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe`
* .NET 8 SDK 9.0.319.
* The driver is **already installed** as `oem108.inf`
  (`pnputil /enum-drivers` → `Original Name: deskfuseaudio.inf`), signed by
  the WDK test certificate `WDKTestCert codexlab,…` which is trusted in the
  machine Root store. The driver loads and works (verified below).
* Repository: `D:\Coding\DeskFuse\v0.3` (git branch `main`).

### Hard constraints

* **Do not** enable test signing, change Secure Boot, or otherwise alter the
  machine's security posture. The driver is already trusted and installed.
* The **LAN host must stay unelevated** — it refuses to start elevated by
  design ("Run the network host as a normal Windows user…"). The privileged
  part is the broker, and only the broker.
* Do not modify the FocusIdd display driver, and do not write to the system
  driver store by hand; use the broker and the normal SetupAPI calls.
* Do not regress the working parts: the driver, the endpoint, the
  host↔phone audio path, the existing 184 unit tests.

## What is already proven working

1. The driver builds, signs, catalogues and installs, and Windows starts it:
   `pnputil /enum-devices /class MEDIA` shows
   `ROOT\MEDIA\0000`, `Device Description: DeskFuse Phone Speakers`,
   `Status: Started`, `Driver Name: oem108.inf`.
2. That endpoint is a **real render endpoint** in the Windows sound list:
   `Speakers (DeskFuse Phone Speakers)`.
3. It **accepts real application audio and returns it on the loopback** —
   a 1 kHz tone played into it comes back as ~740,000 bytes of genuine
   samples (RMS ≈ 16015), which is the tap the phone route needs. Proof
   command (no elevation needed):

   ```
   out\acceptance\speakers\windows-next\FocusWindow.Agent.exe speakers audioroute
   ```
4. The phone's **speaker session reaches the host** as an authenticated
   accessory peer: host log `Authenticated accessory peer joined: <uuid>`.
   The phone runs `SpeakerController` (an accessory hello with capabilities
   `["speakers"]`, no display) inside `SpeakerService`, a `mediaPlayback`
   foreground service.
5. The host side of the route is complete: `PhoneSpeakerRoute` provisions
   the phone's endpoint, captures **that endpoint only** (explicit endpoint
   id, never the default output), streams the PCM over the existing
   encrypted `KIND_AUDIO` lane, and mirrors the endpoint's volume to the
   phone.
6. 184 unit tests pass (`dotnet test windows/agent/FocusWindow.Tests`).

## The problem

The broker must create a **root-enumerated PnP device instance** of the
installed driver, with a **caller-chosen instance id**, at runtime.

Two APIs were tried. Both fail, with **clean, specific errors** — so the
failure is understood, not mysterious:

### A. `SetupDiCreateDeviceInfo` (setupapi.dll)

* **Creation works** when the device-info set is **class-less** and the
  device name is the **full instance path**:
  `SetupDiCreateDeviceInfoList(NULL, NULL)` +
  `SetupDiCreateDeviceInfo(set, L"Root\\DeskFuseAudio\\deskfuse-speakers-abc123",
  &GUID_DEVCLASS_MEDIA, NULL, NULL, 0, &info)` → creates the device
  `ROOT\MEDIA\DESKFUSE-SPEAKERS-ABC123` (Windows normalises the class
  component to the setup class).
* **Registration fails**: `SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, &info)`
  returns false with `GetLastError() = 0xE000020E` (as signed int
  `-536870386`). `DiInstallDevice` (newdev.dll) on the same element fails
  with `0xE0000203`.

Measured matrix (broker command `matrix`, run elevated):

| device info set | device name | flags | result |
| --- | --- | --- | --- |
| class-less | full instance id | 0 | **created**, registration `0xE000020E` |
| class-bound | full instance id | 0 | create fails `0xE0000201` |
| class-bound | `Root\DeskFuseAudio` | 0 | create fails `0xE0000205` |
| class-less | `*DeskFuseAudio` | `DICD_GENERATE_ID` | create fails `0xE0000201` |

`0xE000020x` codes are SetupAPI-internal (they are not in the SDK headers and
`FormatMessage` has no text for them).

### B. `SwDeviceCreate` (cfgmgr32.dll — verified export present)

Every documented form fails **immediately** (before the callback) with
`HRESULT 0x8007007E` (`ERROR_MOD_NOT_FOUND`):

| enumerator | parent instance | hardware id | flags | result |
| --- | --- | --- | --- | --- |
| `ROOT` | `HTREE\ROOT\0` | `Root\DeskFuseAudio` | Removable\|SilentInstall | `0x8007007E` |
| `ROOT` | `HTREE\ROOT\0` | `Root\DeskFuseAudio` | +`DriverRequired` | `0x8007007E` |
| `ROOT` | `HTREE\ROOT\0` | `Root\DeskFuseAudio` | none, no properties | `0x8007007E` |
| `DeskFuse` | `HTREE\ROOT\0` | `DeskFuse\AudioEndpoint` | +`DriverRequired` | `0x8007007E` |
| `DeskFuse` | `NULL` | `DeskFuse\AudioEndpoint` | +`DriverRequired` | `0x8007007E` |

`SwDeviceCreate` is exported by `C:\Windows\System32\cfgmgr32.dll`
(confirmed with `dumpbin -exports`), so the DLL and entry point are right.
The driver package is installed and matches both hardware ids
(`Root\DeskFuseAudio` and `DeskFuse\AudioEndpoint` are both modelled in the
INF).

### What devcon does differently (the strongest clue)

`devcon.exe` (WDK, `Tools\10.0.26100.0\x64\devcon.exe`) succeeds with the
same INF. Its **imports** (`dumpbin -imports`) are:

```
SetupDiGetINFClassW        SetupDiCreateDeviceInfoList    SetupDiCreateDeviceInfoW
SetupDiSetClassInstallParamsW                                  SetupDiCallClassInstaller
SetupDiGetClassDevsExW
```

Notably it does **not** import `SetupDiSetDeviceRegistryProperty`, and it
does **not** import newdev (`UpdateDriverForPlugAndPlayDevices`,
`DiInstallDevice`). Its flow is therefore:

1. `SetupDiGetINFClassW(inf)` → the class GUID and class name from the INF;
2. `SetupDiCreateDeviceInfoList(<class guid>)` — **or** a class-less list;
3. `SetupDiCreateDeviceInfoW(set, <hardware id>, &classGuid, NULL, 0, &info)`;
4. `SetupDiSetClassInstallParamsW(set, &info, header, size)` — an
   `SP_CLASSINSTALL_HEADER` whose payload is `SP_DISP_INSTALL_PARAMS`
   (device path, install section, driver);
5. `SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, &info)`.

The registry result of a devcon install (for comparison with what my code
produces) is `HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses\...` →
`HardwareID {Root\DeskFuseAudio}`, `Service DeskFuseAudio`,
`Mfg @oem108.inf,%manufacturername%;DeskFuse`,
`DeviceDesc @oem108.inf,%devicename%;DeskFuse Phone Speakers`,
`Driver {4d36e96c-…}\0006`, `DeviceType 29`.

### The API contracts (from the local SDK, so you do not have to rediscover them)

`C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\setupapi.h`:

```c
HDEVINFO SetupDiCreateDeviceInfoListW(LPCWSTR ClassGuid, HWND hwndParent);
BOOL    SetupDiCreateDeviceInfoW(HDEVINFO, LPCWSTR DeviceName, LPCGUID ClassGuid,
                                 LPCWSTR DeviceDescription, HWND hwndParent,
                                 DWORD CreationFlags, PSP_DEVINFO_DATA);
BOOL    SetupDiCallClassInstaller(DI_FUNCTION InstallFunction, HDEVINFO DeviceInfoSet,
                                   PSP_DEVINFO_DATA DeviceInfoData);
BOOL    SetupDiSetDeviceRegistryPropertyW(HDEVINFO, PSP_DEVINFO_DATA DeviceInfoData,
                                          DWORD Property, DWORD PropertyRegDataType,
                                          CONST BYTE *PropertyBuffer, DWORD PropertyBufferSize);
BOOL    SetupDiSetClassInstallParamsW(HDEVINFO, PSP_DEVINFO_DATA, PSP_CLASSINSTALL_HEADER,
                                      DWORD ClassInstallParamsSize);
```

`SP_DEVINFO_DATA` is **40 bytes**: `DWORD cbSize; GUID ClassGuid; DWORD
DevInst; ULONG_PTR Reserved;` — the `GUID` is stored **by value**, and
`cbSize` must be set before the call. (Two bugs here cost hours: a missing
`DWORD CreationFlags` parameter, and `out` instead of `ref` for the struct —
`out` zeroes `cbSize` and Windows answers `ERROR_INVALID_PARAMETER`.)

`SPDRP_*` values: `HARDWAREID 1`, `SERVICE 3`, `MFG 11`, `FRIENDLYNAME 12`,
`DEVICEDESC 13`. `DICD_GENERATE_ID = 0x1`.

`DEVPKEY_Device_FriendlyName` = `{a45c254e-df1c-4efd-8020-67d146a850e0}, 14`.

`SwDeviceCreate` (`um/swdevice.h`):

```c
HRESULT SwDeviceCreate(PCWSTR pszEnumeratorName, PCWSTR pszParentDeviceInstance,
                       const SW_DEVICE_CREATE_INFO *pCreateInfo, ULONG cPropertyCount,
                       const DEVPROPERTY *pProperties, SW_DEVICE_CREATE_CALLBACK pCallback,
                       PVOID pContext, HSWDEVICE *phSwDevice);
```
`SW_DEVICE_CREATE_INFO` is `{ULONG cbSize; PCWSTR pszInstanceId; PCZZWSTR pszzHardwareIds;
PCZZWSTR pszzCompatibleIds; const GUID *pContainerId; ULONG CapabilityFlags; PCWSTR
pszDeviceDescription; PCWSTR pszDeviceLocation; const SECURITY_DESCRIPTOR
*pSecurityDescriptor;}` and `DEVPROPERTY` is `{DEVPROPCOMPKEY CompKey; DEVPROPTYPE Type;
ULONG BufferSize; PVOID Buffer;}` where `DEVPROPCOMPKEY` is `{DEVPROPKEY Key; DEVPROPSTORE
Store; PCWSTR LocaleName;}` — **32 bytes**, not 20.

## What has already been ruled out

* The INF: a devcon install of the same INF works.
* The driver package: installed, trusted, and loaded.
* The endpoint: it appears, is named, and carries real audio.
* The endpoint ↔ device identity: the endpoint's property store contains the
  device instance path (`{1}.ROOT\MEDIA\0000` style), so endpoints can be
  matched to devices by identity, not by name. Proof command:
  `FocusWindow.Agent.exe speakers props`.
* The naming mechanism: the parenthetical in "Speakers (X)" is the **device's
  friendly name** (`DEVPKEY_Device_FriendlyName` of the device instance) —
  proven on this machine with MM-1. The INF deliberately sets **no**
  `FriendlyName` on the filter interfaces, so a device instance can carry a
  per-phone name.
* `SwDeviceCreate` with every documented combination of enumerator, parent,
  hardware id, capability flags and properties.

## What I want solved

Pick whichever of these you can make work; do not do all of them.

1. **Preferred** — a working runtime creation of a root-enumerated device
   instance with a caller-chosen instance id, using the ordinary SetupAPI
   calls, so that the broker can do:

   ```csharp
   SetupDiCreateDeviceInfoList(NULL, NULL);
   SetupDiCreateDeviceInfo(set, L"Root\\DeskFuseAudio\\deskfuse-speakers-<hash>",
                           &GUID_DEVCLASS_MEDIA, NULL, NULL, 0, &info);
   // set SPDRP_HARDWAREID (REG_MULTI_SZ), SPDRP_SERVICE, SPDRP_FRIENDLYNAME
   SetupDiSetClassInstallParams(set, &info, <SP_DISP_INSTALL_PARAMS for
                                device path "Root\DeskFuseAudio",
                                install section "DeskFuseAudio_Device",
                                driver "DeskFuseAudio">, size);
   SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, &info);   // must succeed
   ```

   Then the endpoint appears for that instance, named after the phone.

2. **Also acceptable** — make `SwDeviceCreate` succeed (it gives
   `SWD\<enumerator>\<instance id>` instance ids, which are equally stable
   and are the modern supported API), with the INF modelling the hardware id
   the software device reports.

3. **If SetupAPI and SwDeviceCreate both stay blocked**: the alternative
   architecture — one device publishing **N** endpoints, one per phone, with
   the driver registering a topology+wave pair per phone at runtime
   (`PcRegisterSubdevice` / `PcUnregisterSubdevice`, the documented
   SysVad sideband pattern) and naming each interface
   (`IoSetDeviceInterfacePropertyData` on `DEVPKEY_DeviceInterface_FriendlyName`
   before the interface is published). The vendored sample already contains
   the machinery (`src/common.cpp` has the interface-with-properties helper).
   The broker would then drive the driver over an IOCTL instead of creating
   PnP nodes, and no elevation would be needed at all.

## How to test it yourself

Everything is built and installed. The fastest loop:

```powershell
# 1. build the broker (self contained)
cd D:\Coding\DeskFuse\v0.3\windows\audio\DeskFuse.AudioBroker
dotnet publish -c Release -r win-x64 --self-contained true `
  -o D:\Coding\DeskFuse\v0.3\out\acceptance\speakers\broker-next

# 2. run the creation matrix ELEVATED (it creates and removes devices)
#    (the script kills the old broker, copies broker-next over broker, runs it)
powershell -Command "Start-Process powershell -Verb RunAs -WindowStyle Hidden -ArgumentList
  '-NoProfile','-ExecutionPolicy','Bypass','-File',
  'D:\Coding\DeskFuse\v0.3\out\acceptance\speakers\matrix-run.ps1'"

# 3. read the result
type D:\Coding\DeskFuse\v0.3\out\acceptance\speakers\matrix.log
```

`matrix` is the broker's own test command; it prints one line per creation
form and leaves the device tree clean. Add your new form to
`windows/audio/DeskFuse.AudioBroker/DeviceInstance.cs` → `Matrix()` and
re-run step 2. **You need elevation** (the UAC prompt is the only
interactive step; everything else is automatable).

Other useful commands, none of which need elevation:

```
out\acceptance\speakers\windows-next\FocusWindow.Agent.exe speakers devices
out\acceptance\speakers\windows-next\FocusWindow.Agent.exe speakers props
out\acceptance\speakers\windows-next\FocusWindow.Agent.exe speakers audioroute
out\acceptance\speakers\windows-next\FocusWindow.Agent.exe speakers open --endpoint-id "{0.0.0.00000000}.{…}"
```

and for the device tree:

```
pnputil /enum-devices /class MEDIA
reg query "HKLM\SYSTEM\CurrentControlSet\Enum\ROOT\MEDIA" /s
```

**Success looks like**: one line of the matrix reading
`registered=True`, a new `ROOT\MEDIA\…` node in `pnputil /enum-devices`
whose `FriendlyName` is the phone's name, and a second endpoint in
`speakers devices` showing `Speakers (DeskFuse <phone label>)`.

## Notes and traps that already cost time here

* `SetupDiCallClassInstaller` takes the **install function first**;
  `SetupDiSetDeviceRegistryProperty` takes the **`DeviceInfoData` second**.
  Getting either wrong produces `ERROR_INVALID_HANDLE` or a silent no-op.
* `out` zeroes `SP_DEVINFO_DATA`; use `ref` and set `cbSize` yourself.
* `DEVPROP_TYPE_STRING` values are null-terminated; `REG_MULTI_SZ` needs a
  **double** null.
* `SwDeviceClose` removes the software device; the handle owns its
  lifetime.
* The LAN host refuses to run elevated, and the broker is the only
  privileged process. Keep it that way.
* `Set-Content`/`Out-File` from the elevated scripts write UTF-16; the
  logs are readable either way.
