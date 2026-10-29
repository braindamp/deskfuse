# Interim input safety and limitations

The host now retains one input ledger across display-session objects, fences old
local session instances and stops automatic global key/button release on focus
loss or disconnection. An incomplete native injection remains uncertain and
blocks a subsequent session. This does not implement authenticated input wire
generations, physical-input attribution or recipient-isolated Windows input.

While physical-input attribution is unavailable, modifier bits alone do not
authorize synthesized modifier presses and automatic releases. A shortcut is
accepted when its modifier is separately held through an explicit remote key
event; implicit on-screen sticky-modifier shortcuts remain unavailable until a
safe, qualified implementation exists. The phone is told when an input action
is not sent. Viewing, files and other independent capabilities remain available.

If the host reports held or uncertain input, it does not send global key-up or
button-up events. The PC user must deliberately reconcile the local input state
and ensure it is neutral before restarting the host and reconnecting. The current
in-memory ledger does not survive host process exit; do not treat a restart as
proof of neutral hardware. A proper local reconciliation flow and persistent
recovery semantics remain open work.
