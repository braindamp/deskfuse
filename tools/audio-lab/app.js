/* DESKFUSE AUDIO LAB — self-contained interactive simulation.
 * All settings are front-end mock parameters. No sockets, storage, audio
 * devices, WebAudio, permissions or production endpoints are accessed.
 */
'use strict';

const $ = (selector, parent = document) => parent.querySelector(selector);
const $$ = (selector, parent = document) => [...parent.querySelectorAll(selector)];
const clamp = (n, lo, hi) => Math.min(hi, Math.max(lo, n));
const round1 = n => Math.round(n * 10) / 10;

// Only the values itemized in listeningReference are reported from the
// external fine-adjustment session. Other initialized values are *lab model*
// choices and are NOT promoted to tested defaults.
const initial = {
  prebuffer: 30, targetQueue: 30, hardwarePolicy: 'platform',
  nativeReserveFrames: 1440, adaptiveBuffer: 'off',
  resampler: 'sinc128', driftPpm: 400, maxSlewPpm: 100,
  responseSpeed: 2, packetAge: 60,
  packetMs: '5', concealMs: 10, fadeMs: 2, outputMode: 'shared',
  audioUsage: 'unspecified', recoveryMode: 'balanced',
  outputRoute: 'speaker', captureSource: 'loopback', capturePeriod: 'default',
  nativeQueue: '8', rustQueue: '8', handoff: 'pipe', wakeStrategy: 'per-block',
  networkScenario: 'good', jitterMs: 4, lossPct: 0.2, freshness: 'strict',
  egressMode: 'paced', telemetryDetail: 'normal', controllerStrategy: 'output',
  clockPrecision: 'source-qpc', testMode: 'none'
};

const listeningReference = {
  source: 'MM-1', pcm: '48 kHz / 2 channels / PCM16', packetFrames: 240,
  packetMs: '5', outputMode: 'shared', performanceRequest: 'low_latency',
  hardwarePolicy: 'platform', platformBufferRequestFrames: 0,
  reportedActualBufferFrames: 4104, nativeReserveFrames: 1440,
  resampler: 'sinc128', sincTaps: 128, driftPpm: 400, maxSlewPpm: 100,
  fadeMs: 2, fadeFrames: 96, concealMs: 10, concealFrames: 480
};
// Reported source, formats, burst-size observations and derived capacity
// are reference *metadata*, not freely configurable renderer fields.
const referenceKeys = ['packetMs','outputMode','hardwarePolicy',
  'nativeReserveFrames','resampler','driftPpm','maxSlewPpm','fadeMs','concealMs'];
const presetData = {
  reference: { ...initial },
  ultra: { ...initial, prebuffer: 10, targetQueue: 10, nativeReserveFrames: 480,
    hardwarePolicy: 'bursts2', adaptiveBuffer: 'off', packetAge: 35, concealMs: 0,
    fadeMs: 1, outputMode: 'auto', audioUsage: 'game', responseSpeed: 3,
    recoveryMode: 'fast' },
  safe: { ...initial, prebuffer: 15, targetQueue: 15, nativeReserveFrames: 720,
    hardwarePolicy: 'bursts3', adaptiveBuffer: 'low', packetAge: 45,
    concealMs: 5, outputMode: 'auto', audioUsage: 'game' },
  balanced: { ...initial, prebuffer: 20, targetQueue: 20, nativeReserveFrames: 960,
    hardwarePolicy: 'bursts3', adaptiveBuffer: 'low', packetAge: 55 },
  quality: { ...initial, prebuffer: 35, targetQueue: 35, nativeReserveFrames: 1680,
    hardwarePolicy: 'bursts6', adaptiveBuffer: 'high', concealMs: 10,
    responseSpeed: 1, packetAge: 60, fadeMs: 3, recoveryMode: 'conservative' }
};

let state = { ...initial };
let currentPreset = 'reference';
let snapshots = { a: null, b: null };
let testRecords = [];
let history = [];
let tick = 0;
const storageKey = 'deskfuse.audio.lab.v03.workspace';

const live = 'live', restart = 'restart', future = 'future', dev = 'dev';
const controls = [
  { id:'prebuffer', cat:'BUFFER ENGINE', name:'Startup prebuffer', type:'range', unit:'ms', min:5,max:50,step:5,
    description:'Audio collected before first playback or re-prime. Affects startup and recovery, not directly steady-state delay.', status:live,
    minLabel:'5 ms · fragile', maxLabel:'50 ms · cautious' },
  { id:'targetQueue', cat:'BUFFER ENGINE', name:'Steady-state queue target', type:'range', unit:'ms', min:5,max:50,step:5,
    description:'The main adjustable contributor to ongoing application-side latency. Smaller is faster but less resilient.', status:live,
    minLabel:'5 ms · fast', maxLabel:'50 ms · resilient' },
  { id:'hardwarePolicy', cat:'ANDROID OUTPUT', name:'AAudio buffer policy', type:'options',
    values:[['platform','Platform default (0)'],['bursts2','2 bursts · candidate'],['bursts3','3 bursts'],['bursts4','4 bursts'],['bursts6','6 bursts']],
    description:'Reference request is platform default (0). A test reported 4,104 frames of granted capacity; capacity is NOT end-to-end delay. Burst modes are hypothetical here.', status:dev },
  { id:'nativeReserveFrames', cat:'ANDROID RENDERER', name:'Native post-callback reserve', type:'range',unit:'frames',min:240,max:2880,step:240,
    description:'Reported reference is 1,440 frames / 30 ms. Proposed tuning must preserve callback and re-prime safety; this is distinct from AAudio granted buffer capacity.',status:future,
    minLabel:'240 frames · 5 ms',maxLabel:'2,880 frames · 60 ms' },
  { id:'adaptiveBuffer', cat:'BUFFER ENGINE', name:'Adaptive buffering', type:'options', values:[['off','Off'],['low','Low'],['medium','Medium'],['high','High']],
    description:'Proposed automatic policy for varying jitter. Not part of the reported reference; do not confuse this with deployed auto-buffer logic.', status:future },
  { id:'resampler', cat:'FIDELITY', name:'Resampling engine', type:'options', values:[['linear','Linear comparator'],['fir','Low-delay FIR candidate'],['sinc128','128-tap streaming sinc']],
    description:'The listening-test reference uses the 128-tap streaming sinc. Developer DSP toggles exist; this website is not connected, and filter delay still needs qualification.', status:dev },
  { id:'driftPpm', cat:'CLOCK CORRECTION', name:'Maximum drift correction', type:'range', unit:'ppm', min:100,max:500,step:50,
    description:'Reported reference ±400 ppm; bounded correction follows Android output demand, never sender packet arrival.', status:future,
    minLabel:'±100 ppm', maxLabel:'±500 ppm' },
  { id:'maxSlewPpm', cat:'CLOCK CORRECTION', name:'Maximum drift slew', type:'range',unit:'ppm/s',min:20,max:200,step:10,
    description:'Reported reference 100 ppm/s. Limits how quickly correction changes; distinct from maximum drift-correction magnitude.',status:future,
    minLabel:'20 ppm/s · smooth', maxLabel:'200 ppm/s · reactive' },
  { id:'responseSpeed', cat:'CLOCK CORRECTION', name:'Drift controller response', type:'range', unit:'×', min:0.5,max:4,step:0.5,
    description:'Conceptual response-time control, separate from the reported 100 ppm/s slew limit. Not separately qualified by the recent listening notes.', status:future,
    minLabel:'Slow / smooth', maxLabel:'Fast / reactive' },
  { id:'packetAge', cat:'QUIC FRESHNESS', name:'Maximum source age', type:'range', unit:'ms', min:20,max:90,step:5,
    description:'Discard overly old captured PCM rather than accumulate delayed sound. Current source-age bound is 60 ms.', status:live,
    minLabel:'20 ms · fresh', maxLabel:'90 ms · permissive' },
  { id:'packetMs', cat:'QUIC FRAMING', name:'Audio packet duration', type:'options', values:[['2.5','2.5 ms'],['5','5 ms'],['10','10 ms']],
    description:'Current packetization is 5 ms. Changing it requires coordinated source, protocol and receiver changes.', status:future },
  { id:'concealMs', cat:'RECOVERY', name:'Gap concealment allowance', type:'range', unit:'ms', min:0,max:20,step:5,
    description:'Reported reference: ON, at most 480 frames / 10 ms of short-gap concealment.', status:future,
    minLabel:'0 ms', maxLabel:'20 ms' },
  { id:'fadeMs', cat:'FIDELITY', name:'Anti-click fade length', type:'range', unit:'ms', min:0,max:5,step:0.5,
    description:'Reported reference: ON, 96 frames / 2 ms. A fade reduces clicks but does not reconstruct lost PCM.', status:future,
    minLabel:'Off', maxLabel:'5 ms' },
  { id:'outputMode', cat:'ANDROID OUTPUT', name:'AAudio sharing request', type:'options', values:[['shared','Shared'],['auto','Auto'],['exclusive','Exclusive']],
    description:'Current native engine requests shared output. Exclusive may lower delay if supported; reopening required.', status:restart },
  { id:'audioUsage', cat:'ANDROID OUTPUT', name:'Android usage classification', type:'options', values:[['unspecified','Not specified'],['media','Media'],['game','Game']],
    description:'Not specified in the listening-test reference. Game classification may influence device routing and processing.', status:restart },
  { id:'recoveryMode', cat:'RECOVERY', name:'Recovery priority', type:'options', values:[['fast','Fast'],['balanced','Balanced'],['conservative','Conservative']],
    description:'Conceptual choice between quicker re-prime and more playback headroom after underruns.', status:future }
];
const statusText = { [live]:'◇ LIVE DESIGN', [restart]:'↻ RESTART', [future]:'⌘ FUTURE', [dev]:'◉ DEV CONTROL' };
const suffix = (c,value) => {
  if(c.id==='nativeReserveFrames')return `${Number(value).toLocaleString('en-US')} frames / ${(Number(value)/48).toFixed(0)} ms`;
  if(c.id==='maxSlewPpm')return `${value} ppm/s`;
  if(c.id==='driftPpm')return `±${value} ppm`;
  if(c.id==='responseSpeed')return `${Number(value).toFixed(1)}×`;
  if(c.id==='fadeMs')return value===0?'OFF':`${value} ms`;
  if(c.id==='concealMs')return Number(value)===0?'OFF':`${value} ms`;
  return `${value} ${c.unit ?? ''}`.trim();
};

function renderControls(){
  $('#controls-grid').innerHTML = controls.map((c,i)=>{
    const value = state[c.id];
    let input = '';
    if(c.type==='range'){
      input = `<input data-setting="${c.id}" class="sim-range" type="range" min="${c.min}" max="${c.max}" step="${c.step}" value="${value}" aria-label="${c.name}" style="--fill:${(value-c.min)/(c.max-c.min)*100}%"><div class="slider-meta"><span>${c.minLabel}</span><span>${c.maxLabel}</span></div>`;
    } else {
      input = `<div class="select-control" data-control="${c.id}">${c.values.map(([v,label])=>`<button type="button" data-choice="${c.id}" data-value="${v}" class="option-button ${v===value?'selected':''}" aria-pressed="${v===value}">${label}</button>`).join('')}</div>`;
    }
    return `<article class="control-card"><div class="control-header"><span class="control-num">${String(i+1).padStart(2,'0')} / ${c.cat}</span><span class="mode-chip chip-${c.status}">${statusText[c.status]}</span></div><div class="control-title"><span>${c.name}</span>${c.type==='range'?`<output id="value-${c.id}" for="${c.id}">${suffix(c,value)}</output>`:''}</div><p class="control-description">${c.description}</p>${input}<div class="card-footer">${c.status===dev?'Developer control reported; this offline UI is not wired.':c.status===live?'Prototype would apply at a safe boundary.':c.status===restart?'Requires reopening the audio stream.':'Future parameter integration, not currently a runtime slider.'}</div></article>`;
  }).join('');
}

const advanced = [
 { title:'RECEIVER / STARVATION, PRIMING & RECOVERY', detail:'Next focused listening-test review; no new recovery policy claimed', items:[
   {id:'starvationStatus',name:'Receiver starvation events',kind:'readout',readout:'Not connected · inspect existing receiver counters',status:'read',hint:'Separate upstream gaps, native starvation and AAudio underruns.'},
   {id:'primeStatus',name:'Startup and re-prime sequence',kind:'readout',readout:'Test target · inspect native reserve and actual callback demand',status:'read',hint:'Record when audio becomes audible and when recovery re-establishes continuous playback.'},
   {id:'recoveryStatus',name:'Authenticated recovery and re-anchor',kind:'readout',readout:'Test target · existing implementation retained',status:'read',hint:'Do not trade away monotonic source identity or authentication for marginal latency.'},
   {id:'recoveryThreshold',name:'Renderer underrun / x-run count',kind:'readout',readout:'Not connected · existing diagnostics required',status:'read'},
   {id:'testGuidance',name:'Recommended next experiment',kind:'readout',readout:'Change only reserve or buffer policy, then listen and log',status:'read'}
 ]},
 { title:'ANDROID / OUTPUT HARDWARE' , detail:'Requested capabilities versus actual HAL behavior', items:[
   {id:'performanceMode',name:'Granted performance mode',kind:'readout',readout:'Unknown · device disconnected',status:'read'},
   {id:'actualBurst',name:'Actual frames per burst',kind:'readout',readout:'Not reported · must query actual stream',status:'read'},
   {id:'actualBuffer',name:'Reported AAudio capacity',kind:'readout',readout:'4,104 frames / 85.5 ms capacity (historical test)',status:'read',hint:'Not hardware latency, not necessarily full, not a current live value.'},
   {id:'requestedPerformance',name:'Requested AAudio performance',kind:'readout',readout:'Low latency · shared output in reference',status:'read'},
   {id:'outputRoute',name:'Audio route',kind:'select',values:[['speaker','Phone speaker'],['wired','Wired headset'],['bluetooth','Bluetooth']],status:'restart',hint:'Bluetooth can add substantial codec/radio latency.'},
   {id:'callbackSizes',name:'Actual callback-size distribution',kind:'readout',readout:'Unknown · requires live hardware trace',status:'read'}
 ]},
 { title:'WINDOWS / CAPTURE ENGINE', detail:'Event-driven WASAPI and future virtual-device integration', items:[
   {id:'reportedEndpoint',name:'Reference Windows endpoint',kind:'readout',readout:'MM-1 · reported from listening test',status:'read'},
   {id:'pcmFormat',name:'Reference PCM format',kind:'readout',readout:'48 kHz / stereo / 16-bit',status:'read'},
   {id:'captureSource',name:'Capture source',kind:'select',values:[['loopback','Current MM-1 speaker loopback'],['virtual','DeskFuse virtual endpoint']],status:'future',hint:'Virtual endpoint integration is a design route, not an active capture mode.'},
   {id:'capturePeriod',name:'Capture period strategy',kind:'select',values:[['default','Event-driven / current'],['5','5 ms candidate'],['10','10 ms candidate']],status:'future',hint:'The 200 ms WASAPI buffer capacity is not its capture period.'},
   {id:'nativeQueue',name:'Native writer queue depth',kind:'select',values:[['4','4 blocks / 20 ms'],['8','8 blocks / 40 ms'],['16','16 blocks / 80 ms']],status:'future',hint:'Current fixed capacity: 8 × 240-frame blocks.'},
   {id:'rustQueue',name:'Rust reader channel depth',kind:'select',values:[['4','4 blocks / 20 ms'],['8','8 blocks / 40 ms'],['16','16 blocks / 80 ms']],status:'future',hint:'Current fixed capacity: 8 blocks; capacity is not accumulated latency.'},
   {id:'handoff',name:'Native → Rust handoff',kind:'select',values:[['pipe','Current local pipe'],['shm','Shared memory candidate'],['direct','Direct in-process candidate']],status:'future',hint:'Do not change transport semantics merely to eliminate a syscall.'},
   {id:'wakeStrategy',name:'Reader notification strategy',kind:'select',values:[['per-block','Wake on each block'],['coalesced','Coalesced wakeups']],status:'future',hint:'Wake coalescing saves overhead but may increase pickup delay.'}
 ]},
 { title:'QUIC / NETWORK TRANSPORT', detail:'Media freshness, queue pressure and controlled network scenarios', items:[
   {id:'egressMode',name:'QUIC pacing behavior',kind:'select',values:[['paced','Protocol-paced (current)'],['burst-guard','Burst-guard candidate']],status:'future',hint:'Never impose an independent media clock from ACKs.'},
   {id:'freshness',name:'Old-packet handling',kind:'select',values:[['strict','Strict drop (current)'],['balanced','Balanced candidate']],status:'future',hint:'No security bypass, retroactive ciphertext edit or timestamp rewrites.'},
   {id:'networkScenario',name:'Simulated Wi-Fi condition',kind:'select',values:[['good','Good'],['variable','Variable'],['congested','Congested']],status:'sim',hint:'Changes the demo model only; not a real Wi-Fi control.'},
   {id:'jitterMs',name:'Scenario jitter',kind:'range',min:0,max:30,step:1,unit:'ms',status:'sim',hint:'Mock input; actual network jitter is observed, not directly configurable.'},
   {id:'lossPct',name:'Scenario packet loss',kind:'range',min:0,max:5,step:0.1,unit:'%',status:'sim',hint:'Mock impairment input; no packets are transmitted.'},
   {id:'transportLoss',name:'Queue loss / DATAGRAM events',kind:'readout',readout:'Unknown · live QUIC counters required',status:'read'}
 ]},
 { title:'QUALITY / VALIDATION & SAFETY', detail:'Separate measured fidelity, latency and playback continuity', items:[
   {id:'controllerStrategy',name:'Playout clock authority',kind:'select',values:[['output','Android output callback (retain)']],status:'locked',hint:'Safety invariant. Sender QUIC ACK timing must not become the audio clock.'},
   {id:'clockPrecision',name:'Capture source timestamps',kind:'select',values:[['source-qpc','Original WASAPI packet QPC (retain)']],status:'locked',hint:'Safety invariant. Preserve original sample positions and provenance.'},
   {id:'telemetryDetail',name:'Telemetry detail',kind:'select',values:[['minimal','Minimal'],['normal','Normal'],['extended','Extended']],status:'future',hint:'Diagnostics must stay outside real-time audio callbacks.'},
   {id:'testMode',name:'Acoustic validation',kind:'select',values:[['none','Not recording'],['alignment','Speaker-to-phone alignment plan']],status:'future',hint:'Requires an actual synchronous acoustic recording; not modeled as measured delay.'},
   {id:'realLatency',name:'Measured physical delay',kind:'readout',readout:'NOT MEASURED — requires microphone capture',status:'read'},
   {id:'safety',name:'Transport identity and recovery',kind:'readout',readout:'Protected · authentication and monotonic source IDs',status:'locked'}
 ]}
];
function advancedTag(kind){
  if(kind==='read')return '<span class="mode-chip chip-live">READ ONLY</span>';
  if(kind==='locked')return '<span class="mode-chip chip-live">LOCKED</span>';
  if(kind==='sim')return '<span class="mode-chip chip-live">SIM INPUT</span>';
  return `<span class="mode-chip chip-${kind}">${statusText[kind]}</span>`;
}
function renderAdvanced(){
  $('#advanced-groups').innerHTML = advanced.map((group,index)=>`<details class="advanced-group" ${index===0?'open':''}>
    <summary><span class="advanced-no">0${index+1}</span><div class="advanced-label"><strong>${group.title}</strong><small>${group.detail}</small></div><span class="mode-chip chip-future">${group.items.length} OPTIONS</span><span class="advanced-chevron">+</span></summary>
    <div class="advanced-contents">${group.items.map(item=>{
      let widget='';
      if(item.kind==='readout')widget=`<span class="readout-value">${item.readout}</span>`;
      if(item.kind==='select')widget=`<select class="select-native" data-setting="${item.id}" aria-label="${item.name}" ${item.status==='locked'?'disabled':''}>${item.values.map(([v,t])=>`<option value="${v}" ${state[item.id]===v?'selected':''}>${t}</option>`).join('')}</select>`;
      if(item.kind==='range')widget=`<div class="adv-slider"><input class="sim-range" data-setting="${item.id}" type="range" min="${item.min}" max="${item.max}" step="${item.step}" value="${state[item.id]}" aria-label="${item.name}"><div class="adv-range-meta"><span>${item.min} ${item.unit}</span><output id="value-${item.id}">${state[item.id]} ${item.unit}</output><span>${item.max} ${item.unit}</span></div></div>`;
      return `<div class="advanced-item"><div class="advanced-item-head"><strong>${item.name}</strong>${advancedTag(item.status)}</div>${widget}${item.hint?`<p>${item.hint}</p>`:''}</div>`;
    }).join('')}</div></details>`).join('');
}

// This is an illustrative causal model, not a calibrated latency or quality predictor.
function predict(s){
  const capturePeriod = s.capturePeriod==='5'?5:s.capturePeriod==='10'?10:10;
  const capture = (s.captureSource==='virtual'?6.5:10.5) + (capturePeriod-5)*0.22;
  const handoff = s.handoff==='direct'?0.8:s.handoff==='shm'?1.3:3.1;
  const packet = Number(s.packetMs)*0.52;
  const congestion = s.networkScenario==='good'?0:s.networkScenario==='variable'?5:13;
  const network = 5.5 + Number(s.jitterMs)*0.58 + Number(s.lossPct)*1.3 + congestion;
  const receiver = 2.2;
  // Application reserve is NOT AAudio platform capacity. Only a portion is
  // modeled as steady-state headroom; these coefficients are illustrative.
  const queue = Number(s.targetQueue)*0.35 + Number(s.nativeReserveFrames)/48*0.65;
  const dsp = s.resampler==='linear'?0.4:s.resampler==='fir'?1.8:3.4;
  const shared = s.outputMode==='shared'?12.5:s.outputMode==='auto'?9:7;
  const route = s.outputRoute==='bluetooth'?65:s.outputRoute==='wired'?5:0;
  const configuredBursts = s.hardwarePolicy==='platform'?4:
    Number(s.hardwarePolicy.replace('bursts','')) || 4;
  // Platform buffer request=0 is not equivalent to a measured four-burst
  // hardware delay. Four bursts is a deliberately hypothetical plot input.
  const aaudio = configuredBursts*4 + shared + route + (s.audioUsage==='game'?-0.5:0);
  const parts=[capture,handoff,packet+network,receiver,queue,dsp,aaudio].map(round1);
  const latency = round1(parts.reduce((a,b)=>a+b,0));
  let stability = 99.7 - Math.max(0,22-Number(s.targetQueue))*0.78
    - Math.max(0,4-configuredBursts)*3.5
    - Math.max(0,45-Number(s.packetAge))*0.13
    - Math.max(0,Number(s.jitterMs)-5)*0.45
    - Number(s.lossPct)*2.1 - congestion*.48;
  if(s.adaptiveBuffer==='high') stability+=1.8;
  if(s.adaptiveBuffer==='medium') stability+=1;
  if(s.adaptiveBuffer==='low') stability+=0.5;
  if(s.concealMs===0) stability-=2;
  if(s.recoveryMode==='fast') stability-=1.5;
  if(s.outputMode==='exclusive') stability-=1;
  stability=round1(clamp(stability,20,99.9));
  let fidelity = s.resampler==='linear'?79:s.resampler==='fir'?91:96;
  fidelity += s.fadeMs>0?1.1:0;
  fidelity -= Math.max(0,Number(s.lossPct)*1.5 + (96-stability)*0.15);
  fidelity = Math.round(clamp(fidelity,30,99));
  // Front-end coordinates: X=faster projected response; Y=continuity.
  const speed = clamp(Math.round((132-latency)/1.04),5,96);
  return {latency,stability,fidelity,speed,parts};
}
const partNames=[['01','WASAPI'],['02','CAPTURE IPC'],['03','QUIC + WIFI'],['04','RECEIVER'],['05','APP BUFFER'],['06','RESAMPLER'],['07','AAUDIO / HAL']];
const partColors=['#4b8c55','#77bd67','#83b852','#648d78','#b4f66e','#9dca73','#d5ad64'];

function paintRange(input){const min=Number(input.min),max=Number(input.max),v=Number(input.value);input.style.setProperty('--fill', `${(v-min)/(max-min)*100}%`);}
function updateReadouts(){
  const model=predict(state);
  $('#metric-latency').innerHTML=`${Math.round(model.latency)}<small>ms</small>`;
  $('#metric-stability').innerHTML=`${model.stability.toFixed(1)}<small>%</small>`;
  $('#metric-fidelity').innerHTML=`${model.fidelity}<small>/ 100</small>`;
  $('#stability-bar').style.width=`${model.stability}%`;
  $('#fidelity-bar').style.width=`${model.fidelity}%`;
  $('#latency-marker').style.left=`${clamp((model.latency-35)/(140-35)*100,0,100)}%`;
  $('#signal-total').innerHTML=`${Math.round(model.latency)} ms <small>TOTAL</small>`;
  $('#signal-pipeline').innerHTML=partNames.map(([number,name],i)=>`<div class="signal-node" title="Simulated component delay"><span class="node-index">${number}</span><strong class="node-name">${name}</strong><span class="node-time">${model.parts[i]}<small> ms</small></span></div>`).join('');
  $('#signal-stack').innerHTML=model.parts.map((value,i)=>`<span class="signal-segment" title="${partNames[i][1]}: ${value} modeled ms" style="width:${value/model.latency*100}%;background:${partColors[i]}"></span>`).join('');
  const profiles=['ultra','safe','balanced','quality'];
  for(const name of profiles){
    const projected = predict({...presetData[name],networkScenario:state.networkScenario,jitterMs:state.jitterMs,lossPct:state.lossPct});
    positionPoint($(`#point-${name}`),projected);
  }
  positionPoint($('#point-current'),model);
  drawTelemetry();
  updateReferenceDelta();
}
function positionPoint(node,model){
  node.style.left = `${clamp(model.speed,7,89)}%`;
  node.style.bottom = `${clamp(((model.stability-43)/57)*86+7,7,92)}%`;
}

function updateMaster(){
  // Master bias maps a few latency-policy dimensions only, not fidelity.
  let x = Number($('#master-bias').value);
  $('#master-readout').innerHTML=`${x}<span>/ 100</span>`;
  state.targetQueue=clamp(40-Math.round(x*.32/5)*5,5,50);
  state.prebuffer=clamp(40-Math.round(x*.32/5)*5,5,50);
  state.nativeReserveFrames=clamp(Math.round((48-x*.39)/5)*240,240,2880);
  state.hardwarePolicy=x>82?'bursts2':x>58?'bursts3':x>35?'platform':'bursts6';
  state.concealMs=x>80?0:x>57?5:10;
  currentPreset='custom';
  renderControls();renderPresetState();updateReadouts();
  persistWorkspace();toast('Master bias updated modeled buffer policy. The listening-test reference was not overwritten.');
}
function renderPresetState(){
  $$('[data-preset]').forEach(btn=>{
    const active=btn.dataset.preset===currentPreset;
    btn.classList.toggle('active',active);btn.setAttribute('aria-pressed',String(active));
  });
}
function applyPreset(name){
  if(!presetData[name])return;
  state={...presetData[name],networkScenario:state.networkScenario,jitterMs:state.jitterMs,lossPct:state.lossPct};
  currentPreset=name;
  const map={reference:50,ultra:93,safe:73,balanced:63,quality:17};
  $('#master-bias').value=map[name];
  $('#master-readout').innerHTML=`${map[name]}<span>/ 100</span>`;
  paintRange($('#master-bias'));
  renderControls();renderAdvanced();renderPresetState();updateReadouts();
  persistWorkspace();toast(`${name.toUpperCase()} proposed profile loaded — no hardware changed.`);
}
function handleSetting(id,value){
  if(!(id in state))return;
  const item=controls.find(c=>c.id===id);
  const adv=advanced.flatMap(g=>g.items).find(a=>a.id===id);
  if(item?.type==='range'||adv?.kind==='range')state[id]=Number(value);
  else state[id]=value;
  currentPreset='custom';renderPresetState();
  if(item?.type==='range'){
    const out=$(`#value-${id}`);if(out)out.textContent=suffix(item,state[id]);
  }
  if(adv?.kind==='range'){
    const out=$(`#value-${id}`);if(out)out.textContent=`${state[id]} ${adv.unit}`;
  }
  if(item?.type==='options'){
    $$(`[data-choice="${id}"]`).forEach(b=>{
      const active=b.dataset.value===String(value);
      b.classList.toggle('selected',active);b.setAttribute('aria-pressed',String(active));
    });
  }
  updateReadouts();persistWorkspace();
}

function updateClock(){
  const d=new Date();$('#clock').textContent=d.toLocaleTimeString('en-GB',{hour12:false});
}
function seedHistory(){const n=predict(state).latency;history=Array.from({length:60},(_,i)=>n+Math.sin(i*.36)*2.5+Math.sin(i*.98)*1.3);}
function cycleTelemetry(){
  tick++;
  const m=predict(state),noise=Math.sin(tick*.53)*1.7+Math.cos(tick*1.2)*1.2;
  const jitter=Math.min(8,Number(state.jitterMs)*.27);
  const value=m.latency+noise+jitter*Math.sin(tick*.19);
  history.push(value);if(history.length>60)history.shift();drawTelemetry();
}
function drawTelemetry(){
  const canvas=$('#telemetry-chart');if(!canvas)return;
  const rect=canvas.getBoundingClientRect();if(rect.width<5||rect.height<5)return;
  const dpr=Math.min(window.devicePixelRatio||1,2);const w=rect.width,h=rect.height;
  const cw=Math.round(w*dpr),ch=Math.round(h*dpr);
  if(canvas.width!==cw||canvas.height!==ch){canvas.width=cw;canvas.height=ch;}
  const ctx=canvas.getContext('2d');ctx.setTransform(dpr,0,0,dpr,0,0);ctx.clearRect(0,0,w,h);
  const data=history.length?history:[predict(state).latency];
  const center=predict(state).latency, min=center-16,max=center+16;
  ctx.strokeStyle='#28382b';ctx.lineWidth=1;
  for(let i=0;i<4;i++){let y=8+(h-16)*i/3;ctx.beginPath();ctx.moveTo(0,y+.5);ctx.lineTo(w,y+.5);ctx.stroke();}
  const px=(i)=>i/(Math.max(1,data.length-1))*(w-2)+1;
  const py=(v)=>clamp((max-v)/(max-min),.03,.97)*(h-16)+8;
  ctx.beginPath();data.forEach((v,i)=>i?ctx.lineTo(px(i),py(v)):ctx.moveTo(px(i),py(v)));
  ctx.lineTo(w,h);ctx.lineTo(0,h);ctx.closePath();const g=ctx.createLinearGradient(0,0,0,h);g.addColorStop(0,'#a4fb6e2f');g.addColorStop(1,'#a4fb6e00');ctx.fillStyle=g;ctx.fill();
  ctx.beginPath();data.forEach((v,i)=>i?ctx.lineTo(px(i),py(v)):ctx.moveTo(px(i),py(v)));
  ctx.strokeStyle='#b4f66e';ctx.lineWidth=1.8;ctx.stroke();
  const last=py(data[data.length-1]);ctx.beginPath();ctx.arc(w-1,last,3,0,Math.PI*2);ctx.fillStyle='#d5ffb8';ctx.fill();
}
let toastTimer=null;
function toast(message){const elem=$('#toast');elem.textContent=message;elem.classList.add('visible');clearTimeout(toastTimer);toastTimer=setTimeout(()=>elem.classList.remove('visible'),3200);}
function snapshotText(s){const m=predict(s);return `<span class="snapshot-data">${Math.round(m.latency)} ms <span style="color:#849b89">delay</span> · ${m.stability.toFixed(1)}% <span style="color:#849b89">stable</span><small>Fidelity ${m.fidelity}/100 · queue ${s.targetQueue} ms · ${s.resampler} · ${s.outputMode}</small></span>`;}
function renderSnapshots(){
  for(const which of ['a','b']){
    const el=$(`#snapshot-${which}`),snap=snapshots[which];
    const button=el.querySelector('button');
    $('.snapshot-top span',el).textContent=snap?'PINNED':'EMPTY';
    $('p',el).innerHTML=snap?snapshotText(snap):'No configuration pinned';
    button.textContent=snap?`↻ REPLACE SNAPSHOT ${which.toUpperCase()}`:`⊕ PIN CURRENT AS ${which.toUpperCase()}`;
  }
  const out=$('#comparison-result');
  if(!snapshots.a||!snapshots.b){out.textContent='Pin two configurations to compare their modeled delay, stability and fidelity.';return;}
  const a=predict(snapshots.a),b=predict(snapshots.b);
  const delta=(x,digits=1)=>x>0?`+${x.toFixed(digits)}`:x.toFixed(digits);
  out.textContent=`B vs A  ·  Delay ${delta(b.latency-a.latency)} ms (negative is faster)  ·  Stability ${delta(b.stability-a.stability)} pts  ·  Fidelity ${delta(b.fidelity-a.fidelity,0)} pts. Simulated comparisons only.`;
}
function saveSnapshot(which){snapshots[which]={...state};renderSnapshots();persistWorkspace();toast(`Snapshot ${which.toUpperCase()} captured in this tab.`);}
function exportSettings(){
  const payload={schema:'deskfuse-audio-lab-v0.3',title:'DeskFuse Audio Lab — personal settings',created:new Date().toISOString(),note:'Test-reported reference; not connected to audio engine. Predicted scores are hypothetical.',reference:{...listeningReference},profile:currentPreset,settings:{...state},modeled:predict(state),snapshotA:snapshots.a,snapshotB:snapshots.b,tests:[...testRecords]};
  const blob=new Blob([JSON.stringify(payload,null,2)+'\n'],{type:'application/json'});
  const url=URL.createObjectURL(blob);const a=document.createElement('a');a.href=url;  a.download='deskfuse-audio-lab-workspace.json';document.body.appendChild(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(url),2000);
  toast('Exported simulated configuration as JSON.');
}


// --- Personal testing notebook. All records stay on device until exported. ---
function htmlSafe(v){return String(v).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));}
function changedKeys(s){return Object.keys(initial).filter(k=>String(s[k])!==String(initial[k]));}
function knownReferenceDifferences(s){return referenceKeys.filter(k=>String(s[k])!==String(initial[k]));}
function updateReferenceDelta(){
  const n=knownReferenceDifferences(state).length;
  const all=changedKeys(state);
  const label=$('#baseline-diff');
  if(label){label.textContent=n===0?'REPORTED VALUES MATCH REFERENCE':`${n} REPORTED FIELD${n===1?'':'S'} CHANGED`;label.classList.toggle('changed',n>0);}
  $('#reference-compare').textContent=all.length===0?'Reference workspace loaded.':
    `${all.length} setting${all.length===1?'':'s'} differs from full laboratory starting state; ${n} in explicitly reported reference fields.`;
  const badge=$('#factor-count');
  badge.textContent=all.length===0?'REFERENCE STATE':`${all.length} DIFFERENCE${all.length===1?'':'S'}`;
  badge.classList.toggle('not-isolated',all.length>1);
  const selected=$('#focus-factor')?.value;
  if(selected){const current=state[selected];const base=initial[selected];
    $('#factor-explanation').textContent=all.length===0?'Reference starting state; no settings changed.':
      all.length===1?`One-factor comparison: ${selected} = ${current} (reference model value ${base}).`:
      `${all.length} model fields differ. Use “Isolate this factor” to preserve only ${selected} and restore everything else.`;}
}
function restoreReference(){state={...initial};currentPreset='reference';
  $('#master-bias').value=50;$('#master-readout').innerHTML='50<span>/ 100</span>';paintRange($('#master-bias'));
  renderControls();renderAdvanced();renderPresetState();updateReadouts();persistWorkspace();toast('Test-reported reference restored. No audio hardware changed.');}
function isolateFactor(){const key=$('#focus-factor').value;if(!(key in initial))return;
  const selected=state[key];state={...initial,[key]:selected};currentPreset='custom';
  renderControls();renderAdvanced();renderPresetState();updateReadouts();persistWorkspace();
  toast('All other settings restored to the reference state.');}
function revertFocus(){const key=$('#focus-factor').value;if(!(key in initial))return;
  state[key]=initial[key];currentPreset='custom';renderControls();renderAdvanced();renderPresetState();updateReadouts();persistWorkspace();
  toast('Focused parameter restored.');}
function setupFocusSelect(){const all=[...controls,...advanced.flatMap(g=>g.items).filter(i=>i.kind==='select'||i.kind==='range')];
  const options=all.filter(x=>x.id in initial);
  $('#focus-factor').innerHTML=options.map(c=>`<option value="${htmlSafe(c.id)}">${htmlSafe(c.name)}</option>`).join('');
  $('#focus-factor').value='nativeReserveFrames';}
function newTest(){
  const number=$('#test-measured').value.trim();const measured=number===''?null:Number(number);
  if(measured!==null&&(!Number.isFinite(measured)||measured<0||measured>2000)){toast('Measured delay must be between 0 and 2,000 ms, or blank.');return;}
  const changed=changedKeys(state);
  const record={id:Date.now().toString(36)+'-'+Math.random().toString(36).slice(2,7),
    at:new Date().toISOString(),label:$('#test-label').value.trim().slice(0,100)||`Session ${testRecords.length+1}`,
    route:$('#test-route').value.trim().slice(0,90),delay:$('#test-delay').value,
    continuity:$('#test-continuity').value,quality:$('#test-quality').value,
    measuredDelayMs:measured,notes:$('#test-notes').value.trim().slice(0,2500),
    focus:$('#focus-factor').value,changedKeys:changed,settings:{...state}};
  testRecords.unshift(record);if(testRecords.length>100)testRecords.length=100;
  renderTestRecords();persistWorkspace();$('#test-label').value='';$('#test-notes').value='';$('#test-measured').value='';
  toast(changed.length===1?'Controlled one-factor listening test saved.':'Listening test saved with change count indicated.');
}
function renderTestRecords(){
  $('#test-count').textContent=testRecords.length;
  const box=$('#test-records');
  if(!testRecords.length){box.innerHTML='<p class="empty-notebook">No tests yet. Change a parameter, then save your first observation.</p>';return;}
  box.innerHTML=testRecords.map((r)=>`<article class="test-item">
    <div class="test-item-top"><strong>${htmlSafe(r.label)}</strong><span class="test-count-badge ${r.changedKeys.length===1?'isolated':''}">${r.changedKeys.length===1?'ONE-FACTOR':`${r.changedKeys.length} FACTORS`}</span></div>
    <div class="test-item-meta"><span>${htmlSafe(r.at.slice(0,16).replace('T',' '))} UTC</span><span>${htmlSafe(r.route)}</span></div>
    <p class="test-changes">${r.changedKeys.length?r.changedKeys.map(k=>`${htmlSafe(k)}: ${htmlSafe(r.settings[k])}`).join(' · '):'Reference state · no changes'}</p>
    <div class="test-item-scores"><span>DELAY ${htmlSafe(r.delay)}</span><span>CONTINUITY ${htmlSafe(r.continuity)}</span><span>QUALITY ${htmlSafe(r.quality)}</span>${r.measuredDelayMs!==null?`<span>MEASURED ${htmlSafe(r.measuredDelayMs)} ms</span>`:''}</div>
    ${r.notes?`<p class="test-note">${htmlSafe(r.notes)}</p>`:''}
    <button type="button" class="test-load button-secondary" data-test-load="${htmlSafe(r.id)}">↶ LOAD SETTINGS</button>
  </article>`).join('');
}
function persistWorkspace(){
 try{localStorage.setItem(storageKey,JSON.stringify({schema:'deskfuse-audio-lab-v0.3',settings:state,profile:currentPreset,tests:testRecords,snapshots}));
    $('#storage-note').textContent='Saved in this browser on this device. Export JSON for backups or moving the laboratory. No network calls.';
 }catch(e){$('#storage-note').textContent='Browser storage unavailable (possible in file://). Export JSON to keep records; tab remains usable.';}
}
function isRecord(r){return !!r&&typeof r==='object'&&typeof r.id==='string'&&typeof r.at==='string'&&
  typeof r.label==='string'&&r.label.length<=100&&typeof r.route==='string'&&r.route.length<=90&&
  typeof r.delay==='string'&&typeof r.continuity==='string'&&typeof r.quality==='string'&&
  (r.measuredDelayMs===null||(typeof r.measuredDelayMs==='number'&&r.measuredDelayMs>=0&&r.measuredDelayMs<=2000))&&
  typeof r.notes==='string'&&r.notes.length<=2500&&r.settings&&typeof r.settings==='object';}
function safeSettings(src){
 const next={...initial};if(!src||typeof src!=='object')return next;
 for(const c of controls){const val=src[c.id];if(val===undefined)continue;
   if(c.type==='range'){const x=Number(val);if(Number.isFinite(x)&&x>=c.min&&x<=c.max&&Math.abs(Math.round((x-c.min)/c.step)-(x-c.min)/c.step)<1e-6)next[c.id]=x;}
   else if(c.values.some(v=>v[0]===val))next[c.id]=val;
 }
 for(const g of advanced)for(const c of g.items){if(!(c.id in next))continue;const val=src[c.id];if(val===undefined)continue;
   if(c.kind==='range'){const x=Number(val);if(Number.isFinite(x)&&x>=c.min&&x<=c.max)next[c.id]=x;}
   else if(c.kind==='select'&&c.values.some(v=>v[0]===val))next[c.id]=val;}
 return next;
}
function restoreWorkspace(data){
 if(!data||data.schema!=='deskfuse-audio-lab-v0.3'||!data.settings||!Array.isArray(data.tests||[]))throw Error('Unsupported lab document.');
 state=safeSettings(data.settings);
 testRecords=(data.tests||[]).filter(isRecord).slice(0,100).map(r=>({...r,settings:safeSettings(r.settings),changedKeys:changedKeys(safeSettings(r.settings))}));
 snapshots={a:data.snapshots?.a?safeSettings(data.snapshots.a):null,b:data.snapshots?.b?safeSettings(data.snapshots.b):null};
 currentPreset=(data.profile in presetData)?data.profile:'custom';
 renderControls();renderAdvanced();renderPresetState();updateReadouts();renderTestRecords();renderSnapshots();
 $('#master-bias').value=50;paintRange($('#master-bias'));persistWorkspace();
}
async function importWorkspace(file){
 if(!file)return;
 if(file.size>1024*1024){toast('Lab file exceeds 1 MB limit.');return;}
 try{const data=JSON.parse(await file.text());restoreWorkspace(data);toast('Local lab notebook imported. No network request made.');}
 catch(e){toast('Could not import this lab JSON file. Existing data preserved.');}
}
function exportNotebook(){const payload={schema:'deskfuse-audio-lab-v0.3',description:'Offline listening-test notebook. Reported reference values vs user tests; model outputs not measured.',
  exported:new Date().toISOString(),reference:{...listeningReference},settings:{...state},profile:currentPreset,tests:testRecords,snapshots};
 const data=new Blob([JSON.stringify(payload,null,2)+'\n'],{type:'application/json'});const url=URL.createObjectURL(data);
 const a=document.createElement('a');a.href=url;a.download='deskfuse-audio-lab-tests.json';document.body.appendChild(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(url),1200);toast('Exported complete listening-test notebook.');}
function loadSavedWorkspace(){
 try{const raw=localStorage.getItem(storageKey);if(!raw)return;const data=JSON.parse(raw);
  restoreWorkspace(data);}catch(e){$('#storage-note').textContent='Local save unavailable; export JSON to back up test sessions.';}
}

function boot(){
  setupFocusSelect();renderControls();renderAdvanced();renderPresetState();
  $$('.sim-range').forEach(paintRange);paintRange($('#master-bias'));
  seedHistory();updateReadouts();updateClock();renderSnapshots();renderTestRecords();loadSavedWorkspace();
  setInterval(updateClock,1000);setInterval(cycleTelemetry,1100);
  document.addEventListener('input',event=>{
    const input=event.target;
    if(input.id==='master-bias'){paintRange(input);updateMaster();return;}
    if(input.matches('input[data-setting]')){paintRange(input);handleSetting(input.dataset.setting,input.value);}
    if(input.matches('select[data-setting]'))handleSetting(input.dataset.setting,input.value);
  });
  document.addEventListener('change',event=>{
    const input=event.target;
    if(input.matches('select[data-setting]'))handleSetting(input.dataset.setting,input.value);
    if(input.id==='focus-factor')updateReferenceDelta();
  });
  document.addEventListener('click',event=>{
    const choice=event.target.closest('[data-choice]');
    if(choice){handleSetting(choice.dataset.choice,choice.dataset.value);return;}
    const preset=event.target.closest('[data-preset]');
    if(preset){applyPreset(preset.dataset.preset);return;}
    const pin=event.target.closest('[data-snapshot]');
    if(pin){saveSnapshot(pin.dataset.snapshot);return;}
    if(event.target.closest('#export-button'))exportSettings();
    if(event.target.closest('#export-notebook'))exportNotebook();
    if(event.target.closest('#load-reference'))restoreReference();
    if(event.target.closest('#isolate-factor'))isolateFactor();
    if(event.target.closest('#revert-factor'))revertFocus();
    const load=event.target.closest('[data-test-load]');
    if(load){const found=testRecords.find(x=>x.id===load.dataset.testLoad);if(found){state=safeSettings(found.settings);currentPreset='custom';renderControls();renderAdvanced();renderPresetState();updateReadouts();persistWorkspace();toast('Saved test settings loaded.');}}
    if(event.target.closest('#clear-notebook')){if(confirm('Delete all local listening-test records? Export a backup first if needed.')){testRecords=[];renderTestRecords();persistWorkspace();toast('Local test records cleared.');}}
    if(event.target.closest('#menu-button')){
      const open=$('#sidebar').classList.toggle('open');$('#menu-button').setAttribute('aria-expanded',String(open));
    }
    const nav=event.target.closest('.sidebar-nav a');
    if(nav){$$('.nav-link').forEach(a=>a.classList.remove('active'));nav.classList.add('active');$('#sidebar').classList.remove('open');$('#menu-button').setAttribute('aria-expanded','false');}
  });
  $('#test-form').addEventListener('submit',e=>{e.preventDefault();newTest();});
  $('#import-notebook').addEventListener('change',e=>{importWorkspace(e.target.files?.[0]);e.target.value='';});
  window.addEventListener('resize',drawTelemetry,{passive:true});
  if('ResizeObserver' in window)new ResizeObserver(drawTelemetry).observe($('#telemetry-chart'));
}
if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',boot);else boot();
