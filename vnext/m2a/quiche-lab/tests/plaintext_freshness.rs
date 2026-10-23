//! In-memory authenticated quiche fixture. No flaky UDP or clock sleeps:
//! exercises the real outgoing DATAGRAM queue/purge/seal API.
mod common;
use deskfuse_quiche_lab::plaintext_expiry::{PlaintextExpiry,Identity,QueueAdmission};
use deskfuse_quiche_lab::reactor::Egress;
use deskfuse_quiche_lab::{encode,Header,HEADER_BYTES,VERSION,KIND_AUDIO};
use std::io;
use std::net::SocketAddr;
use std::path::Path;
use std::error::Error;
use std::time::Duration;

type Result<T> = std::result::Result<T,Box<dyn Error>>;
const ALPN:&[u8]=b"df-inmemory-mp001-1";
const MTU:usize=1350;
fn config(path:&Path,server:bool)->Result<quiche::Config>{
    let mut c=quiche::Config::new(quiche::PROTOCOL_VERSION)?;
    c.set_application_protos(&[ALPN])?;
    c.set_max_recv_udp_payload_size(MTU);
    c.set_max_send_udp_payload_size(MTU);
    c.set_max_idle_timeout(30000);
    c.set_initial_max_data(65536);
    c.set_initial_max_stream_data_bidi_local(8192);
    c.set_initial_max_stream_data_bidi_remote(8192);
    c.set_initial_max_streams_bidi(1);
    c.enable_dgram(true,4,4);
    c.verify_peer(true);
    c.load_verify_locations_from_file(path.join("ca.pem").to_str().unwrap())?;
    c.load_cert_chain_from_pem_file(
        path.join(if server{"server.pem"}else{"client.pem"}).to_str().unwrap())?;
    c.load_priv_key_from_pem_file(
        path.join(if server{"server.key"}else{"client.key"}).to_str().unwrap())?;
    Ok(c)
}
fn pump(src:&mut quiche::Connection,dst:&mut quiche::Connection)->Result<usize>{
    let mut buf=[0u8;MTU];
    let mut n_packets=0;
    for _ in 0..32 {
        let (n,info)=match src.send(&mut buf){
            Ok(v)=>v,
            Err(quiche::Error::Done)=>break,
            Err(e)=>return Err(format!("in-memory seal error: {e:?}").into()),
        };
        dst.recv(&mut buf[..n],quiche::RecvInfo{from:info.from,to:info.to})?;
        n_packets+=1;
    }
    Ok(n_packets)
}
fn established_pair()->Result<(quiche::Connection,quiche::Connection)>{
    let temp=tempfile::tempdir()?;
    common::issue_test_identities(temp.path())?;
    let mut cfg_c=config(temp.path(),false)?;
    let mut cfg_s=config(temp.path(),true)?;
    let a:SocketAddr="127.0.0.1:48234".parse()?;
    let b:SocketAddr="127.0.0.1:48235".parse()?;
    let cc=quiche::ConnectionId::from_ref(&[0x65;16]);
    let mut client=quiche::connect(Some("deskfuse-lab.local"),&cc,a,b,&mut cfg_c)?;
    let mut initial=[0u8;MTU];
    let (n,info)=client.send(&mut initial)?;
    let header=quiche::Header::from_slice(&mut initial[..n],quiche::MAX_CONN_ID_LEN)?;
    let mut server=quiche::accept(&header.dcid,None,b,a,&mut cfg_s)?;
    server.recv(&mut initial[..n],quiche::RecvInfo{from:info.from,to:info.to})?;
    for _ in 0..512 {
        pump(&mut server,&mut client)?;
        pump(&mut client,&mut server)?;
        if server.is_established()&&client.is_established(){return Ok((server,client));}
        if server.is_closed()||client.is_closed(){return Err("mTLS quiche handshake closed".into());}
    }
    Err("mTLS quiche handshake failed to establish within bounded service".into())
}
fn block(sequence:u32)->(Identity,Vec<u8>){
    let h=Header{
        version:VERSION,kind:KIND_AUDIO,flags:0,
        header_bytes:HEADER_BYTES as u16,frame_count:240,
        media_epoch:9,source_stream_id:3,format_config_id:1,
        packet_sequence:sequence,first_source_frame:48000+u64::from(sequence)*240,
        payload_bytes:960,clock_anchor_id:17,
    };
    let mut w=encode(&h).to_vec();
    w.extend([0u8;960]);
    (Identity{epoch:9,stream:3,format:1,sequence,source_frame:h.first_source_frame},w)
}
#[test]
fn genuine_quiche_queued_plaintext_expires_before_sealing()->Result<()>{
    let (mut sender,_receiver)=established_pair()?;
    assert!(sender.dgram_max_writable_len().unwrap_or(0)>=1000);
    let mut track=PlaintextExpiry::default();
    for i in 0..4{
        let (_,bytes)=block(i);
        assert_eq!(track.admit(&mut sender,&bytes,100_000,0,100_001)?,QueueAdmission::Queued);
    }
    assert_eq!(sender.dgram_send_queue_len(),4);
    track.purge(&mut sender,600_000); // 50ms age => still fresh
    assert_eq!(sender.dgram_send_queue_len(),4);
    assert_eq!(track.len(),4);
    track.purge(&mut sender,800_001); // 70ms age => expire
    assert_eq!(sender.dgram_send_queue_len(),0);
    assert_eq!(track.expired,4);
    assert_eq!(track.len(),0);
    // Protocol obligations must remain serviceable after media purge.
    let mut buffer=[0u8;MTU];
    match sender.send(&mut buffer){Ok(_)|Err(quiche::Error::Done)=>{},
                                  Err(e)=>return Err(format!("{e:?}").into())}
    Ok(())
}
#[test]
fn seal_is_irreversible_and_metadata_is_reclaimed()->Result<()>{
    let (mut sender,_receiver)=established_pair()?;
    let (_,bytes)=block(1);
    let mut track=PlaintextExpiry::default();
    assert_eq!(track.admit(&mut sender,&bytes,100_000,0,100_001)?,QueueAdmission::Queued);
    track.purge(&mut sender,100_001);
    let mut sealed=[0u8;MTU];
    // A send call can also issue protocol-only packets, so keep servicing
    // until the DATAGRAM was constructed or bounded attempts are exhausted.
    let mut generated=Vec::new();
    for _ in 0..32 {
        match sender.send(&mut sealed) {
            Ok((n,_)) => generated.push(sealed[..n].to_vec()),
            Err(quiche::Error::Done) => break,
            Err(e)=>return Err(format!("{e:?}").into()),
        }
        if sender.dgram_send_queue_len()==0 {break;}
    }
    assert!(!generated.is_empty() && sender.dgram_send_queue_len()==0);
    let ciphertext=generated.last().unwrap().clone();
    track.purge(&mut sender,1_000_000);
    assert_eq!(track.len(),0);
    assert!(track.absent>=1 && track.expired==0,
           "already-sealed ciphertext is outside plaintext expiry purge");
    assert_eq!(*generated.last().unwrap(),ciphertext);
    Ok(())
}
#[test]
fn repeated_scheduling_opportunities_purge_before_construction()->Result<()>{
    let (mut sender,_receiver)=established_pair()?;
    let mut track=PlaintextExpiry::default();
    for attempt in 0..4 {
        let (_,bytes)=block(attempt);
        assert_eq!(track.admit(&mut sender,&bytes,100_000,0,100_001)?,QueueAdmission::Queued);
        // Simulated service path: receive, QUIC timeout, pacing wake,
        // post-stall readiness. ALL must run the same hook before conn.send.
        track.purge(&mut sender,900_000);
        assert_eq!(sender.dgram_send_queue_len(),0);
    }
    assert_eq!(track.expired,4);
    track.clear();
    assert_eq!(track.len(),0);
    Ok(())
}

#[test]
fn blocked_sealed_packet_allows_four_more_plaintexts_and_independent_expiry()->Result<()> {
    let (mut sender,mut receiver)=established_pair()?;
    // Drain handshake/control leftovers through the real library before
    // holding an actual encrypted media packet in the real Egress slot.
    for _ in 0..8 {
        if pump(&mut sender,&mut receiver)?+pump(&mut receiver,&mut sender)?==0 {break;}
    }
    let local:SocketAddr="127.0.0.1:48235".parse()?;
    let mut egress=Egress::default();
    let mut track=PlaintextExpiry::default();
    let (_,first)=block(0);
    assert_eq!(track.admit(&mut sender,&first,100_000,0,100_001)?,QueueAdmission::Queued);
    let mut original_ciphertext=Vec::new();
    egress.service_with_io(local,&mut sender,false,
        |c|{track.purge(c,100_001);Ok(())},
        |bytes,_|{
            original_ciphertext=bytes.to_vec();
            Err(io::Error::from(io::ErrorKind::WouldBlock))
        })?;
    assert!(egress.has_pending_ciphertext() && egress.needs_write_readiness());
    assert_eq!(sender.dgram_send_queue_len(),0,"first audio really constructed");
    let constructed=sender.stats().sent;

    // Regression: five fresh admissions in total, with one sealed and FOUR
    // still plaintext. Previously the fifth enqueue succeeded and the stale
    // metadata entry for the sealed packet made accept() terminate the lab.
    for sequence in 1..=4 {
        let (_,bytes)=block(sequence);
        assert_eq!(track.admit(&mut sender,&bytes,100_000,0,100_001)?,QueueAdmission::Queued);
        egress.service_with_io(local,&mut sender,false,
            |c|{track.purge(c,100_001);Ok(())},
            |_,_|panic!("blocked socket must not be retried without readiness"))?;
    }
    assert_eq!(sender.stats().sent,constructed,"no construction behind sealed slot");
    assert_eq!(sender.dgram_send_queue_len(),4);
    assert_eq!(track.len(),4);
    assert_eq!(track.high_water,4);
    assert!(track.absent>=1,"sealed media no longer occupies plaintext metadata");
    let (_,overflow)=block(5);
    assert_eq!(track.admit(&mut sender,&overflow,100_000,0,100_001)?,QueueAdmission::Full);
    assert_eq!(sender.dgram_send_queue_len(),4,"controlled full drop is atomic");
    assert_eq!(track.len(),4);

    assert_eq!(track.next_expiry_wait(100_001),Some(Duration::from_millis(60)));
    assert_eq!(track.next_expiry_wait(700_000),Some(Duration::from_nanos(100)));
    assert_eq!(track.next_expiry_wait(700_001),Some(Duration::ZERO));
    // Simulate ONLY the one-shot expiry wake: no source event and no socket
    // writability. The production reactor still reconciles stale plaintext.
    egress.service_with_io(local,&mut sender,false,
        |c|{track.purge(c,700_001);Ok(())},
        |_,_|panic!("expiry cleanup cannot send or reconstruct ciphertext"))?;
    assert_eq!(sender.stats().sent,constructed);
    assert!(egress.has_pending_ciphertext());
    assert_eq!(sender.dgram_send_queue_len(),0);
    assert_eq!(track.len(),0);
    assert_eq!(track.expired,4);
    assert_eq!(track.next_expiry_wait(700_001),None,"no idle periodic expiry wake");

    // Diagnostic post-seal policy A is preserved: once writable, retry the
    // EXACT ciphertext, not a reconstructed packet. QUIC/control stays usable.
    let mut transmissions=0;
    egress.service_with_io(local,&mut sender,true,
        |c|{track.purge(c,700_001);Ok(())},
        |bytes,_|{
            if transmissions==0 {assert_eq!(bytes,original_ciphertext.as_slice());}
            transmissions+=1;
            let mut packet=bytes.to_vec();
            receiver.recv(&mut packet,quiche::RecvInfo {
                from:local,to:"127.0.0.1:48234".parse().unwrap(),
            }).map_err(|error|io::Error::other(format!("receive sealed packet: {error:?}")))?;
            Ok(bytes.len())
        })?;
    assert!(transmissions>=1 && !egress.has_pending_ciphertext());
    // Both sides still exchange authenticated reliable protocol work after
    // the blocked packet and stale plaintext purge, without a reconnect.
    sender.stream_send(1,b"alive",true)?;
    for _ in 0..16 {
        pump(&mut sender,&mut receiver)?;
        pump(&mut receiver,&mut sender)?;
    }
    let mut reply=[0u8;16];
    let (n,fin)=receiver.stream_recv(1,&mut reply)?;
    assert_eq!(&reply[..n],b"alive");assert!(fin);
    assert!(!sender.is_closed() && !receiver.is_closed());
    Ok(())
}

#[test]
fn queue_full_rolls_back_metadata_and_expired_offer_is_not_admitted()->Result<()> {
    let (mut sender,_receiver)=established_pair()?;
    // Non-audio protocol DATAGRAMs share quiche's four-item send queue.
    for _ in 0..4 {sender.dgram_send(b"control")?;}
    let mut track=PlaintextExpiry::default();
    let (_,bytes)=block(0);
    assert_eq!(track.admit(&mut sender,&bytes,100_000,0,100_001)?,QueueAdmission::Full);
    assert_eq!(track.len(),0,"failed quiche admission rolls reservation back");
    assert_eq!(sender.dgram_send_queue_len(),4);
    assert_eq!(track.admit(&mut sender,&bytes,100_000,0,700_001)?,QueueAdmission::Expired);
    assert_eq!(track.len(),0);
    assert_eq!(sender.dgram_send_queue_len(),4,"control plaintext is preserved");
    assert_eq!(track.next_expiry_wait(700_001),None);
    Ok(())
}

#[test]
fn proven_wasapi_second_half_passes_real_quiche_queue_and_still_expires()->Result<()> {
    let (mut sender,_receiver)=established_pair()?;
    let mut track=PlaintextExpiry::default();
    let (_,bytes)=block(33);
    let now=2_000_000u64;
    let second_qpc=now+145_650; // 14.565ms ahead; original head +9.565ms
    assert_eq!(
        track.admit(&mut sender,&bytes,second_qpc,0,now)?,
        QueueAdmission::Expired,
        "unproven future timestamp remains strictly rejected"
    );
    assert_eq!(track.len(),0);
    assert_eq!(
        track.admit(&mut sender,&bytes,second_qpc,240,now)?,
        QueueAdmission::Queued,
        "same real PCM with explicit 240-frame native provenance is admitted"
    );
    assert_eq!(sender.dgram_send_queue_len(),1);
    track.purge(&mut sender,now+1000);
    assert_eq!(track.len(),1,"provenance persists across QUIC plaintext purge");
    track.purge(&mut sender,second_qpc+600_001);
    assert_eq!(sender.dgram_send_queue_len(),0);
    assert_eq!(track.len(),0);
    assert_eq!(track.expired,1,"unchanged 60ms stale-source limit still purges");
    Ok(())
}

#[test]
fn authenticated_quic_recovery_challenge_binds_nonce_context_and_fresh_anchor()->Result<()> {
    use deskfuse_quiche_lab::fresh_anchor::{Message,REQUEST,ANCHOR};
    use deskfuse_quiche_lab::receive_frontier::Context;
    let (mut server,mut client)=established_pair()?;
    let request=Message {
        kind:REQUEST,nonce:0x1007,frame:48240,
        context:Context{epoch:1,stream:1,format:1},
    };
    client.dgram_send(&request.encode())?;
    pump(&mut client,&mut server)?;
    let mut received=[0u8;1350];
    let n=server.dgram_recv(&mut received)?;
    assert_eq!(Message::decode(&received[..n]),Some(request));
    // A post-recovery source frame observed AFTER receiving request.
    let response=Message{kind:ANCHOR,frame:48720,..request};
    server.dgram_send(&response.encode())?;
    pump(&mut server,&mut client)?;
    let n=client.dgram_recv(&mut received)?;
    let decoded=Message::decode(&received[..n]).unwrap();
    assert_eq!(decoded,response);
    let matches_active=|r:Message|r.kind==ANCHOR &&
        r.nonce==request.nonce && r.context==request.context &&
        r.frame>=request.frame;
    assert!(matches_active(decoded));
    assert!(!matches_active(Message{nonce:request.nonce-1,..response}));
    assert!(!matches_active(Message{frame:request.frame-240,..response}));
    assert!(!matches_active(Message{
        context:Context{format:2,..response.context},..response
    }));
    Ok(())
}
