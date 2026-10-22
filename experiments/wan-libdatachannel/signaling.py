"""Disposable two-peer WebSocket rendezvous for a DeskFuse connectivity experiment.

NOT production signaling: bearer token grants access, but this server can rewrite
SDP/fingerprints. Require separate end-to-end peer authentication before any media.
Bind to localhost and terminate internet TLS at a trusted reverse proxy.
"""
from __future__ import annotations

import argparse
import asyncio
import hmac
import os
import re
from dataclasses import dataclass, field

from websockets.asyncio.server import ServerConnection, serve
from websockets.exceptions import ConnectionClosed

ROOM = re.compile(r"[A-Za-z0-9_-]{8,64}\\Z")
ROLES = frozenset(("host", "phone"))
FORWARDED = frozenset(("offer", "answer", "candidate"))
MAX_MESSAGE = 256 * 1024


@dataclass
class Room:
    peers: dict[str, ServerConnection] = field(default_factory=dict)


class Rendezvous:
    def __init__(self, token: str):
        if len(token) < 32:
            raise ValueError("DESKFUSE_PROBE_TOKEN must have at least 32 characters")
        self.token = token
        self.rooms: dict[str, Room] = {}

    async def handle(self, connection: ServerConnection) -> None:
        room_name = role = None
        try:
            registration = await asyncio.wait_for(connection.recv(), timeout=10)
            if not isinstance(registration, str):
                await connection.close(code=1008, reason="text required")
                return
            fields = registration.split("|", 3)
            if len(fields) != 4 or fields[0] != "join":
                await connection.close(code=1008, reason="join required")
                return
            _, room_name, role, supplied_token = fields
            if not ROOM.fullmatch(room_name) or role not in ROLES or not hmac.compare_digest(supplied_token, self.token):
                await connection.close(code=1008, reason="unauthorized")
                return
            room = self.rooms.setdefault(room_name, Room())
            if role in room.peers:
                await connection.close(code=1008, reason="role already occupied")
                return
            room.peers[role] = connection
            await connection.send("joined")
            if len(room.peers) == 2:
                await asyncio.gather(*(client.send("ready") for client in room.peers.values()))
            async for message in connection:
                if not isinstance(message, str) or len(message) > MAX_MESSAGE:
                    await connection.close(code=1009, reason="message too large")
                    break
                kind = message.partition("|")[0]
                if kind not in FORWARDED or "|" not in message:
                    await connection.close(code=1008, reason="invalid signal")
                    break
                other = room.peers.get("phone" if role == "host" else "host")
                if other is not None:
                    try:
                        await other.send(message)
                    except ConnectionClosed:
                        break
        except (TimeoutError, ConnectionClosed):
            pass
        finally:
            if room_name is not None and role is not None:
                room = self.rooms.get(room_name)
                if room is not None and room.peers.get(role) is connection:
                    del room.peers[role]
                    other = next(iter(room.peers.values()), None)
                    if other is not None:
                        try:
                            await other.send("peer_left")
                        except ConnectionClosed:
                            pass
                    if not room.peers:
                        del self.rooms[room_name]


async def run(host: str, port: int, token: str) -> None:
    service = Rendezvous(token)
    async with serve(service.handle, host, port, max_size=MAX_MESSAGE, compression=None, ping_interval=20):
        print(f"DeskFuse disposable signaling rendezvous listening at {host}:{port}", flush=True)
        await asyncio.Future()


def main() -> None:
    parser = argparse.ArgumentParser(description="DeskFuse WAN connectivity test only")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args()
    token = os.environ.get("DESKFUSE_PROBE_TOKEN", "")
    if not token:
        parser.error("set DESKFUSE_PROBE_TOKEN (random 32+ characters)")
    asyncio.run(run(args.host, args.port, token))


if __name__ == "__main__":
    main()
