//! In-memory authenticated quiche fixture. No flaky UDP or clock sleeps:
//! exercises the real outgoing DATAGRAM queue/purge/seal API.
mod common;
use deskfuse_quiche_lab::plaintext_expiry::{PlaintextExpiry,Identity};
use deskfuse_quiche_lab::{encode,Header,HEADER_BYTES,VERSION,KIND_AUDIO};
use std::net::SocketAddr;
use std::path::Path;
use std::error::Error;

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
    (Identity{epoch:9,stream:3,sequence,source_frame:h.first_source_frame},w)
}
#[test]
fn genuine_quiche_queued_plaintext_expires_before_sealing()->Result<()>{
    let (mut sender,_receiver)=established_pair()?;
    assert!(sender.dgram_max_writable_len().unwrap_or(0)>=1000);
    let mut track=PlaintextExpiry::default();
    for i in 0..4{
        let (id,bytes)=block(i);
        sender.dgram_send(&bytes)?;
        track.accept(id,100_000)?;
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
    let (id,bytes)=block(1);
    sender.dgram_send(&bytes)?;
    let mut track=PlaintextExpiry::default();
    track.accept(id,100_000)?;
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
        let (id,bytes)=block(attempt);
        sender.dgram_send(&bytes)?;
        track.accept(id,100_000)?;
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
