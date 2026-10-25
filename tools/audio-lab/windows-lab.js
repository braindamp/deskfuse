/* DeskFuse v0.7 Windows capture readiness display, native checkpoint 2421b68.
 * Planning-only selections never authorize driver actions or physical playback.
 */
'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const AUDIT = '2421b68';
  const SOURCES = [
    {id:'mm1',short:'W0',name:'MM-1 physical loopback reference',status:'PRESERVED REFERENCE',boundary:'session',impacts:['latency','continuity'],note:'Explicit physical comparison endpoint.',steps:['Explicit MM-1 endpoint','WASAPI loopback','Existing QUIC runner','Android AAudio'],detail:'Preserved ca747d8 listening observations; new builds do not inherit physical qualification.'},
    {id:'deskfuse',short:'W1',name:'DeskFuse same-endpoint loopback',status:'IMPLEMENTED · RUNTIME GATED',boundary:'session',impacts:['latency','continuity'],note:'Enrolled product control owns the endpoint.',steps:['Authenticated enrollment','Registry + pinned QUIC','Exact broker endpoint','WASAPI loopback','Existing sender → phone'],detail:'Select the endpoint explicitly in your application. Actual endpoint identity is recorded for comparison; no default routing changes.'},
    {id:'direct_export',short:'W2',name:'Committed WaveRT PCM',status:'IMPLEMENTED · RUNTIME GATED',boundary:'session',impacts:['latency','CPU'],note:'Same authenticated product pipeline; earlier source boundary.',steps:['Authenticated enrollment','Exact broker endpoint','Successful SetWritePacket','Bounded immutable PCM credits','Direct Rust → QUIC → phone'],detail:'Requires running timing build 2026102502, export ABI 2, two notifications and packets of 1–240 frames. Commit timestamps, generation fences and 60 ms age remain enforced.'}
  ];
  const GATES = [
    {id:'w1',code:'W-E1',title:'Authenticated endpoint',state:'IMPLEMENTED · NOT PHYSICALLY QUALIFIED',kind:'pending',detail:'Existing approval + per-phone enrollment, registry identity, session CSR and exact QUIC certificate pins precede broker provisioning. No typed peer hashes or Lab certificate authorization.'},
    {id:'driver',code:'W-E2',title:'Driver timing / minimum period',state:'LIVE NATIVE CHECK REQUIRED',kind:'pending',detail:'Corrected fractional clock, notifications and QPC accounting are implemented. Minimum requests query the running exact-endpoint driver and verify the actual granted period; a build receipt cannot pass this check.'},
    {id:'export',code:'W-E3',title:'Committed PCM export',state:'IMPLEMENTED · GEOMETRY GATED',kind:'pending',detail:'Successful commitment, immutable section-backed slots, bounded descriptors/frame credits, revocation and rundown. Actual packet geometry must qualify before source samples are accepted.'},
    {id:'rust',code:'W-E4',title:'Direct Rust / QUIC',state:'IMPLEMENTED · NOT PHYSICALLY QUALIFIED',kind:'pending',detail:'Direct source and period events share the existing QUIC owner. Original commit age, ordering, accepted-commit recovery fence and generation checks remain authoritative.'},
    {id:'security',code:'IDENTITY',title:'Product enrollment ↔ QUIC',state:'IMPLEMENTED · PRODUCT HOST ONLY',kind:'pending',detail:'Lab launches the existing speaker-only enrolled host. An existing host/port conflict is reported; no automatic takeover, enrollment, routing, driver or APK installation.'}
  ];
  const METRICS = [
    {id:'period',title:'Actual shared engine period',units:'frames',observed:'current_period_frames'},
    {id:'available',title:'Earliest valid PCM available QPC',units:'same-clock QPC',observed:null},
    {id:'commit',title:'WaveRT accepted PCM commit QPC',units:'same-clock QPC',observed:null},
    {id:'sender',title:'Rust sender-ready QPC / elapsed',units:'same-clock QPC',observed:null},
    {id:'source_gap',title:'Pre-QUIC source missing frames',units:'frames',observed:'pre_quic_missing_frames'},
    {id:'freshness',title:'Original commit age + recovery fence',units:'age / generation',observed:null},
    {id:'acoustic',title:'Phone acoustic end-to-end delay',units:'ms, separately measured',observed:null}
  ];
  let integration=null;
  let currentSession=null;
  const setText=(id,value)=>{const e=$(id);if(e)e.textContent=value;};
  const item=(tag,className,textValue)=>{const e=document.createElement(tag);if(className)e.className=className;if(textValue!==undefined)e.textContent=textValue;return e;};
  const selection=()=>({source:$('win-source').value,engine_period:$('win-engine-period').value,capture_policy:$('win-capture-policy').value});
  const selected=()=>SOURCES.find(x=>x.id===selection().source)||SOURCES[0];
  function uiBlocked(){const {source,engine_period}=selection();return source==='mm1'&&engine_period==='minimum'||source==='direct_export'&&selection().capture_policy!=='reference';}
  function stageReason(){const {source,engine_period,capture_policy}=selection();
    if(source!=='mm1')return 'RUNTIME GATED · Existing enrolled phone + matching installed APK; driver/period/geometry checked by native source. No physical qualification claimed.';
    if(engine_period==='minimum')return 'BLOCKED · Minimum-period experiments require the DeskFuse exact-endpoint driver gate';
    if(capture_policy==='minimum')return 'STAGED · Smaller WASAPI allocation is not a proven latency reduction. One-factor test only.';
    return 'REFERENCE · MM-1 loopback selected; nothing applied';
  }
  function renderCards(){const root=$('win-cards');root.replaceChildren();
    for(const x of SOURCES){
      const card=item('article','win-card');card.dataset.winSource=x.id;
      const top=item('div','win-card-top');top.append(item('span','win-code',x.short),item('span','win-tag',x.status));
      card.append(top,item('strong','',x.name),item('small','',x.boundary+' / '+x.impacts.join(' · ')),item('p','',x.note));
      card.addEventListener('click',()=>{$('win-source').value=x.id;update();});
      card.tabIndex=0;card.setAttribute('role','button');card.setAttribute('aria-label','Plan '+x.name);
      card.addEventListener('keydown',e=>{if(e.key==='Enter'||e.key===' '){e.preventDefault();$('win-source').value=x.id;update();}});
      root.append(card);
    }
  }
  function renderGates(){const root=$('win-gates');root.replaceChildren();for(const x of GATES){
    const card=item('article','win-gate');card.dataset.state=x.kind;
    const head=item('div','win-gate-head');head.append(item('span','win-gate-code',x.code),item('span','win-gate-state',x.state));
    card.append(head,item('h4','',x.title),item('p','',x.detail));root.append(card);
  }}
  function renderPipeline(){const x=selected();setText('win-path-mode',x.short+' · '+x.status);
    const host=$('win-pipeline');host.replaceChildren();x.steps.forEach((step,i)=>{if(i)host.append(item('span','win-pipeline-arrow','›'));host.append(item('div','win-pipeline-step',step));});
    setText('win-path-note',x.detail);
  }
  function renderEvidence(){const tbody=$('win-evidence-rows');tbody.replaceChildren();
    const source=currentSession?.session?.source;
    for(const metric of METRICS){const tr=item('tr');tr.append(item('th','',metric.title));
      for(const x of SOURCES){const td=item('td');let value='—';
        if(x.id===source&&metric.observed&&Number.isSafeInteger(currentSession?.telemetry?.[metric.observed])){
          value=currentSession.telemetry[metric.observed]+' '+metric.units+' · last session';
        }
        if(x.id==='direct_export'&&metric.id==='commit')value='Enforced internally; per-packet QPC not exported';

        td.textContent=value; if(value==='—')td.className='win-empty';tr.append(td);
      }tbody.append(tr);
    }
  }
  function update(){const x=selection();
    const otherChanges=Number(x.engine_period!=='reference')+Number(x.capture_policy!=='reference');
    const factor=document.getElementById('live-factor')?.value||'baseline';
    setText('win-change-status',stageReason()+(otherChanges>1?' · MULTIPLE WINDOWS FACTORS STAGED — NOT A CONTROLLED PHYSICAL COMPARISON':''));
    document.querySelectorAll('[data-win-source]').forEach(e=>{
      e.classList.toggle('selected',e.dataset.winSource===x.source);
      e.setAttribute('aria-pressed',String(e.dataset.winSource===x.source));
    });
    // Never enable playback here. live.js owns the final user-visible button,
    // and the native bridge independently rejects blocked modes.
    if(uiBlocked()&&$('live-start'))$('live-start').disabled=true;
    renderPipeline();
    const cap=integration?.receipt?.capabilities||{};
    setText('win-identity','PRODUCT ENROLLMENT ↔ PINNED QUIC · RUNTIME');
    setText('win-driver-status',currentSession?.telemetry?.driver_verified===1?'LAST RUN NATIVE DRIVER CHECK PASSED · RECHECKED AT EACH START':'RUNNING DRIVER NOT YET VERIFIED');
    window.dispatchEvent(new CustomEvent('deskfuse-windows-source-changed',{detail:{...x,executable:!uiBlocked(),moreThanOneWindowsFactor:otherChanges>1,otherAudioFactor:factor!=='baseline',reportedCapabilities:cap}}));
  }
  function exportPlan(){const s=selection();const payload={
      schema:'deskfuse.windows-capture-comparison.plan.v1',type:'NON_EXECUTABLE_PLANNING_EXPORT',
      audited_commit:AUDIT,exported_utc:new Date().toISOString(),source:s.source,engine_period:s.engine_period,capture_policy:s.capture_policy,
      reference:{source:'mm1',engine_period:'reference',capture_policy:'reference'},
      launchable:false,comment:'No device enrollment approval, driver identity attestation or physical measurements are included. This file does not authorize launching or modifying Windows.',
      selected_path:selected().steps,gates:GATES.map(g=>({id:g.id,state_at_audit:g.state,detail:g.detail})),
      measurement_schema:METRICS.map(m=>({id:m.id,name:m.title,unit:m.units,measured_value:null}))
    };
    const blob=new Blob([JSON.stringify(payload,null,2)+'\n'],{type:'application/json'}),url=URL.createObjectURL(blob),a=item('a');
    a.href=url;a.download='DeskFuse_Windows_Capture_Plan_'+new Date().toISOString().slice(0,10)+'.json';document.body.append(a);a.click();a.remove();
    setTimeout(()=>URL.revokeObjectURL(url),2000);setText('win-export-note','Saved planning-only JSON. No hardware action was taken.');
  }
  async function refresh(){if(location.protocol==='file:'){
      setText('win-details','Preview only. Run start-lab.ps1 to inspect local artifacts and broker reachability (read-only).');
      setText('win-broker','NOT CHECKED');setText('win-receipt','NOT CHECKED');return;
    }
    try{
      const response=await fetch('/api/windows-integration',{cache:'no-store'});if(!response.ok)throw Error('Bridge unavailable');
      const data=await response.json();integration=data;
      setText('win-broker',(data.broker?.state||'unknown').toUpperCase());
      setText('win-receipt',(data.receipt?.state||'unknown').toUpperCase());
      const problems=data.receipt?.problems||[],cap=data.receipt?.capabilities||{};
      const binaries=Object.entries(data.receipt?.artifacts||{}).map(([role,b])=>role+': '+(b.matched?'SHA-256 MATCH':'NOT MATCHED')).join('\n');
      setText('win-details',[data.broker?.detail,'Receipt source commit: '+(data.receipt?.source_commit||'unavailable'),
        'Native audio ABI: '+(cap.android_audio_abi??'not recorded'),
        'Windows timing capability listed: '+(JSON.stringify(cap.windows_engine_period_policies??[])),
        'Driver identity is checked at source runtime, never inferred from this receipt',
        'Product receipt: '+(data.product_receipt?.state||'not checked'),
        ...Object.entries(data.product_receipt?.artifacts||{}).map(([role,b])=>role+': '+(b.matched?'SHA-256 MATCH':'NOT MATCHED')+' / '+b.profile),
        'Product renderer profile: '+(data.product_receipt?.capabilities?.product_renderer_profile||'not recorded'),
        ...(data.product_receipt?.problems||[]).map(v=>'PRODUCT BLOCKED: '+v),binaries,
        ...problems.map(v=>'BLOCKED: '+v),
        'Native implementation checkpoint '+AUDIT+'. Physical qualification remains outstanding.'].filter(Boolean).join('\n'));
      update();
      try{const obs=await fetch('/api/status',{cache:'no-store'});if(obs.ok){currentSession=await obs.json();
        const tel=currentSession.telemetry||{};
        setText('win-observed-period',Number.isSafeInteger(tel.current_period_frames)?
          'Reported period: '+tel.current_period_frames+' frames; allocated: '+(tel.allocated_frames??'unknown')+' frames; last source: '+(currentSession.session?.source||'unknown')+' (NOT acoustic latency)' + (currentSession.endpoint_identity?' · endpoint: '+currentSession.endpoint_identity:''):'Actual engine period unavailable in this session; no latency inferred.');
        renderEvidence();}}
      catch(_){}
    }catch(_){integration=null;currentSession=null;setText('win-broker','OFFLINE');setText('win-receipt','OFFLINE');
      setText('win-details','Local read-only integration bridge unavailable. Planning controls remain accessible; no evidence is asserted.');renderEvidence();update();
    }
  }
  document.addEventListener('DOMContentLoaded',()=>{
    renderCards();renderGates();renderEvidence();
    for(const id of ['win-source','win-engine-period','win-capture-policy'])$(id).addEventListener('change',update);
    $('win-export-plan').addEventListener('click',exportPlan);
    update();refresh();setInterval(refresh,5000);
  });
})();
