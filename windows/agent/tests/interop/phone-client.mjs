#!/usr/bin/env node
// Independent phone-side client used to test the C# host WITHOUT sharing its code.
//
// It re-implements, from the Android sources, exactly what the real client does:
//   SecureChannel.kt  - ECDH P-256, HKDF-SHA256, AES-256-GCM, monotonic nonce counter
//   LanTransport.kt   - [kind][len][sealed] framing, ping -> pong, frame header layout
//   VideoDecoder.kt   - target-version gating (a new version may only begin on a keyframe)
//   Protocol.kt       - required fields / enum spellings kotlinx.serialization would enforce
// using Node's crypto instead of our .NET code, so a misreading of the protocol on the host
// side cannot be mirrored here by accident.
//
// Usage: node phone-client.mjs --host 127.0.0.1 --port 4242 --uri "focuswindow://pair?..."
//          --out DIR [--frames 20] [--keyframe-at 8] [--rotate-at 14] [--inputs]
//          [--timeout-ms 60000] [--wrong-secret] [--dpr 3.5] [--width 1440] [--height 3120]
import net from 'node:net';
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
const argv = process.argv.slice(2);
const opt = (name, dflt) => {
  const i = argv.indexOf('--' + name);
  if (i < 0) return dflt;
  const next = argv[i + 1];
  return next === undefined || next.startsWith('--') ? true : next;
};
const host = opt('host', '127.0.0.1');
const port = Number(opt('port', 0));
const uri = opt('uri', process.env.FOCUS_URI ?? ''); // the URI holds & and %, so tests pass it by env
const untilIdleMs = Number(opt('until-idle-ms', 0));
const protocolVersion = Number(opt('protocol', 3));
const outDir = opt('out', '.');
const wantFrames = Number(opt('frames', 20));
const keyframeAt = Number(opt('keyframe-at', 0));
const rotateAt = Number(opt('rotate-at', 0));
const sendInputs = opt('inputs', false) === true;
const timeoutMs = Number(opt('timeout-ms', 60000));
const wrongSecret = opt('wrong-secret', false) === true;
const dpr = Number(opt('dpr', 3.5));
const width = Number(opt('width', 1440));
const height = Number(opt('height', 3120));
fs.mkdirSync(outDir, { recursive: true });
const report = {
  handshake: {},
  hello: null,
  stages: [],
  targets: [],
  violations: [],
  frames: { total: 0, accepted: 0, keyframes: 0, bytes: 0, staleDropped: 0, perVersion: {} },
  pings: 0,
  inputsSent: 0,
  keyframeRequest: null,
  rotation: null,
  byeSent: false,
  closedByHost: false,
  fatalStage: null,
  authRejected: false,
};
const violation = (msg) => { report.violations.push(msg); };
// ---- QR payload, parsed the way PairingPayload.kt does ---------------------------------------
const url = new URL(uri);
if (url.protocol !== 'focuswindow:' || url.hostname !== 'pair') {
  console.error('not a pairing uri'); process.exit(3);
}
const q = url.searchParams;
const hostSpkiFromQr = Buffer.from(q.get('k') ?? '', 'base64url');
let salt = Buffer.from(q.get('s') ?? '', 'base64url');
if (salt.length < 16) { console.error('pairing salt too weak'); process.exit(3); }
if (wrongSecret) salt = crypto.randomBytes(32); // an attacker who never saw the QR
// ---- byte reader -----------------------------------------------------------------------------
class Reader {
  constructor(sock) {
    this.buf = Buffer.alloc(0);
    this.waiters = [];
    this.ended = false;
    sock.on('data', (d) => { this.buf = Buffer.concat([this.buf, d]); this.pump(); });
    sock.on('end', () => { this.ended = true; this.pump(); });
    sock.on('close', () => { this.ended = true; this.pump(); });
    sock.on('error', () => { this.ended = true; this.pump(); });
  }
  pump() {
    while (this.waiters.length) {
      const w = this.waiters[0];
      if (this.buf.length >= w.n) {
        this.waiters.shift();
        const out = this.buf.subarray(0, w.n);
        this.buf = this.buf.subarray(w.n);
        w.resolve(Buffer.from(out));
      } else if (this.ended) {
        this.waiters.shift();
        w.reject(new Error('eof'));
      } else break;
    }
  }
  read(n) {
    return new Promise((resolve, reject) => { this.waiters.push({ n, resolve, reject }); this.pump(); });
  }
}
const sock = net.createConnection({ host, port });
sock.setNoDelay(true);
await new Promise((res, rej) => { sock.once('connect', res); sock.once('error', rej); });
const reader = new Reader(sock);
// ---- handshake (SecureChannel.handshake) -----------------------------------------------------
const { publicKey, privateKey } = crypto.generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
const mySpki = publicKey.export({ type: 'spki', format: 'der' });
const be32 = (n) => { const b = Buffer.alloc(4); b.writeInt32BE(n); return b; };
sock.write(Buffer.concat([be32(mySpki.length), mySpki]));
const wireLen = (await reader.read(4)).readInt32BE(0);
if (wireLen < 1 || wireLen > 4096) { console.error('bad host key length'); process.exit(4); }
const wireKey = await reader.read(wireLen);
report.handshake.hostKeyMatchesQr = wireKey.equals(hostSpkiFromQr);
if (!report.handshake.hostKeyMatchesQr) {
  fs.writeFileSync(path.join(outDir, 'report.json'), JSON.stringify(report, null, 2));
  console.error('host key mismatch - possible interception; refusing to continue');
  process.exit(5);
}
const hostPub = crypto.createPublicKey({ key: hostSpkiFromQr, format: 'der', type: 'spki' });
const shared = crypto.diffieHellman({ privateKey, publicKey: hostPub }); // raw ECDH secret
const hkdf = (info, len) => Buffer.from(crypto.hkdfSync('sha256', shared, salt, Buffer.from(info), len));
const c2s = hkdf('focuswindow:c2s:v3', 32);
const s2c = hkdf('focuswindow:s2c:v3', 32);
const confirm = hkdf('focuswindow:confirm:v3', 4);
report.handshake.code = String((confirm.readInt32BE(0) & 0x7fffffff) % 1000000).padStart(6, '0');
let sendCounter = 0n;
let recvCounter = -1n;
const nonceFor = (counter) => { const n = Buffer.alloc(12); n.writeBigUInt64BE(counter, 4); return n; };
function seal(plain) {
  const counter = sendCounter++;
  const cipher = crypto.createCipheriv('aes-256-gcm', c2s, nonceFor(counter), { authTagLength: 16 });
  const ct = Buffer.concat([cipher.update(plain), cipher.final()]);
  const head = Buffer.alloc(8); head.writeBigUInt64BE(counter);
  return Buffer.concat([head, ct, cipher.getAuthTag()]);
}
function open(packet) {
  if (packet.length <= 8) throw new Error('ciphertext too short');
  const counter = packet.readBigUInt64BE(0);
  if (counter <= recvCounter) throw new Error(`replayed or reordered frame: ${counter} <= ${recvCounter}`);
  const decipher = crypto.createDecipheriv('aes-256-gcm', s2c, nonceFor(counter), { authTagLength: 16 });
  decipher.setAuthTag(packet.subarray(packet.length - 16));
  const plain = Buffer.concat([decipher.update(packet.subarray(8, packet.length - 16)), decipher.final()]);
  recvCounter = counter; // only advanced after authentication, as in the Kotlin code
  return plain;
}
const KIND_CONTROL = 1, KIND_FRAME = 2, KIND_PING = 3, KIND_PONG = 4;
function sendControl(obj) {
  const sealed = seal(Buffer.from(JSON.stringify(obj)));
  sock.write(Buffer.concat([Buffer.from([KIND_CONTROL]), be32(sealed.length), sealed]));
}
// ---- what kotlinx.serialization would reject (Protocol.kt) -----------------------------------
const OCCUPANTS = ['canvas', 'clone', 'window', 'session'];
const METHODS = ['already_correct', 'iddscale', 'permonitor_override', 'fallback_scaled'];
const STATES = ['shared_view', 'coauthored', 'read_only_clone', 'save_back'];
const isInt = (v) => Number.isInteger(v);
function validateServerMessage(m) {
  const need = (cond, what) => { if (!cond) violation(`${m.type}: ${what}`); };
  switch (m.type) {
    case 'hello':
      need(isInt(m.protocolVersion), 'protocolVersion must be an int');
      need(typeof m.hostVersion === 'string' && typeof m.hostName === 'string' && typeof m.channel === 'string', 'hostVersion/hostName/channel required');
      break;
    case 'target': {
      const r = m.record ?? {};
      for (const k of ['version', 'widthPx', 'heightPx', 'dpi', 'canvasDpW', 'canvasDpH', 'refreshHz']) need(isInt(r[k]), `record.${k} must be an int`);
      for (const k of ['codec', 'pixelFormat', 'appLabel']) need(typeof r[k] === 'string', `record.${k} must be a string`);
      need(typeof r.native === 'boolean', 'record.native must be a boolean');
      need(OCCUPANTS.includes(r.occupant), `record.occupant '${r.occupant}' is not a client enum value`);
      need(METHODS.includes(r.densityMethod), `record.densityMethod '${r.densityMethod}' is not a client enum value`);
      need(STATES.includes(r.stateModel), `record.stateModel '${r.stateModel}' is not a client enum value`);
      need(r.degradedReason === undefined || r.degradedReason === null || typeof r.degradedReason === 'string', 'record.degradedReason must be string|null');
      break;
    }
    case 'stage':
      need(typeof m.stage === 'string' && typeof m.detail === 'string', 'stage/detail required');
      need(m.fatal === undefined || typeof m.fatal === 'boolean', 'fatal must be boolean');
      break;
    case 'quality':
      need(typeof m.codec === 'string' && typeof m.pixelFormat === 'string' && typeof m.reason === 'string', 'codec/pixelFormat/reason required');
      need(isInt(m.bitrateKbps) && isInt(m.refreshHz), 'bitrateKbps/refreshHz must be ints');
      break;
    case 'invariant':
      need(typeof m.clean === 'boolean' && Array.isArray(m.drift), 'clean:boolean and drift:[] required');
      break;
    case 'conflict': case 'clipboard':
      break;
    default:
      violation(`unknown server message type '${m.type}' (kotlinx would throw)`);
  }
}
// ---- state shared by the read loop and the scenario ------------------------------------------
let accepted = -1;            // VideoDecoder.acceptedTargetVersion
let lastPts = -1n;
const knownVersions = new Set();
const streams = new Map();
let currentVersion = 0;
let framesInCurrentVersion = 0;
let done = false;
let keyframeRequestedAt = null;
let rotated = false;
let inputsDone = false;
let lastFrameAt = 0;
let firstFrameAt = 0;
function streamFor(version) {
  if (!streams.has(version)) streams.set(version, fs.createWriteStream(path.join(outDir, `stream-v${version}.h264`)));
  return streams.get(version);
}
function onFrame(plain) {
  if (plain.length < 20) { violation('truncated frame header'); return; }
  const magic = plain.readUInt16BE(0);
  const flags = plain.readUInt16BE(2);
  const version = plain.readUInt32BE(4);
  const pts = plain.readBigUInt64BE(8);
  const len = plain.readUInt32BE(16);
  if (magic !== 0x4657) { violation(`bad frame magic 0x${magic.toString(16)}`); return; }
  if (20 + len > plain.length) { violation('frame length mismatch'); return; }
  const keyframe = (flags & 1) !== 0;
  const payload = plain.subarray(20, 20 + len);
  report.frames.total++;
  lastFrameAt = Date.now();
  if (!firstFrameAt) firstFrameAt = lastFrameAt;
  if (!knownVersions.has(version)) violation(`frame of version ${version} arrived before its Target message`);
  if (pts <= lastPts) violation(`pts not strictly increasing: ${pts} after ${lastPts}`);
  lastPts = pts;
  // VideoDecoder.submit(): stale -> drop; a new version may only be opened by a keyframe.
  if (version < accepted) { report.frames.staleDropped++; return; }
  if (version > accepted) {
    if (!keyframe) { report.frames.staleDropped++; violation(`version ${version} did not open with a keyframe`); return; }
    accepted = version;
  }
  report.frames.accepted++;
  report.frames.bytes += payload.length;
  if (keyframe) report.frames.keyframes++;
  const pv = (report.frames.perVersion[version] ??= { frames: 0, keyframes: 0, bytes: 0, firstIsKeyframe: keyframe });
  pv.frames++; pv.bytes += payload.length; if (keyframe) pv.keyframes++;
  streamFor(version).write(payload);
  currentVersion = version;
  framesInCurrentVersion++;
  if (keyframeRequestedAt !== null && keyframe && version === keyframeRequestedAt.version && report.frames.accepted > keyframeRequestedAt.atFrame) {
    report.keyframeRequest.satisfied = true;
    report.keyframeRequest.satisfiedAtFrame = report.frames.accepted;
    keyframeRequestedAt = null;
  }
}
function onControl(plain) {
  let m;
  try { m = JSON.parse(plain.toString('utf8')); } catch { violation('control message is not JSON'); return; }
  validateServerMessage(m);
  if (m.type === 'hello') report.hello = m;
  else if (m.type === 'stage') {
    report.stages.push({ stage: m.stage, detail: m.detail, fatal: !!m.fatal });
    if (m.fatal) report.fatalStage = { stage: m.stage, detail: m.detail, remediation: m.remediation ?? null };
  } else if (m.type === 'target') {
    report.targets.push(m.record);
    knownVersions.add(m.record.version);
    framesInCurrentVersion = 0;
  }
}
async function readLoop() {
  try {
    while (!done) {
      const kind = (await reader.read(1))[0];
      if (kind === KIND_CONTROL || kind === KIND_FRAME) {
        const len = (await reader.read(4)).readInt32BE(0);
        if (len < 1 || len > (kind === KIND_CONTROL ? 1 << 20 : 1 << 24)) { violation(`length out of range: ${len}`); break; }
        const sealed = await reader.read(len);
        let plain;
        try { plain = open(sealed); } catch (e) { violation('decrypt failed: ' + e.message); break; }
        if (kind === KIND_CONTROL) onControl(plain); else onFrame(plain);
      } else if (kind === KIND_PING) {
        report.pings++;
        sock.write(Buffer.from([KIND_PONG])); // LanTransport replies to every ping
      } else { violation(`unknown packet kind ${kind}`); break; }
    }
  } catch (e) {
    if (!done) report.closedByHost = true;
  }
}
const profile = (w, h, orientation) => ({
  version: 1, deviceId: 'node-test-device', deviceLabel: 'Node Test Phone',
  physicalW: w, physicalH: h, dpr, safeTop: 0, safeBottom: 0, safeLeft: 0, safeRight: 0,
  fontScale: 1.0, maxRefreshHz: 120, diagonalMm: 170, orientation, formFactor: 'PHONE',
});
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
async function waitFor(cond, ms, label) {
  const end = Date.now() + ms;
  while (Date.now() < end) { if (cond()) return true; if (report.closedByHost) return false; await sleep(20); }
  violation(`timed out waiting for ${label}`);
  return false;
}
// ---- go --------------------------------------------------------------------------------------
const startedAt = Date.now();
const loop = readLoop();
const watchdog = setTimeout(() => { violation('overall timeout'); done = true; sock.destroy(); }, timeoutMs);
sendControl({ type: 'hello', protocolVersion, clientVersion: 'node-test', profile: profile(width, height, 'PORTRAIT') });
sendControl({ type: 'density', preset: 'logical' });
sendControl({ type: 'refresh', hz: 120 });
if (wrongSecret) {
  // An attacker without the pairing secret: the host must drop us without ever answering.
  await sleep(1500);
  report.authRejected = report.closedByHost || report.hello === null;
} else {
  await waitFor(() => report.targets.length >= 1 || report.fatalStage, 30000, 'the first Target (or a fatal Stage)');
  if (!report.fatalStage) {
    await waitFor(() => report.frames.accepted >= 1, 30000, 'the first frame');
    if (sendInputs) {
      const ev = (e) => { sendControl({ type: 'input', event: e }); report.inputsSent++; };
      ev({ type: 'pointer', x: 100, y: 200, action: 'down', button: 'left', pressure: 0.0, tiltX: 0.0, tiltY: 0.0, isStylus: false });
      ev({ type: 'pointer', x: 110, y: 210, action: 'move', button: 'left', pressure: 0.0, tiltX: 0.0, tiltY: 0.0, isStylus: false });
      ev({ type: 'pointer', x: 110, y: 210, action: 'up', button: 'left', pressure: 0.0, tiltX: 0.0, tiltY: 0.0, isStylus: false });
      ev({ type: 'pointer', x: 300, y: 400, action: 'down', button: 'right', pressure: 0.6, tiltX: 12.5, tiltY: -3.0, isStylus: true });
      ev({ type: 'scroll', x: 300, y: 400, dx: 0, dy: -120 });
      ev({ type: 'key', scancode: 30, keyCode: 29, down: true, modifiers: 1, repeat: 0 });
      ev({ type: 'key', scancode: 30, keyCode: 29, down: false, modifiers: 1, repeat: 0 });
      ev({ type: 'text', text: 'héllo' });
      inputsDone = true;
    }
    if (keyframeAt > 0) {
      await waitFor(() => report.frames.accepted >= keyframeAt, 30000, `${keyframeAt} frames before the keyframe request`);
      keyframeRequestedAt = { version: currentVersion, atFrame: report.frames.accepted };
      report.keyframeRequest = { sentAtFrame: report.frames.accepted, version: currentVersion, satisfied: false };
      sendControl({ type: 'keyframe' });
      await waitFor(() => report.keyframeRequest.satisfied, 15000, 'a keyframe after the request');
    }
    if (rotateAt > 0) {
      await waitFor(() => report.frames.accepted >= rotateAt, 30000, `${rotateAt} frames before rotating`);
      const before = report.targets.length;
      sendControl({ type: 'profile', profile: profile(height, width, 'LANDSCAPE') });
      await waitFor(() => report.targets.length > before, 30000, 'a new Target after rotation');
      const newVersion = report.targets[report.targets.length - 1].version;
      const base = report.frames.perVersion[newVersion]?.frames ?? 0;
      await waitFor(() => (report.frames.perVersion[newVersion]?.frames ?? 0) >= base + 3, 30000, 'frames of the rotated target');
      rotated = true;
      report.rotation = { newVersion, target: report.targets[report.targets.length - 1] };
    }
    await waitFor(() => report.frames.accepted >= wantFrames, 30000, `${wantFrames} frames`);
    // Stay connected until the stream has been SILENT for a while: the final picture must have
    // arrived by then (idle flush), and a quiet desktop must still produce host pings.
    if (untilIdleMs > 0) {
      while (!report.closedByHost && Date.now() - lastFrameAt < untilIdleMs) await sleep(50);
    }
    if (opt('linger-ms', 0) > 0) await sleep(Number(opt('linger-ms', 0)));
  }
  try { sendControl({ type: 'bye', reason: 'node test finished' }); report.byeSent = true; } catch { /* closed */ }
  await sleep(300);
}
report.durationMs = Date.now() - startedAt;
// Same-machine timing aids (the tests measure publish->receive latency against these).
report.startedAtEpochMs = startedAt;
report.firstFrameMs = firstFrameAt ? firstFrameAt - startedAt : null;
report.lastFrameMs = lastFrameAt ? lastFrameAt - startedAt : null;
done = true;
clearTimeout(watchdog);
sock.end();
sock.destroy();
await Promise.race([loop, sleep(500)]);
for (const s of streams.values()) s.end();
await sleep(100);
fs.writeFileSync(path.join(outDir, 'report.json'), JSON.stringify(report, null, 2));
console.log(JSON.stringify({ ok: report.violations.length === 0, violations: report.violations, frames: report.frames.accepted, targets: report.targets.length }));
process.exit(0);
