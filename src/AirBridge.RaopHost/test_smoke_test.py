import unittest
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

import smoke_test


class SmokeHarnessTests(unittest.IsolatedAsyncioTestCase):
    async def test_uses_production_volume_readiness_instead_of_pre_record_audio_setter(self):
        config = SimpleNamespace(name="Fixture", get_service=lambda protocol: object())
        atv = SimpleNamespace(stream=object(), audio=SimpleNamespace(set_volume=AsyncMock()), close=Mock())
        storage = SimpleNamespace(get_settings=AsyncMock(return_value=SimpleNamespace()))

        async def production_stream(stream, source, volume, ready):
            self.assertIs(stream, atv.stream)
            self.assertEqual(30, volume)
            ready.set()

        timing = smoke_test.stream_client.TimingServer.datagram_received
        control = smoke_test.stream_client.ControlClient.datagram_received
        with patch.object(smoke_test, "install_pyatv_adapter"), \
             patch.object(smoke_test.pyatv, "scan", AsyncMock(return_value=[config])), \
             patch.object(smoke_test.pyatv, "connect", AsyncMock(return_value=atv)), \
             patch.object(smoke_test, "MemoryStorage", return_value=storage), \
             patch.object(smoke_test, "stream_with_initial_volume", production_stream):
            result = await smoke_test.main("Fixture", 2)
        atv.audio.set_volume.assert_not_awaited()
        atv.close.assert_called_once()
        self.assertTrue(result["criteria"]["post_record_ready"])
        self.assertFalse(result["acoustic_output_proven"])
        self.assertIs(timing, smoke_test.stream_client.TimingServer.datagram_received)
        self.assertIs(control, smoke_test.stream_client.ControlClient.datagram_received)

    async def test_discovery_failure_restores_instrumentation(self):
        timing = smoke_test.stream_client.TimingServer.datagram_received
        control = smoke_test.stream_client.ControlClient.datagram_received
        with patch.object(smoke_test, "install_pyatv_adapter"), \
             patch.object(smoke_test.pyatv, "scan", AsyncMock(return_value=[])):
            with self.assertRaises(RuntimeError):
                await smoke_test.main("Missing", 2)
        self.assertIs(timing, smoke_test.stream_client.TimingServer.datagram_received)
        self.assertIs(control, smoke_test.stream_client.ControlClient.datagram_received)


if __name__ == "__main__":
    unittest.main()
