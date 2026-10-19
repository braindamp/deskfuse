# Focus Window — Android client

The phone half of a phone-native remote desktop. It does one thing the other 62
apps in this category do not: it makes the PC render at **this screen's real
pixel density**, then shows those pixels **1:1**.

```
1440 x 3120 physical px  ÷  3.5 dpr  =  411 x 891 dp of Windows layout
14 dp body token         ×  3.5      =  49 physical px, captured once, shown once
```

## Build

```bash
cd android
./gradlew :app:assembleDebug          # debug APK
./gradlew :app:testDebugUnitTest      # density contract tests (the P0 gate)
./gradlew :app:assembleRelease        # runs auditTrustPosture automatically
```

Requires JDK 17, Android SDK 35, `minSdk 30`.

## Architecture

```
profile/     PhoneProfile          FR-DPI-01  the only thing the host learns about us
             ProfileProvider       FR-MOB-*   rotation, fold, cutout, refresh

protocol/    Protocol.kt           FR-ENC-05  TargetRecord is versioned with the stream
                                   FR-IN-*    input events in TARGET CLIENT PIXELS

transport/   SecureChannel         FR-TRUST-02 ECDH P-256 + HKDF + AES-GCM
             PairingPayload        FR-TRUST-02 one QR scan, no typed codes
             PairingRepository     FR-TRUST-01 encrypted at rest, local only
             LanTransport          FR-TRUST-03 LAN-first; control + frames, one socket

media/       VideoDecoder          NFR-PIX-01 MediaCodec straight to the Surface
render/      PhoneSurfaceView      NFR-PIX-01 1:1 or letterbox. Never stretch.
input/       InputMapper           FR-IN-01   absolute touch is the DEFAULT

session/     SessionState          FR-OPS-03  every failure is a named state
             SessionController                the state machine
             SessionService        FR-REF-05  survives sleep and network changes

ui/          StateScreens          FR-OPS-03  no black screen without an explanation
             SessionChrome         FR-DPI-08  density readout + modifier bar
```

## The four rules this code will not break

1. **No resample in the native path.** `PhoneSurfaceView` either blits 1:1 or
   letterboxes with `scale = 1.0`. If the host sends a stream that is not this
   phone's size while claiming `native = true`, we show it as `SCALED_FALLBACK`
   and label the session. We never quietly stretch.

2. **No unexplained blank surface.** `SessionState` is a sealed hierarchy and
   `StateScreen` renders every branch. "Black screen on connect" is the
   signature failure of this product category; it is structurally unreachable
   here.

3. **No accessibility service, no ADB, no root.** Asserted at runtime in
   `FocusWindowApp.assertTrustPosture()` and enforced on release builds by the
   `auditTrustPosture` Gradle task. Android 17 / Advanced Protection Mode
   removes AccessibilityService from non-accessibility apps, and every
   incumbent's phone-control story depends on it or on ADB.

4. **No account, no session cap.** The LAN path never asks for an identity, and
   there is no timer anywhere in this source tree.

## Testing the contract

`DensityContractTest` is the executable form of the P0 exit criteria:

- `canvas is the phone's logical size, not its pixel size` → 411 x 891 dp
- `expected dpi is 96 times dpr` → 336 DPI
- `token size proves native beats a pixel-resized desktop` → 14 dp vs 4 dp
- `one to one requires the stream to equal the surface exactly`
- `rotation swaps axes and remaps insets by physical edge`

If the first three fail, the host is not holding up its end and the session
must not be presented as native.
