#!/usr/bin/env python3
"""Generate PUBLIC P2.1 test-only wire vectors; never use these keys for pairing."""
import base64
import hashlib
import hmac
from pathlib import Path
from urllib.parse import quote
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.ciphers.aead import AESGCM

ROOT = Path(__file__).resolve().parents[2]
P = ROOT / 'tests/protocol3/protocol-v3-vectors.properties'
host = ec.derive_private_key(int('157da9941100264acf773f9baf1f01f123bb2304608f152ad2f3d649eb92c244',16), ec.SECP256R1())
client = ec.derive_private_key(int('25fc1f40373f4d0b1f89b4f500410ec8fe30ca952911f483b648c67d8572c380',16), ec.SECP256R1())
salt = bytes.fromhex('f7a811628e1554491275baf465f0c3b9240e171aa3357d1490ed49fd70e8bd1e')
spki = lambda k:k.public_key().public_bytes(serialization.Encoding.DER,serialization.PublicFormat.SubjectPublicKeyInfo)
priv = lambda k:k.private_bytes(serialization.Encoding.DER,serialization.PrivateFormat.PKCS8,serialization.NoEncryption())
shared = client.exchange(ec.ECDH(),host.public_key())
prk = hmac.digest(salt,shared,'sha256')
def expand(info, n):
    t = b''; out = b''
    while len(out)<n:
        t = hmac.digest(prk, t + info.encode('ascii') + bytes([1+len(out)//32]), 'sha256')
        out += t
    return out[:n]
c2s=expand('focuswindow:c2s:v3',32)
s2c=expand('focuswindow:s2c:v3',32)
code = (int.from_bytes(expand('focuswindow:confirm:v3',4),'big') & 0x7fffffff)%1000000
up=b'{"type":"hello","version":3}'
down=b'{"type":"ready","version":3}'
def packet(key,counter,plain):
    return counter.to_bytes(8,'big') + AESGCM(key).encrypt(b'\x00'*4+counter.to_bytes(8,'big'),plain,None)
p0=packet(c2s,0,up); p1=packet(c2s,1,b'next'); d0=packet(s2c,0,down)
def b64url(v): return base64.urlsafe_b64encode(v).decode('ascii')
uri='focuswindow://pair?h=192.168.1.24&p=47810&k='+quote(b64url(spki(host)),safe='')+'&s='+quote(b64url(salt),safe='')+'&n=Desk%20PC'
values={
 'protocolVersion':'3','hostPrivatePkcs8Hex':priv(host).hex(),'hostPublicSpkiHex':spki(host).hex(),
 'clientPrivatePkcs8Hex':priv(client).hex(),'clientPublicSpkiHex':spki(client).hex(),
 'pairingSecretHex':salt.hex(),'rawSharedSecretHex':shared.hex(),
 'c2sKeyHex':c2s.hex(),'s2cKeyHex':s2c.hex(),'confirmationCode':f'{code:06d}',
 'hostFingerprint':hashlib.sha256(spki(host)).digest()[:4].hex().upper(),
 'qrUri':uri,
 'clientPlain0Hex':up.hex(),'clientPacket0Hex':p0.hex(),
 'clientPlain1Hex':b'next'.hex(),'clientPacket1Hex':p1.hex(),
 'hostPlain0Hex':down.hex(),'hostPacket0Hex':d0.hex(),
 'hostPlain1Hex':b'ready2'.hex(),'hostPacket1Hex':packet(s2c,1,b'ready2').hex(),
 'corruptedHostPacket1Hex':(packet(s2c,1,b'ready2')[:-1] + bytes([packet(s2c,1,b'ready2')[-1]^1])).hex(),
 'forgedHighCounterHostPacket1Hex':((2**63-2).to_bytes(8,'big')+packet(s2c,1,b'ready2')[8:]).hex(),
 'clientControlWire0Hex':(b'\x01'+len(p0).to_bytes(4,'big')+p0).hex(),
 'hostControlWire0Hex':(b'\x01'+len(d0).to_bytes(4,'big')+d0).hex(),
 'corruptedClientPacket1Hex':(p1[:-1]+bytes([p1[-1]^1])).hex(),
 'forgedHighCounterPacket1Hex':((2**63-2).to_bytes(8,'big')+p1[8:]).hex(),
}
P.write_text('# DeskFuse P2.1 — TEST ONLY, deterministic fixture; NEVER production credentials.\n'
             +'# All hex strings represent exact bytes; AES-256-GCM nonce=00000000||uint64be(counter).\n'
             +'\n'.join(f'{k}={v}' for k,v in values.items())+'\n',encoding="utf-8")
print(f'Generated {len(values)} deterministic protocol-v3 vectors: {P}')
