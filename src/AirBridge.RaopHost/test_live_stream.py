import asyncio
import inspect
import struct
import unittest
from types import SimpleNamespace
from unittest.mock import AsyncMock, Mock, patch

import pyatv.protocols.raop as raop
from pyatv.const import Protocol
from pyatv.core.facade import FacadeStream
from pyatv.protocols.airplay.auth import hap_transient
from pyatv.protocols.airplay.utils import pct_to_dbfs
from pyatv.protocols.raop.audio_source import AudioSource, _to_audio_samples
from pyatv.protocols.raop.stream_client import StreamClient
from pyatv.settings import Settings

from live_stream import (
    DiagnosticToneSource,
    LivePcmSource,
    install_pyatv_adapter,
    pcm_s16be_to_uncompressed_alac,
    s16le_to_airplay,
    stream_with_initial_volume,
)


class LivePcmSourceTests(unittest.TestCase):
    def test_format_is_canonical(self):
        source = LivePcmSource("unused")
        self.assertEqual(44100, source.sample_rate)
        self.assertEqual(2, source.channels)
        self.assertEqual(2, source.sample_size)
        self.assertEqual(0, source.duration)

    def test_adapter_accepts_pre_normalized_audio_sources(self):
        install_pyatv_adapter()
        source = LivePcmSource("unused")
        opened = asyncio.run(raop.open_source(source, 44100, 2, 2))
        self.assertIs(source, opened)

    def test_volume_adapter_preserves_facade_call_signature(self):
        install_pyatv_adapter()
        parameters = inspect.signature(raop.RaopAudio.set_volume).parameters
        self.assertIn("output_device", parameters)
        self.assertIsNone(parameters["output_device"].default)

    def test_adapter_names_sender_in_mac_approval_prompt(self):
        install_pyatv_adapter()
        self.assertEqual("AirBridge", hap_transient._AIRPLAY_HEADERS["X-Apple-Client-Name"])

    def test_s16le_is_converted_to_raop_byte_order(self):
        # -32768, -1, 0, 1, 32767 encoded as little-endian signed 16-bit.
        little_endian = bytes.fromhex("0080 ffff 0000 0100 ff7f")
        self.assertEqual(bytes.fromhex("8000 ffff 0000 0001 7fff"), s16le_to_airplay(little_endian))
        self.assertEqual(_to_audio_samples(little_endian), s16le_to_airplay(little_endian))

    def test_rejects_partial_sample(self):
        with self.assertRaises(ValueError):
            s16le_to_airplay(b"\x01")

    def test_uncompressed_alac_wrapper_matches_reference_bitstream(self):
        pcm = bytes.fromhex("0000 7fff 8000 ffff 1234 abcd")
        encoded = pcm_s16be_to_uncompressed_alac(pcm)
        self.assertEqual(len(pcm) + 4, len(encoded))
        self.assertEqual(self._reference_uncompressed_alac(pcm), encoded)

    def test_uncompressed_alac_rejects_partial_stereo_frame(self):
        with self.assertRaises(ValueError):
            pcm_s16be_to_uncompressed_alac(b"\x00\x01")

    def test_diagnostic_tone_emits_airplay_ordered_stereo_samples(self):
        source = DiagnosticToneSource(0.1)
        frames = asyncio.run(source.readframes(32))
        left = struct.unpack_from(">h", frames, 4)[0]
        right = struct.unpack_from(">h", frames, 6)[0]
        self.assertEqual(left, right)
        self.assertNotEqual(left, 0)

    @staticmethod
    def _reference_uncompressed_alac(pcm):
        bits = []
        for value, count in ((1, 3), (0, 4), (0, 8), (0, 4), (0, 1), (0, 2), (1, 1)):
            bits.extend((value >> shift) & 1 for shift in range(count - 1, -1, -1))
        for value in pcm:
            bits.extend((value >> shift) & 1 for shift in range(7, -1, -1))
        bits.extend((1, 1, 1))
        output = bytearray((len(bits) + 7) // 8)
        for index, bit in enumerate(bits):
            output[index // 8] |= bit << (7 - index % 8)
        return bytes(output)


class InitialVolumeTests(unittest.IsolatedAsyncioTestCase):
    async def test_facade_volume_and_readiness_follow_record_and_volume_response(self):
        install_pyatv_adapter()
        for requested, expected in ((14, 14), (0, 0.01), (-20, 0.01), (120, 100)):
            with self.subTest(initial_volume=requested):
                events = []
                record_started = asyncio.Event()
                release_record = asyncio.Event()
                volume_started = asyncio.Event()
                release_volume = asyncio.Event()
                ready = asyncio.Event()

                async def record():
                    record_started.set()
                    await release_record.wait()
                    events.append("RECORD")

                async def set_parameter(parameter, value):
                    self.assertEqual("volume", parameter)
                    events.append(("SET_VOLUME", float(value)))
                    volume_started.set()
                    await release_volume.wait()

                core = SimpleNamespace(
                    service=SimpleNamespace(credentials=None, password=None, properties={}),
                    takeover=Mock(return_value=Mock()),
                )
                manager = raop.RaopPlaybackManager(core)
                rtsp = SimpleNamespace(
                    connection=SimpleNamespace(remote_ip="127.0.0.1"),
                    record=AsyncMock(side_effect=record),
                    flush=AsyncMock(),
                    set_parameter=AsyncMock(side_effect=set_parameter),
                    teardown=AsyncMock(),
                )
                protocol = SimpleNamespace(start_feedback=AsyncMock(), teardown=Mock())
                client = StreamClient(rtsp, manager.context, protocol, Settings())
                client.control_client = Mock()
                client.timing_server = Mock()
                client.initialize = AsyncMock()
                client._stream_data = AsyncMock()
                # A receiver-advertised volume must not override the requested level.
                client.info["initialVolume"] = -12.0
                manager._stream_client = client
                manager._rtsp = rtsp
                audio = raop.RaopAudio(manager, Mock())
                low_level_stream = raop.RaopStream(core, Mock(), audio, manager)
                facade = FacadeStream(Mock())
                facade.register(low_level_stream, Protocol.RAOP)

                # Keep the real facade, RAOP stream and volume methods; stub UDP
                # creation and PCM delivery so no receiver or network is required.
                with patch.object(client.loop, "create_datagram_endpoint", new=AsyncMock(return_value=(Mock(), None))):
                    playback = asyncio.create_task(stream_with_initial_volume(
                        facade, DiagnosticToneSource(0.01), requested, ready
                    ))
                    try:
                        await asyncio.wait_for(record_started.wait(), timeout=1)
                        self.assertFalse(ready.is_set())
                        self.assertEqual([], events)
                        self.assertAlmostEqual(pct_to_dbfs(expected), manager.context.volume)

                        release_record.set()
                        await asyncio.wait_for(volume_started.wait(), timeout=1)
                        self.assertFalse(ready.is_set())
                        self.assertEqual("RECORD", events[0])
                        self.assertEqual("SET_VOLUME", events[1][0])
                        self.assertAlmostEqual(pct_to_dbfs(expected), events[1][1])

                        release_volume.set()
                        await asyncio.wait_for(playback, timeout=1)
                        self.assertTrue(ready.is_set())
                    finally:
                        playback.cancel()
                        await asyncio.gather(playback, return_exceptions=True)


if __name__ == "__main__":
    unittest.main()
