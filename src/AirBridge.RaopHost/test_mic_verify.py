import math
import struct
import unittest
from contextlib import nullcontext
from unittest.mock import AsyncMock, patch
import io
import json
import asyncio
import threading
from types import SimpleNamespace

from mic_verify import EXPECTED_FREQUENCY, analyze, main, verify


class MicrophoneVerifierTests(unittest.TestCase):
    def test_non_bin_centred_reference_tone_is_classified_clean(self):
        sample_rate = 16000
        seconds = 4
        pcm = bytearray()
        for index in range(sample_rate * seconds):
            value = round(2500 * math.sin(2 * math.pi * EXPECTED_FREQUENCY * index / sample_rate))
            pcm.extend(struct.pack("<h", value))

        result = analyze(bytes(pcm), sample_rate, 0, seconds)

        self.assertEqual("clean_tone", result["classification"])
        self.assertAlmostEqual(EXPECTED_FREQUENCY, result["dominant_frequency_hz"], delta=0.5)

    def test_non_clean_acoustic_result_returns_failure_exit_status(self):
        with patch("mic_verify.verify", AsyncMock(return_value={"classification": "not_clean_tone"})), \
             patch("mic_verify.hardware_lease", return_value=nullcontext()), \
             patch("sys.stdout", new_callable=io.StringIO) as output:
            self.assertEqual(1, main(["--device", "synthetic", "--target", "fixture"]))
            report = json.loads(output.getvalue())
        self.assertEqual("failed", report["status"])
        self.assertFalse(report["acoustic_output_proven"])

    def test_clean_tone_returns_success_and_explicit_acoustic_criterion(self):
        with patch("mic_verify.verify", AsyncMock(return_value={"classification": "clean_tone"})), \
             patch("mic_verify.hardware_lease", return_value=nullcontext()), \
             patch("sys.stdout", new_callable=io.StringIO) as output:
            self.assertEqual(0, main(["--device", "synthetic", "--target", "fixture"]))
            report = json.loads(output.getvalue())
        self.assertTrue(report["acoustic_output_proven"])
        self.assertEqual(0.25, report["criteria"]["minimum_tone_energy_fraction"])

    def test_ambient_baseline_does_not_claim_audible_receiver_output(self):
        with patch("mic_verify.baseline", AsyncMock(return_value={"classification": "not_clean_tone"})), \
             patch("mic_verify.hardware_lease", return_value=nullcontext()), \
             patch("sys.stdout", new_callable=io.StringIO) as output:
            self.assertEqual(0, main(["--device", "synthetic", "--target", "fixture", "--baseline"]))
            report = json.loads(output.getvalue())
        self.assertEqual("ambient_baseline", report["mode"])
        self.assertFalse(report["acoustic_output_proven"])

    def test_missing_hardware_opt_in_fails_before_recording(self):
        with patch.dict("os.environ", {}, clear=True), \
             patch("mic_verify.verify", AsyncMock()) as verify, \
             patch("sys.stdout", new_callable=io.StringIO):
            self.assertEqual(1, main(["--device", "synthetic", "--target", "fixture"]))
        verify.assert_not_awaited()


class MicrophoneCaptureLifecycleTests(unittest.IsolatedAsyncioTestCase):
    async def test_ffmpeg_output_is_drained_while_receiver_streaming_runs(self):
        draining = threading.Event()

        def communicate(timeout):
            draining.set()
            return b"fixture", b""

        async def stream(*args):
            self.assertTrue(await asyncio.to_thread(draining.wait, 1), "Microphone pipe must be drained before the tone finishes")

        process = SimpleNamespace(communicate=communicate, returncode=0)
        with patch("mic_verify.subprocess.Popen", return_value=process), \
             patch("mic_verify.asyncio.sleep", AsyncMock()), \
             patch("mic_verify.stream_tone", side_effect=stream), \
             patch("mic_verify.strongest_tone_window", return_value=0), \
             patch("mic_verify.analyze", return_value={"classification": "clean_tone"}):
            self.assertEqual("clean_tone", (await verify("synthetic", "fixture", 8))["classification"])


if __name__ == "__main__":
    unittest.main()
