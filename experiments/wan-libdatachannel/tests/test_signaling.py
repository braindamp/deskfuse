"""Run: python -m unittest discover -s experiments/wan-libdatachannel/tests -v"""
import asyncio
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from websockets.asyncio.client import connect
from websockets.asyncio.server import serve
from websockets.exceptions import ConnectionClosedError
from signaling import Rendezvous

TOKEN = "a-test-token-that-is-longer-than-thirty-two-chars"


class RendezvousTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.service = Rendezvous(TOKEN)
        self.server = await serve(self.service.handle, "127.0.0.1", 0)
        self.url = f"ws://127.0.0.1:{self.server.sockets[0].getsockname()[1]}"

    async def asyncTearDown(self):
        self.server.close()
        await self.server.wait_closed()

    async def test_connect_route_and_cleanup(self):
        async with connect(self.url) as host, connect(self.url) as phone:
            await host.send(f"join|test-room|host|{TOKEN}")
            self.assertEqual(await host.recv(), "joined")
            await phone.send(f"join|test-room|phone|{TOKEN}")
            self.assertEqual(await phone.recv(), "joined")
            self.assertEqual(await host.recv(), "ready")
            self.assertEqual(await phone.recv(), "ready")
            await host.send("offer|YWJj")
            self.assertEqual(await phone.recv(), "offer|YWJj")
            await phone.send("answer|ZGVm")
            self.assertEqual(await host.recv(), "answer|ZGVm")
        await asyncio.sleep(0)
        self.assertEqual(self.service.rooms, {})

    async def test_authentication_and_single_role(self):
        async with connect(self.url) as client:
            await client.send("join|test-room|host|wrong")
            with self.assertRaises(ConnectionClosedError):
                await client.recv()
        async with connect(self.url) as host, connect(self.url) as duplicate:
            await host.send(f"join|test-room|host|{TOKEN}")
            self.assertEqual(await host.recv(), "joined")
            await duplicate.send(f"join|test-room|host|{TOKEN}")
            with self.assertRaises(ConnectionClosedError):
                await duplicate.recv()

    async def test_rejects_non_signaling_messages(self):
        async with connect(self.url) as host:
            await host.send(f"join|test-room|host|{TOKEN}")
            self.assertEqual(await host.recv(), "joined")
            await host.send("media|secret")
            with self.assertRaises(ConnectionClosedError):
                await host.recv()


if __name__ == "__main__":
    unittest.main()
