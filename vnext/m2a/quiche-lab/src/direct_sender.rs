// Child of the existing sender: reuse its transport, wire encoder, freshness
// hooks and authenticated Android recovery protocol.
use super::*;
use deskfuse_quiche_lab::{committed_source::{Source,Setup},fresh_anchor::CommitFence,reactor::WindowsEvents};
struct Recovery {request:AnchorMessage,qpc:u64,at:Instant,fence:Option<CommitFence>,confirmed:Option<u64>}
pub fn run(bind:SocketAddr,allow_ip:IpAddr,ca:&str,cert:&str,key:&str,seconds:u64,peer_id:&str,dll:&str,minimum:bool)->Result<()> {
    use std::io::Write;
    let mut cfg=config(ca,cert,key)?;
    let udp=socket(bind)?;let local=udp.local_addr()?;
    deskfuse_quiche_lab::session_control::register_socket(&udp)?;
    let events=WindowsEvents::new(&udp)?;
    println!("NATIVE_LISTEN {}",local.port());std::io::stdout().flush()?;
    let waiting=Instant::now()+CONNECT_LIMIT;
    let mut connection:Option<quiche::Connection>=None;let mut remote=None;
    let mut authenticated=None;let mut setup:Option<Setup>=None;let mut source:Option<Source>=None;
    let mut recovery:Option<Recovery>=None;let mut nonce_frontier=0;
    let mut egress=Egress::default();let mut pending=PlaintextExpiry::default();
    let mut sequence=0u64;let mut sent=0u64;let mut expired=0u64;let mut dropped=0u64;
    let mut bytes=[0u8;65535];
    let result=(||->Result<()> {loop {
        if deskfuse_quiche_lab::session_control::stopping(){return Ok(())}
        if authenticated.is_none() && Instant::now()>=waiting{return Err("native QUIC authentication deadline".into())}
        if authenticated.is_some_and(|at:Instant|at.elapsed()>=Duration::from_secs(seconds)){return Ok(())}
        if let Some(c)=connection.as_mut(){service_timeout(c);if c.is_closed(){return Err("native QUIC connection retired".into())}}
        for _ in 0..128 {
            let (n,from)=match udp.recv_from(&mut bytes){Ok(p)=>p,Err(e)if e.kind()==io::ErrorKind::WouldBlock=>break,Err(e)=>return Err(e.into())};
            if from.ip()!=allow_ip || remote.is_some_and(|p|p!=from){continue}
            if connection.is_none(){
                let header=match quiche::Header::from_slice(&mut bytes[..n],quiche::MAX_CONN_ID_LEN){Ok(h)if h.version==quiche::PROTOCOL_VERSION=>h,_=>continue};
                connection=Some(quiche::accept(&header.dcid,None,local,from,&mut cfg)?);remote=Some(from);
            }
            let c=connection.as_mut().unwrap();
            match c.recv(&mut bytes[..n],quiche::RecvInfo{from,to:local}){Ok(_)|Err(quiche::Error::Done)=>{},Err(e)=>return Err(e.into())}
        }
        if let Some(c)=connection.as_mut(){
            if c.is_established() && authenticated.is_none(){
                verified(c)?; // exact session leaf checked BEFORE even starting broker setup
                authenticated=Some(Instant::now());
                setup=Some(Setup::start(peer_id.to_owned(),dll.to_owned(),minimum,local));
            }
            if authenticated.is_some(){
                for _ in 0..16 {
                    let n=match c.dgram_recv(&mut bytes){Ok(n)=>n,Err(quiche::Error::Done)=>break,Err(e)=>return Err(e.into())};
                    let request=AnchorMessage::decode(&bytes[..n]).ok_or("unknown native recovery control")?;
                    if request.kind!=fresh_anchor::REQUEST || request.context!=(SourceContext{epoch:1,stream:1,format:1}) || request.nonce==0 {
                        return Err("invalid native recovery context".into())
                    }
                    if request.nonce<nonce_frontier{continue}
                    if request.nonce==nonce_frontier {
                        let prior=recovery.as_ref().ok_or("missing recovery binding")?;
                        if prior.request!=request{return Err("replayed nonce with different recovery frontier".into())}
                        if let Some(frame)=prior.confirmed {
                            let reply=AnchorMessage{kind:fresh_anchor::ANCHOR,frame,..request};
                            match c.dgram_send(&reply.encode()){Ok(())|Err(quiche::Error::Done)=>{},Err(e)=>return Err(e.into())}
                        }
                        continue; // retries NEVER move the original fence or deadline
                    }
                    nonce_frontier=request.nonce;
                    let qpc=source_age::local_qpc_100ns()?;
                    let fence=source.as_mut().map(|s|s.fence(qpc)).transpose()?;
                    pending.clear();pending.purge(c,qpc); // discard all pre-fence plaintext immediately
                    recovery=Some(Recovery{request,qpc,at:Instant::now(),fence,confirmed:None});
                }
                if let Some(preparing)=setup.as_ref(){
                    match preparing.try_take(){
                        Ok(ready)=>{source=Some(ready?);drop(setup.take());
                            if let Some(ref mut request)=recovery {request.fence=Some(source.as_mut().unwrap().fence(request.qpc)?);}},
                        Err(TryRecvError::Empty)=>{},Err(TryRecvError::Disconnected)=>return Err("source setup disappeared".into()),
                    }
                }
                if source.is_none() && authenticated.unwrap().elapsed()>=CONNECT_LIMIT{return Err("direct source setup deadline".into())}
                if let Some(ref mut current)=source {
                    if let Some(ref request)=recovery {
                        if request.confirmed.is_none(){fresh_anchor::require_live(request.at,Instant::now())?;}
                    }
                    for _ in 0..8 { // fixed owner work budget; one 240-frame assembler only
                        let Some(block)=current.next_block()? else {break};
                        let now=source_age::local_qpc_100ns()?;
                        if !matches!(block.timing.classify(now),AgeDecision::Fresh{..}) {expired+=1;continue}
                        if let Some(ref mut request)=recovery {
                            let fence=request.fence.ok_or("recovery without accepted-commit fence")?;
                            if request.confirmed.is_none() {
                                fresh_anchor::require_live(request.at,Instant::now())?;
                                if block.first_frame<request.request.frame ||
                                    !fence.accepts(block.generation,block.min_commit,block.timing.commit_qpc,block.first_frame){dropped+=1;continue}
                                let response=AnchorMessage{kind:fresh_anchor::ANCHOR,frame:block.first_frame,..request.request};
                                match c.dgram_send(&response.encode()) {
                                    Ok(())=>request.confirmed=Some(block.first_frame),
                                    Err(quiche::Error::Done)=>continue,Err(e)=>return Err(e.into()),
                                }
                            }
                        }
                        if sequence>u32::MAX as u64{return Err("native audio sequence exhausted".into())}
                        let wire=captured_packet(sequence as u32,block.first_frame,&block.pcm);sequence+=1;
                        source_egress(&mut egress,&udp,c,false,&mut pending,true)?;
                        match pending.admit_committed(c,&wire,block.timing,source_age::local_qpc_100ns()?)? {
                            QueueAdmission::Queued=>sent+=1,QueueAdmission::Expired=>expired+=1,QueueAdmission::Full=>dropped+=1,
                        }
                        source_egress(&mut egress,&udp,c,false,&mut pending,true)?;
                    }
                    current.arm()?;
                }
            }
            source_egress(&mut egress,&udp,c,false,&mut pending,true)?;
        }
        let expiry=if pending.len()!=0 {
            let at=Instant::now();pending.next_expiry_wait(source_age::local_qpc_100ns()?).and_then(|d|at.checked_add(d))
        }else{None};
        let recover_deadline=recovery.as_ref().filter(|r|r.confirmed.is_none()).map(|r|r.at+Duration::from_millis(fresh_anchor::TIMEOUT_MS));
        let wake=deadline(&[connection.as_ref().and_then(|c|c.timeout_instant()),egress.deadline(),expiry,recover_deadline,
            Some(authenticated.map_or(waiting,|t|t+Duration::from_secs(seconds))),
            if source.is_none(){authenticated.map(|t|t+CONNECT_LIMIT)}else{None}]);
        let (ready,period)=events.wait(egress.needs_write_readiness(),source.as_ref().map(|s|s.event),source.as_ref().map(|s|s.period_event()),wake)?;
        if period {if let Some(ref current)=source{current.service_period()?;}}
        if ready.writable {if let Some(c)=connection.as_mut(){source_egress(&mut egress,&udp,c,true,&mut pending,true)?;}}
    }})();
    // Acquire any completed setup before cleanup; no setup-owned acquisition
    // may outlive transport termination and endpoint release.
    if let Some(preparing)=setup.take(){if let Some(Ok(ready))=preparing.finish(){source=Some(ready);}}
    if let Some(ref mut current)=source{current.stop();}
    pending.clear();drop(egress);drop(connection); // transport retires before endpoint lease
    drop(source);
    eprintln!("DONE committed source queued={sent} expired={expired} dropped={dropped}");
    result
}
