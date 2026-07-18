import asyncio
import inspect
import struct
import unittest

import pyatv.protocols.raop as raop
from pyatv.protocols.raop.audio_source import AudioSource, _to_audio_samples

from live_stream import (
    DiagnosticToneSource,
    LivePcmSource,
    install_pyatv_adapter,
    pcm_s16be_to_uncompressed_alac,
    s16le_to_airplay,
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


if __name__ == "__main__":
    unittest.main()
