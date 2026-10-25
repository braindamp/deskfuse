/* The only real hardware button is START PHYSICAL TEST.
 * A file:// launch remains preview-only. No settings are sent automatically.
 */
'use strict';
(() => {
  const elem = id => document.getElementById(id);
  const MODELS = {
    baseline: {buffer_bursts:0, modes:'f1c1r1', desc:'Tested reference: 0 bursts / fade + conceal + sinc128'},
    b2: {buffer_bursts:2, modes:'f1c1r1', desc:'Two bursts; not physically qualified'},
    b3: {buffer_bursts:3, modes:'f1c1r1', desc:'Three bursts: prior listening tests reported worse interruptions'},
    fade: {buffer_bursts:0, modes:'f0c1r1', desc:'Disable anti-click fades; original 2ms setting otherwise unchanged'},
    conceal: {buffer_bursts:0, modes:'f1c0r1', desc:'Disable short-gap concealment; 10ms allowance otherwise unchanged'},
    bypass: {buffer_bursts:0, modes:'f1c1r0', desc:'Identity DSP bypass (NOT linear resampling); 128-tap sinc disabled'}
  };
  let auth = null, busy = false, lastState = null, mediaUrl = null;
  const update = (id, v) => { const n=elem(id); if(n) n.textContent=v; };
  const selected = () => MODELS[elem('live-factor').value] || MODELS.baseline;
  function warn(message) { update('live-message', message); elem('live-message').classList.add('live-alert'); }
  function clearWarn() { elem('live-message').classList.remove('live-alert'); update('live-message', selected().desc); }
  function gridToLive() {
    if(typeof state !== 'object' || typeof initial !== 'object') { warn('Preview settings are unavailable.'); return; }
    const diff=Object.keys(initial).filter(k=>String(state[k])!==String(initial[k]));
    if(diff.length===0){elem('live-factor').value='baseline';clearWarn();return;}
    if(diff.length!==1){warn('The tuning grid has more than one changed field. Restore the reference and isolate one supported setting.');return;}
    const [k]=diff;
    const key=k==='hardwarePolicy' && state[k]==='bursts2'?'b2':
      k==='hardwarePolicy' && state[k]==='bursts3'?'b3':
      k==='fadeMs' && state[k]===0?'fade':
      k==='concealMs' && state[k]===0?'conceal':null;
    if(!key){warn('That slider is a future/conceptual parameter, not implemented by the current native runner. No audio configuration changed.');return;}
    elem('live-factor').value=key;clearWarn();
  }
  async function refresh() {
    if(location.protocol==='file:') {
      update('live-state','PREVIEW ONLY'); warn('Open this lab through start-lab.ps1 and http://localhost:8765 to run real audio.');
      elem('live-start').disabled=true;return;
    }
    try{
      const response=await fetch('/api/status',{cache:'no-store'});
      if(!response.ok)throw new Error('Local bridge did not respond');
      const data=await response.json();lastState=data;auth=data.token;
      const source=elem('win-source')?.value||'mm1';
      const problems=data.source_preflight?.[source]||data.preflight;
      const ready=problems.length===0;
      update('live-state',data.running?'PHYSICAL TEST RUNNING':data.return_code===0?'COMPLETED · EXIT 0':data.return_code!==null?'ENDED · CHECK LOGS':ready?'BRIDGE READY':'SETUP REQUIRED');
      elem('live-state').className='live-status'+(data.running?' wait':(!ready||data.return_code!==null&&data.return_code!==0)?' bad':'');
      update('live-setup', ready?(source==='mm1'?'Local preflight OK. Four matching Release hashes are checked before launch.':'Product paths present. Host dependency hashes, receiver APK and native runtime gates are checked before use. Android callback/acoustic telemetry is unavailable here.'):problems.join('\n'));
      elem('live-start').disabled=!ready||data.running||busy||(source==='mm1'&&elem('win-engine-period')?.value==='minimum');
      elem('live-factor').disabled=data.running||source!=='mm1';
      for(const id of ['win-source','win-engine-period','win-capture-policy'])elem(id).disabled=data.running;
      elem('live-duration').disabled=data.running;
      elem('live-label').disabled=data.running;
      update('live-trial',data.profile || 'No physical session launched');
      update('live-evidence',data.evidence_folder || 'Not recorded yet');
      update('live-console', data.log?.length?data.log.join('\n'):'No physical session output. Preview charts elsewhere on this page remain simulated.');
      const tel=data.telemetry || {};
      update('live-underruns',tel.underrun_events??'—');
      update('live-reprimes',tel.reprime_events??'—');
      update('live-xruns',tel.xrun_count??tel.xruns??'—');
      update('live-queue',tel.buffer_frames??'—');
      update('live-gaps',tel.pre_quic_missing_frames??'—');
      update('live-clipping',tel.clipped_samples??'—');
      if(data.return_code===0 && data.profile) {
        if(!elem('test-label').value)elem('test-label').value=data.profile;
      }
    }catch(e){auth=null;update('live-state','BRIDGE OFFLINE');elem('live-state').className='live-status bad';
      update('live-setup','The interactive site is in preview mode. Start the separate local bridge to enable tests.');
      elem('live-start').disabled=true;}
  }
  async function start() {
    if(!auth || busy)return;
    busy=true;elem('live-start').disabled=true;
    const model=selected();
    const source=elem('win-source')?.value || 'mm1';
    const engine_period=elem('win-engine-period')?.value || 'reference';
    const capture_policy=elem('win-capture-policy')?.value || 'reference';
    if(source!=='mm1'&&(model.buffer_bursts!==0||model.modes!=='f1c1r1')) {warn('Product comparisons use unchanged Android reference settings. Select the baseline factor.');busy=false;refresh();return;}
    if(source==='mm1'&&engine_period==='minimum') {warn('Minimum period requires the DeskFuse driver gate.');busy=false;refresh();return;}
    if(source==='direct_export'&&capture_policy!=='reference') {warn('WASAPI allocation does not apply to direct PCM.');busy=false;refresh();return;}
    const changed=Number(model.buffer_bursts!==0)+[1,3,5].filter(i=>model.modes[i]!=='1').length+
      Number(engine_period!=='reference')+Number(capture_policy!=='reference');
    if(changed>1){warn('One-factor policy: restore other physical settings before testing a Windows policy.');busy=false;refresh();return;}
    const payload={...model,source,engine_period,capture_policy,seconds:Number(elem('live-duration').value),label:elem('live-label').value.trim()};
    if(source!=='mm1') {
      if(!confirm('Start an authenticated product audio run? Connect the already-enrolled phone in speaker-only mode, then explicitly route your application to its reported DeskFuse endpoint. Running driver/period/geometry checks may block the run. No routing, driver or APK installation occurs.')) {busy=false;refresh();return;}
    }else if(payload.buffer_bursts===2 || payload.buffer_bursts===3) {
      const known=payload.buffer_bursts===3?'The three-burst mode previously produced more interruptions; ':'';
      if(!confirm(known+'This will deploy matching test binaries to your Xiaomi via ADB and start physical playback. Continue?')) {busy=false;refresh();return;}
    }else{
      if(!confirm('Start real MM-1 → Xiaomi playback? The runner will validate matching Release hashes and may push test binaries over ADB. Explicitly route music to MM-1. No Windows default will be changed.')) {busy=false;refresh();return;}
    }
    try{
      const r=await fetch('/api/start',{method:'POST',headers:{'Content-Type':'application/json','X-DeskFuse-Lab-Token':auth},body:JSON.stringify(payload)});
      const data=await r.json();
      if(!r.ok)throw new Error(data.error||'Cannot start');
      update('live-message','Physical test launched: '+data.profile+'. Listen on Xiaomi and save your observations in Test notebook.');
      elem('live-message').classList.remove('live-alert');
      elem('test-label').value=data.profile;
    }catch(e){warn('START FAILED: '+String(e.message||e));}
    finally{busy=false;refresh();}
  }
  function chooseAudio(ev){
    const file=ev.target.files?.[0]; if(!file)return;
    if(mediaUrl)URL.revokeObjectURL(mediaUrl);
    mediaUrl=URL.createObjectURL(file);
    elem('live-audio').src=mediaUrl;
    update('live-music-caption','Local file: '+file.name+'. Browser plays through its Windows audio output route; ensure this reaches MM-1.');
  }
  document.addEventListener('DOMContentLoaded',()=>{
    elem('live-factor').addEventListener('change',clearWarn);
    elem('live-from-grid').addEventListener('click',gridToLive);
    elem('live-start').addEventListener('click',start);
    elem('live-music-file').addEventListener('change',chooseAudio);
    window.addEventListener('deskfuse-windows-source-changed',()=>{if(elem('win-source').value!=='mm1')elem('live-factor').value='baseline';refresh();});
    clearWarn();refresh();setInterval(refresh,1800);
  });
})();
