import asyncio
import unittest
from unittest.mock import AsyncMock, patch

import host as host_module


class FakeConfig:
    def __init__(self, identifier: str, name: str):
        self.identifier = identifier
        self.name = name


class FakeSource:
    def __init__(self, pipe_name: str):
        self.pipe_name = pipe_name
        self.closed = False

    async def open(self):
        return self

    async def close(self):
        self.closed = True


class FakeStream:
    def __init__(self, events):
        self.events = events

    async def stream_file(self, source, initial_volume=None):
        self.events.append("RECORD")
        if initial_volume is not None:
            self.events.append(("SET_VOLUME", initial_volume))
        await asyncio.Future()


class FakeAudio:
    def __init__(self):
        self.volumes = []

    async def set_volume(self, volume: float):
        self.volumes.append(volume)


class FakeAtv:
    def __init__(self):
        self.events = []
        self.stream = FakeStream(self.events)
        self.audio = FakeAudio()
        self.closed = False

    def close(self):
        self.closed = True


class ConcurrentHostTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.host = host_module.Host()
        self.host.devices = {
            "speakerA": FakeConfig("config-speakerA", "Speaker A"),
            "office": FakeConfig("config-office", "Office"),
        }
        self.atvs = [FakeAtv(), FakeAtv()]
        self.connect_patch = patch.object(host_module.pyatv, "connect", new=AsyncMock(side_effect=self.atvs))
        self.source_patch = patch.object(host_module, "LivePcmSource", FakeSource)
        self.emit = AsyncMock()
        self.emit_patch = patch.object(host_module, "emit", new=self.emit)
        self.connect_patch.start()
        self.source_patch.start()
        self.emit_patch.start()

    async def asyncTearDown(self):
        await self.host.stop_all()
        self.emit_patch.stop()
        self.source_patch.stop()
        self.connect_patch.stop()

    async def test_sessions_start_and_stop_independently(self):
        await asyncio.gather(
            self.host.start("speakerA", None, "pipe-speakerA"),
            self.host.start("office", None, "pipe-office"),
        )
        await asyncio.gather(*(
            asyncio.wait_for(session.stream_ready.wait(), timeout=1)
            for session in self.host.sessions.values()
        ))
        await asyncio.sleep(0)

        self.assertEqual({"speakerA", "office"}, set(self.host.sessions))
        state_events = [call.args[0] for call in self.emit.await_args_list if call.args[0].get("event") == "state"]
        self.assertTrue(any(event["receiver_id"] == "speakerA" and event["state"] == "streaming" for event in state_events))
        self.assertTrue(any(event["receiver_id"] == "office" and event["state"] == "streaming" for event in state_events))

        result = await self.host.stop("speakerA")

        self.assertTrue(result["was_active"])
        self.assertEqual({"office"}, set(self.host.sessions))
        self.assertTrue(self.atvs[0].closed)
        self.assertFalse(self.atvs[1].closed)
        self.assertTrue(any(
            call.args[0].get("receiver_id") == "speakerA" and call.args[0].get("state") == "idle"
            for call in self.emit.await_args_list
        ))

    async def test_volume_is_receiver_scoped(self):
        await self.host.start("speakerA", None, "pipe-speakerA")
        await self.host.start("office", None, "pipe-office")

        result = await self.host.set_volume(37, "office")

        self.assertEqual({"receiver_id": "office", "volume": 37}, result)
        self.assertEqual([], self.atvs[0].audio.volumes)
        self.assertEqual([37.0], self.atvs[1].audio.volumes)
        with self.assertRaisesRegex(RuntimeError, "receiver_id is required"):
            await self.host.set_volume(50)

    async def test_initial_volume_is_applied_only_after_record(self):
        await self.host.start("speakerA", None, "pipe-speakerA", initial_volume=30)
        await asyncio.wait_for(self.host.sessions["speakerA"].stream_ready.wait(), timeout=1)

        self.assertEqual(["RECORD", ("SET_VOLUME", 30.0)], self.atvs[0].events)
        self.assertEqual([], self.atvs[0].audio.volumes)
        self.assertEqual(30, self.host.sessions["speakerA"].desired_volume)

    async def test_early_live_volume_waits_for_record_readiness(self):
        release_record = asyncio.Event()

        async def delayed_stream(stream, source, initial_volume, ready_event):
            await release_record.wait()
            ready_event.set()
            await asyncio.Future()

        with patch.object(host_module, "stream_with_initial_volume", new=delayed_stream):
            await self.host.start("speakerA", None, "pipe-speakerA", initial_volume=14)
            volume_change = asyncio.create_task(self.host.set_volume(10, "speakerA"))
            await asyncio.sleep(0)
            await asyncio.sleep(0)

            self.assertFalse(volume_change.done())
            self.assertEqual([], self.atvs[0].audio.volumes)
            self.assertFalse(any(
                call.args[0].get("state") == "streaming"
                for call in self.emit.await_args_list
            ))

            release_record.set()
            result = await asyncio.wait_for(volume_change, timeout=1)
            for _ in range(10):
                if any(call.args[0].get("state") == "streaming" for call in self.emit.await_args_list):
                    break
                await asyncio.sleep(0)

            self.assertEqual({"receiver_id": "speakerA", "volume": 10}, result)
            self.assertEqual([10.0], self.atvs[0].audio.volumes)
            self.assertTrue(any(
                call.args[0].get("state") == "streaming"
                for call in self.emit.await_args_list
            ))

    async def test_legacy_untargeted_commands_work_for_single_session(self):
        await self.host.start("speakerA", None, "pipe-speakerA")

        await self.host.set_volume(64)
        result = await self.host.stop()

        self.assertEqual([64.0], self.atvs[0].audio.volumes)
        self.assertEqual(["speakerA"], result["receiver_ids"])
        self.assertEqual({}, self.host.sessions)


if __name__ == "__main__":
    unittest.main()
