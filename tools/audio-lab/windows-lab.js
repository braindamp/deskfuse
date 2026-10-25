/* Window-source comparisons, read-only. Physical start remains in live.js. */
'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const SOURCES = [
    {id:'mm1',name:'MM-1 / WASAPI loopback',status:'RUNNER SUPPORTED',change:'SESSION RESTART',impacts:['LATENCY','STABILITY'],detail:'Protected reference. Music must actually render to MM-1.'},
    {id:'deskfuse',name:'DeskFuse virtual endpoint / loopback',status:'AUTH BINDING REQUIRED',change:'ENDPOINT + SESSION',impacts:['LATENCY','ROUTING','STABILITY'],detail:'Native server-deskfuse exists. A user-supplied peer hash cannot authorize it; actual enrollment and mTLS must match.'},
    {id:'direct_export',name:'WaveRT accepted-PCM export',status:'NOT IMPLEMENTED',change:'DRIVER + REDEPLOY',impacts:['LATENCY','CPU'],detail:'Planned comparison on the SAME own endpoint and engine period, not implemented in this checkpoint.'}
  ];
  let last=null;
  const text=(id,s)=>{const e=$(id);if(e)e.textContent=s};
  function update(){
    const src=$('win-source').value, period=$('win-engine-period').value, capture=$('win-capture-policy').value;
    const differences=Number(src!=='mm1')+Number(period!=='reference')+Number(capture!=='reference');
    text('win-change-status',src==='deskfuse'?'DEDICATED PATH LOCKED · No trusted enrollment-to-mTLS binding':differences>1?'MORE THAN ONE WINDOWS FACTOR CHANGED · PLAN ONLY':differences===0?'PROTECTED MM-1 REFERENCE · No changes applied':'STAGED FOR A SEPARATE VERIFIED SESSION · Not applied yet');
    const start=$('live-start');if(start&&(src!=='mm1'||last?.running)) start.disabled=true;
    if(src==='mm1'&&last)start.disabled=!(last.receipt?.state==='verified'&&last.broker?.state!=='not_checked'&&last.bridgeReady);
    [...document.querySelectorAll('[data-win-source]')].forEach(e=>e.classList.toggle('selected',e.dataset.winSource===src));
    window.dispatchEvent(new CustomEvent('deskfuse-windows-source-changed',{detail:{source:src,period,capture}}));
  }
  function cards(){const host=$('win-cards');for(const x of SOURCES){const card=document.createElement('div');card.className='win-card';card.dataset.winSource=x.id;
      const title=document.createElement('strong');title.textContent=x.name;
      const status=document.createElement('span');status.className='win-tag';status.textContent=x.status;
      const meta=document.createElement('small');meta.textContent=x.change+' / '+x.impacts.join(' · ');
      const body=document.createElement('p');body.textContent=x.detail;
      card.append(title,status,meta,body);host.append(card)}}
  async function refresh(){
    if(location.protocol==='file:'){text('win-details','File preview only. Run start-lab.ps1 for real Windows read-only checks.');return}
    try{const res=await fetch('/api/windows-integration',{cache:'no-store'});if(!res.ok)throw Error('Bridge unavailable');const data=await res.json();
      last={...data,bridgeReady:true};text('win-broker',data.broker.state.toUpperCase());text('win-receipt',data.receipt.state.toUpperCase());
      text('win-identity','NOT BOUND · Dedicated launch blocked');
      const problems=data.receipt.problems||[];const cap=data.receipt.capabilities||{};
      const binary=Object.entries(data.receipt.artifacts||{}).map(([k,v])=>k+': '+(v.matched?'SHA256 MATCH':'MISMATCH')).join('\n');
      text('win-details',[data.broker.detail,'Receipt commit: '+(data.receipt.source_commit||'unavailable'),
        'Native ABI: '+(cap.android_audio_abi??'not recorded'),'Capabilities: '+JSON.stringify(cap),binary,
        ...problems.map(p=>'BLOCKED: '+p),'Dedicated endpoint cannot launch until the separate trusted enrollment binding is supplied.'].filter(Boolean).join('\n'));
      update();
    }catch(_){last=null;text('win-broker','OFFLINE');text('win-receipt','OFFLINE');text('win-details','Local bridge unavailable. Planning-only controls remain functional.');update()}
  }
  document.addEventListener('DOMContentLoaded',()=>{cards();['win-source','win-engine-period','win-capture-policy'].forEach(id=>$(id).addEventListener('change',update));update();refresh();setInterval(refresh,5000)});
})();
