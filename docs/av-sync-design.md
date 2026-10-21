# Audio/video synchronisation and pipeline latency

The phone shows the PC's picture and plays the PC's sound, but they are two
independent pipelines with two independent clocks, so they drift apart. This
is the design for putting them on one clock, and for choosing the trade-off
per mode.

## What is true today

* **Video**: WGC capture -> GPU -> NVENC -> encrypted LAN -> MediaCodec ->
  SurfaceView. The frame header already carries a host timestamp
  (`ptsUs`, from the capture's QPC).
* **Audio**: WASAPI loopback of the phone's endpoint -> PCM16 -> encrypted
  LAN -> AudioTrack. The packets carry a stream id and samples, and nothing
  about *when* they were captured.
* The two paths have different lengths. The video path is the long one
  (capture, encode, decode, composite); the audio path is short. So the
  sound arrives at the phone **before** the picture it belongs to.

**Nothing here is measured yet.** The first job is to measure both paths and
the offset between them, because the fix depends on which path is longer.

## The shape of the fix

1. **One clock.** The host's QPC is the common time base. The video already
   uses it. The audio needs it too: for each captured buffer the host knows
   the WASAPI stream position and the device clock, so it can stamp the
   buffer with the QPC moment it belongs to.
2. **Carry the stamp.** The audio packets do not need a new format. A
   control message ("audio clock": stream id, the host timestamp of the
   first sample, the sample rate) tells the phone where the audio sits on
   the host's clock. The phone pairs it with the stream it belongs to.
3. **The phone measures the offset.** The phone knows when a frame was
   presented (the decoder's PTS) and when a sample was played (the track's
   playback position). The difference is the A/V offset, measured on the
   device that has to correct it.
4. **The phone corrects it.** The video is the master, because the picture
   is what a person reads their lip movements against. The audio is delayed
   to match, by starting the track at the right moment, and the correction
   is repeated slowly so the two clocks cannot drift apart.

## The modes, and why they differ

| Mode | What matters | What the phone does |
| --- | --- | --- |
| **Media** (watching a film, a video call) | the picture and the sound must agree | delay the audio to match the picture exactly. The total latency becomes the video's, which is fine: nothing is interactive. |
| **Gameplay** (a game, anything with input) | the reaction loop must stay short | do not add latency to the sound. The picture is already the long path, so the real fix is a shorter video path; until that exists, the offset is bounded rather than eliminated, and the input path is what must get shorter. |

The honest summary: **mode changes the trade-off, not the mechanism.** One
clock, one measurement, one correction; the mode decides whether the
correction is allowed to cost latency.

## What "minimising the pipeline delay" actually means

The delay a person feels is the sum of every stage, and the video path
dominates. In rough order of what it would cost to improve:

1. **Capture and encode latency on the PC** (the window capture, the GPU
   path and the encoder's own buffering). This is where most of the delay
   probably is, and it is a host-side change with no protocol work.
2. **The transport.** Audio and video share one ordered TCP connection, so a
   large video frame can delay the audio behind it. Audio is prioritised in
   the writer today; a separate lane for media would remove the coupling.
3. **Decode and composite on the phone.** A fixed cost, and the decoder's
   own buffering is part of it.
4. **The audio path** is already short; it only needs to be *aligned*, not
   shortened.

Measuring first is what tells us which of these is worth doing, and the
measurement is the same work the synchronisation needs: a host timestamp on
the audio, and the phone reporting where its picture and its sound are.

## The work, in order

1. **Measure.** Stamp the audio with the host clock; have the phone report
   the offset between the presented picture and the played sound. One
   number, honestly measured, decides everything above.
2. **Align.** Correct the offset on the phone, per the mode.
3. **Shorten.** Attack the video path's delay, and give media its own lane
   so the picture cannot hold the sound back.

## Notes

* The wire format of the audio packets does not change (a stream id and
  PCM16LE); the timing arrives beside it, not inside it.
* The existing priority in the transport writer already prefers audio over
  video. A burst of video can still delay it, which is what the media lane
  in step 3 is for.
* Do not fake a latency figure. A number that is not measured is a claim,
  not a fact.
