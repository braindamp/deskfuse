/* DeskFuse v0.5 configuration atlas. Draft-only state: NEVER calls /api/start. */
'use strict';
(() => {
  const catalog = window.DESKFUSE_ATLAS;
  if (!catalog || !Array.isArray(catalog.controls)) return;
  const $ = id => document.getElementById(id);
  const key = 'deskfuse.audio_lab.atlas.v0.5';
  const phases = {
    live: {name:'Potential live update',short:'LIVE',hint:'After an explicitly built runtime control channel; not proof that one exists now.',color:'#60dbbb',rank:0},
    prime: {name:'Re-prime receiver',short:'RE-PRIME',hint:'Reset and safely prime receiver buffers before the new policy takes effect.',color:'#c1ee7f',rank:1},
    stream: {name:'Reopen media stream',short:'REOPEN',hint:'AAudio/WASAPI stream must be reopened; audible interruption possible.',color:'#f1c172',rank:2},
    session: {name:'Restart / renegotiate session',short:'RESTART',hint:'Run another verified physical trial or start a compatible network session.',color:'#d2a1ee',rank:3},
    build: {name:'Rebuild / redeploy code',short:'BUILD',hint:'Fixed in current code; no supported runtime setting. Native/protocol work required.',color:'#f2a2a2',rank:4},
    future: {name:'Research / new capability',short:'ROADMAP',hint:'Does not exist on the checked physical path. Architectural work is required.',color:'#96a3b0',rank:5}
  };
  const statuses={physical:'RUNNER SUPPORTED',fixed:'FIXED IN CODE',observed:'TELEMETRY',proposed:'PROPOSED'};
  const sceneNames={all:'ALL MODES',local:'LOCAL WI-FI',cellular:'CELLULAR / WAN',video:'VIDEO + A/V',duplex:'FULL DUPLEX',reverse:'REVERSE LINK',p2p:'PEER TO PEER'};
  const impactNames={latency:'LATENCY',stability:'CONTINUITY',fidelity:'FIDELITY',bandwidth:'BANDWIDTH',power:'POWER',cpu:'CPU / HEAT',sync:'SYNCHRONIZATION',privacy:'PRIVACY',security:'SECURITY',cost:'DATA COST',memory:'MEMORY'};
  let plan={}, filters={scope:'all',impact:'all',phase:'all',status:'all',query:''}, expanded=new Set(['Android output & hardware route','Native DSP, drift & recovery']);
  const byId=new Map(catalog.controls.map(x=>[x.id,x]));
  function valid(c,value){
    if (c.kind==='readout') return false;
    if(c.kind==='select')return c.options.includes(String(value));
    if(c.kind==='range'){
      const [min,max,step]=c.options;let x=Number(value);
      return Number.isFinite(x)&&x>=min&&x<=max&&Math.abs((x-min)/step-Math.round((x-min)/step))<0.00001;
    }
    return false;
  }
  function baseline(c){return String(c.baseline)}
  function changed(){return Object.entries(plan).filter(([id,v])=>byId.has(id)&&valid(byId.get(id),v)&&String(v)!==baseline(byId.get(id)))}
  function load(){try{const v=JSON.parse(localStorage.getItem(key)||'{}');if(v&&typeof v==='object')for(const [id,value]of Object.entries(v))if(byId.has(id)&&valid(byId.get(id),value)&&String(value)!==baseline(byId.get(id)))plan[id]=String(value)}catch(_){}}
  function save(){try{localStorage.setItem(key,JSON.stringify(plan))}catch(_){}}
  function el(tag,cls,text){const n=document.createElement(tag);if(cls)n.className=cls;if(text!==undefined)n.textContent=text;return n}
  function button(label,cls,handler){const n=el('button',cls,label);n.type='button';n.addEventListener('click',handler);return n}
  function notify(message){$('atlas-messages').textContent=message}
  function tags(parent,c){const t=el('div','atlas-card-badges');const apply=el('span','atlas-apply-badge',phases[c.apply].short);apply.style.setProperty('--phase-color',phases[c.apply].color);apply.title=phases[c.apply].hint;t.append(apply);c.impacts.forEach(x=>t.append(el('span','atlas-effect',impactNames[x]||x.toUpperCase())));parent.append(t)}
  function makeControl(c){
    const card=el('article','atlas-card');card.style.setProperty('--phase-color',phases[c.apply].color);
    if(Object.hasOwn(plan,c.id))card.classList.add('changed');
    const head=el('div','atlas-card-header');const title=el('div');title.append(el('span','atlas-ref',c.id.toUpperCase().replaceAll('_',' / ')));title.append(el('h4','',c.title));head.append(title);const status=el('span','atlas-status',statuses[c.status]);status.dataset.status=c.status;head.append(status);card.append(head);
    card.append(el('p','atlas-card-desc',c.detail));tags(card,c);
    const box=el('div','atlas-config');const note=el('label');note.textContent=c.kind==='readout'?'REPORTED STATE':c.status==='proposed'?'ILLUSTRATIVE CONFIGURATION':'SOURCE / REFERENCE';box.append(note);
    if(c.kind==='readout'){
      box.append(el('output','',c.baseline));
    }else{
      const id='atlas-input-'+c.id;let input;
      if(c.kind==='select'){
        input=el('select');c.options.forEach(v=>{const op=el('option','',v);op.value=v;input.append(op)});input.value=plan[c.id]??baseline(c);
      }else{
        input=el('input');input.type='range';input.min=c.options[0];input.max=c.options[1];input.step=c.options[2];input.value=plan[c.id]??baseline(c);
        const out=el('output','',input.value+' '+c.options[3]);out.htmlFor=id;
        const h=el('label');h.textContent='PLAN VALUE';h.append(out);box.append(h);
        input.addEventListener('input',()=>out.textContent=input.value+' '+c.options[3]);
      }
      input.id=id;input.setAttribute('aria-label',c.title);
      input.addEventListener('change',()=>{
        const v=String(input.value);if(v===baseline(c))delete plan[c.id];else plan[c.id]=v;
        save();card.classList.toggle('changed',Object.hasOwn(plan,c.id));updatePlan();
      });
      box.append(input);
    }
    card.append(box);
    const foot=el('div','atlas-card-foot');foot.append(el('span','',c.status==='proposed'?'DRAFT VALUE ≠ CURRENT DEVICE':c.status==='physical'?'NEXT PHYSICAL TRIAL':c.status==='observed'?'OBSERVATION ONLY':'SOURCE-LEVEL PROPERTY'));
    if(c.status==='physical'){
      foot.append(button('SET UP LISTENING TRIAL ↗','',()=>stagePhysical(c)));
      foot.lastChild.dataset.physical='true';
    }else if(Object.hasOwn(plan,c.id)){
      const hint=el('em','', 'PLANNED ONLY');foot.append(hint);
    }
    card.append(foot);return card;
  }
  function stagePhysical(c){
    // Physical runner only supports single-factor 0/2/3 buffer and ON/OFF DSP.
    const diffs=changed();
    if(diffs.length!==1||diffs[0][0]!==c.id){notify('Physical trial staging requires EXACTLY ONE changed option. Restore your plan and isolate this factor first.');return}
    const val=diffs[0][1];
    const factor=c.id==='android_buffer'?(val==='2 bursts'?'b2':val==='3 bursts'?'b3':null):
      c.id==='dsp_fade_on'&&val==='Off'?'fade':
      c.id==='dsp_conceal_on'&&val==='Off'?'conceal':
      c.id==='dsp_sinc'&&val==='Identity bypass'?'bypass':null;
    if(!factor){notify('This combination is not supported by the existing physical runner. No audio has changed.');return}
    const selector=$('live-factor');if(!selector){notify('Physical trial selector unavailable.');return}
    selector.value=factor;selector.dispatchEvent(new Event('change',{bubbles:true}));
    notify('Staged '+c.title+' → '+val+'. Physical playback has NOT started. Review the session and press START PHYSICAL TEST separately.');
    $('live-lab')?.scrollIntoView({behavior:'smooth',block:'start'});
  }
  function matched(c){return (filters.scope==='all'||c.scopes.includes(filters.scope))&&(filters.phase==='all'||c.apply===filters.phase)&&(filters.status==='all'||c.status===filters.status)&&(filters.impact==='all'||c.impacts.includes(filters.impact))&&(!filters.query||[c.title,c.id,c.group,c.detail,c.apply,c.status,...c.scopes,...c.impacts].join(' ').toLowerCase().includes(filters.query))}
  function render(){
    const host=$('atlas-groups');host.replaceChildren();const found=catalog.controls.filter(matched);
    const groups=new Map();for(const c of found){if(!groups.has(c.group))groups.set(c.group,[]);groups.get(c.group).push(c)}
    $('atlas-visible').textContent=found.length+' / '+catalog.controls.length+' OPTIONS VISIBLE';
    if(!groups.size){host.append(el('div','atlas-empty','No settings match these filters. Clear filters to see the full catalog.'));return}
    for(const [name,group]of groups){const details=el('details','atlas-group');details.open=expanded.has(name)||filters.query!==''||filters.phase!=='all'||filters.status!=='all'||filters.impact!=='all';const summary=el('summary');const title=el('span','atlas-group-title',name);title.append(el('small','',group.reduce((a,x)=>a+Number(x.status==='physical'),0)+' CURRENT RUNNER CONTROLS · '+group.reduce((a,x)=>a+Number(x.status==='proposed'),0)+' DESIGN ITEMS'));summary.append(title);summary.append(el('span','atlas-group-count',group.length+' SETTINGS'));details.append(summary);const grid=el('div','atlas-cards');group.forEach(c=>grid.append(makeControl(c)));details.append(grid);details.addEventListener('toggle',()=>{if(!filters.query&&filters.phase==='all'&&filters.status==='all'&&filters.impact==='all'){if(details.open)expanded.add(name);else expanded.delete(name)}});host.append(details)}
  }
  function updatePlan(){const diff=changed();$('atlas-changed').textContent=diff.length;const applies=diff.map(([id])=>byId.get(id).apply);const hardest=applies.map(x=>phases[x]).sort((a,b)=>b.rank-a.rank)[0];$('atlas-apply-cost').textContent=hardest?'HIGHEST CHANGE BOUNDARY: '+hardest.short:'NO CHANGES STAGED';
    const proposed=diff.filter(([id])=>byId.get(id).status==='proposed').length;
    $('atlas-plan-note').textContent=proposed?proposed+' proposal(s) do not exist in the physical runner. Export only; no native changes.':diff.length?'This is a local planning draft. Changes never reach the device until a separately supported physical session is launched.':'The reference/seed values are unchanged. No physical parameter has been applied.';
  }
  function exportPlan(){const payload={schema:'deskfuse.audio-lab.configuration-atlas.v1',at:new Date().toISOString(),notes:'Planning-only. Not a native config or executable test plan.',reference:catalog.reference,changes:changed().map(([id,value])=>({id,title:byId.get(id).title,baseline:baseline(byId.get(id)),value,apply:byId.get(id).apply,status:byId.get(id).status,scopes:byId.get(id).scopes}))};const blob=new Blob([JSON.stringify(payload,null,2)],{type:'application/json'});const url=URL.createObjectURL(blob);const link=el('a');link.href=url;link.download='DeskFuse_Audio_Lab_Planned_Profile_'+new Date().toISOString().slice(0,10)+'.json';document.body.append(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),2500);notify('Draft profile exported. No native settings were applied.')}
  async function importPlan(file){if(!file)return;try{if(file.size>1024*1024)throw Error('File exceeds 1 MB');const x=JSON.parse(await file.text());if(x.schema!=='deskfuse.audio-lab.configuration-atlas.v1'||!Array.isArray(x.changes)||x.changes.length>catalog.controls.length)throw Error('Not a recognized atlas profile');const next={};for(const item of x.changes){const c=byId.get(item?.id);if(!c||!valid(c,item?.value))throw Error('Invalid setting in imported profile');if(String(item.value)!==baseline(c))next[c.id]=String(item.value)}plan=next;save();updatePlan();render();notify('Imported '+changed().length+' planned changes. Nothing was sent to the physical runner.')}catch(e){notify('Import rejected: '+e.message)}}
  function legend(){const host=$('atlas-legend');for(const [id,phase]of Object.entries(phases)){
      const b=el('button','atlas-lifecycle');b.style.setProperty('--phase-color',phase.color);const dot=el('span','atlas-lifecycle-dot');const body=el('span');body.append(el('strong','',phase.name));body.append(el('small','',phase.hint));b.append(dot,body);b.title='Filter to '+phase.name;b.addEventListener('click',()=>{filters.phase=filters.phase===id?'all':id;$('atlas-phase').value=filters.phase;updateFilters();render()});b.dataset.phase=id;host.append(b);
    }}
  function setFilter(id,field){$(id).addEventListener('change',e=>{filters[field]=e.target.value;updateFilters();render()})}
  function updateFilters(){document.querySelectorAll('.atlas-lifecycle').forEach(x=>x.classList.toggle('active',filters.phase===x.dataset.phase));document.querySelectorAll('.atlas-scene').forEach(x=>x.classList.toggle('active',filters.scope===x.dataset.scene));document.querySelectorAll('[data-atlas-impact]').forEach(x=>x.classList.toggle('active',filters.impact===x.dataset.atlasImpact))}
  function setup(){
    $('atlas-total').textContent=String(catalog.controls.length);$('atlas-nav-total').textContent=String(catalog.controls.length);$('atlas-group-total').textContent=String(new Set(catalog.controls.map(x=>x.group)).size);
    const scenarios=$('atlas-scenarios');for(const [k,v]of Object.entries(sceneNames)){const b=button(v,'atlas-scene',()=>{filters.scope=k;updateFilters();render()});b.dataset.scene=k;scenarios.append(b)}
    const phaseSelect=$('atlas-phase');phaseSelect.append(new Option('ALL CHANGE BOUNDARIES','all'));for(const [id,p]of Object.entries(phases))phaseSelect.append(new Option(p.name,id));
    const statusSelect=$('atlas-status');statusSelect.append(new Option('ALL IMPLEMENTATION STATES','all'));for(const [id,s]of Object.entries(statuses))statusSelect.append(new Option(s,id));
    const impactSelect=$('atlas-impact');impactSelect.append(new Option('ALL IMPACT DIMENSIONS','all'));for(const [id,s]of Object.entries(impactNames))impactSelect.append(new Option(s,id));
    legend();const strip=$('atlas-impact-strip');for(const [id,name]of Object.entries(impactNames)){const b=button(name,'',()=>{filters.impact=filters.impact===id?'all':id;impactSelect.value=filters.impact;updateFilters();render()});b.dataset.atlasImpact=id;strip.append(b)}
    $('atlas-search').addEventListener('input',e=>{filters.query=e.target.value.trim().toLowerCase();render()});
    setFilter('atlas-phase','phase');setFilter('atlas-status','status');setFilter('atlas-impact','impact');
    $('atlas-clear').addEventListener('click',()=>{filters={scope:'all',impact:'all',phase:'all',status:'all',query:''};$('atlas-phase').value='all';$('atlas-status').value='all';$('atlas-impact').value='all';$('atlas-search').value='';updateFilters();render();notify('All filters cleared.')});
    $('atlas-expand').addEventListener('click',()=>document.querySelectorAll('.atlas-group').forEach(x=>x.open=true));
    $('atlas-collapse').addEventListener('click',()=>document.querySelectorAll('.atlas-group').forEach(x=>x.open=false));
    $('atlas-export').addEventListener('click',exportPlan);
    $('atlas-import-trigger').addEventListener('click',()=>$('atlas-import').click());
    $('atlas-import').addEventListener('change',e=>{const f=e.target.files?.[0];importPlan(f);e.target.value='' });
    $('atlas-reset').addEventListener('click',()=>{if(!changed().length)return;if(!confirm('Reset all atlas PLANNING values to their baseline/seed values? Listening notebook and actual physical evidence remain unchanged.'))return;plan={};save();render();updatePlan();notify('Atlas planning draft cleared. Other lab and physical test data unchanged.')});
    load();updateFilters();updatePlan();render();
  }
  if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',setup);else setup();
})();
