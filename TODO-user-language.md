# TODO: user-facing language (hide the mechanism)

The phone is a product for a person, not for the implementer. Right now the
host sends internal wording straight to the phone, and the phone shows it.
Every string below is either already visible to the user or one keystroke
away from being visible.

**The rule**

1. Say what the user was trying to do, not how it is built.
2. Say whether trying again can help, and who has to act if it cannot.
3. One short sentence in the headline. The technical detail goes to the host
   log and, on the phone, behind a "details" tap.
4. Never show the user a name they could not have chosen: driver names,
   device instance ids, GUIDs, INF names, Win32 error text, or the names of
   our own components.

**Open decisions (owner)**

* Headline wording for a display-side failure:
  * "Your PC could not open a window for this phone."  (plain, intent first)
  * "Your PC is not ready to show a window on this phone."  (says "not ready")
* Speaker-side failure wording: "Your PC could not set up a speaker for this
  phone", or its own separate wording?

**Already leaking to the user**

| Where | Now | Should be |
| --- | --- | --- |
| host `Preparing the display` | "Creating the phone-shaped monitor." | "Getting your PC ready to show a window on this phone." |
| host display errors | "FocusIdd already has a display that this host does not own." | "Your PC could not open a window for this phone." |
| host display errors | "Another DeskFuse process owns virtual-display control." | "Another DeskFuse session is using this PC. Close it and try again." |
| host display errors | "An unowned virtual display is active." | same as above |
| host display errors | "The negotiated virtual display disappeared before capture." | "Your PC stopped showing the window. Try again." |
| host display errors | "The phone monitor disappeared while its density was being read." | "Your PC could not open a window for this phone." |
| host display errors | "Windows' display topology kept changing while it was being read." | "Your PC's screens changed while it was setting up. Try again." |
| host display errors | "Windows did not confirm virtual monitor departure." | "Your PC could not close the previous window cleanly. Try again." |
| host preflight | "Installed FocusIdd is absent or protocol does not match." | "This PC needs a newer version of DeskFuse." |
| host driver errors | "Driver IOCTL 0x… failed: … (Win32 …)" | "Your PC could not talk to its display component. Restart DeskFuse there." |
| host driver errors | "Could not enumerate devices: …", "Could not read the driver's device path: …", "Empty device path." | "Your PC could not open a window for this phone." |
| host gpu errors | "The native WGC/NVENC pipeline has not delivered a frame.", "DeskFuse native GPU ABI mismatch." | "Your PC could not prepare the picture for this phone." |
| host density errors | "Verified native DPI is required for the GPU workspace.", "GetDpiForMonitor failed for the phone monitor." | "Your PC could not prepare the picture for this phone." |
| host speaker error | "The phone speaker endpoint appeared but Windows would not open it." | "Your PC could not set up a speaker for this phone." |
| app | "Preparing your display" | "Getting your PC ready." |
| app | "The PC has not created a phone speaker endpoint yet." | "Your PC has not set up a speaker for this phone yet." |
| app | "This device reports an unusual display configuration." | keep, but say what to do: "This phone's screen settings are unusual. DeskFuse may not show a picture correctly." |
| app | "missing or implausible Android display density: $dpr" | never shown; it is a log line |

**Structural changes (do these with the wording)**

1. **A failed display session must not block the speaker.** The failure
   screen keeps a "Use this phone as a speaker" action, so the phone is still
   useful when the display side is broken.
2. **The headline is one sentence; the detail is one tap away.** The app
   already has a diagnostics panel: host-internal text belongs there and in
   the host log, never in the headline.
3. **The host should send a user-facing message and a technical detail
   separately**, instead of one string that serves both. The phone shows the
   first, the diagnostics show the second. This is the structural change
   that stops this recurring: one `DisplayException` detail today is
   simultaneously a log line and a user-facing sentence.

**Done in this slice**

* The phone speaker route's own errors are already plain
  ("Your PC could not set up a speaker for this phone" territory); the
  remaining leak is the host's "appeared but Windows would not open it"
  wording, listed above.
