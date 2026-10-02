"""Verify a receiver test tone acoustically using an explicitly selected microphone."""
from __future__ import annotations

import argparse
import array
import asyncio
import json
import math
import subprocess
import sys

from smoke_test import main as stream_tone
from hardware_lease import hardware_lease

EXPECTED_FREQUENCY = 523.25


def goertzel_power(samples: list[float], sample_rate: int, frequency: float) -> float:
    coefficient = 2.0 * math.cos(2.0 * math.pi * frequency / sample_rate)
    previous = previous2 = 0.0
    for sample in samples:
        current = sample + coefficient * previous - previous2
        previous2, previous = previous, current
    return previous2 * previous2 + previous * previous - coefficient * previous * previous2


def analyze(raw: bytes, sample_rate: int, start_second: float, duration_seconds: int) -> dict:
    pcm = array.array("h", raw)
    if sys.byteorder != "little":
        pcm.byteswap()
    start = int(start_second * sample_rate)
    end = min(len(pcm), start + duration_seconds * sample_rate)
    samples = [value / 32768.0 for value in pcm[start:end]]
    if not samples:
        raise RuntimeError("The microphone returned no samples in the analysis window")
    rms = math.sqrt(sum(value * value for value in samples) / len(samples))
    powers = {frequency: goertzel_power(samples, sample_rate, frequency) for frequency in range(100, 2001, 10)}
    dominant = max(powers, key=powers.get)
    tone_power = goertzel_power(samples, sample_rate, EXPECTED_FREQUENCY)
    amplitude = 2.0 * math.sqrt(tone_power) / len(samples)
    tone_energy_fraction = min(1.0, (amplitude * amplitude / 2.0) / max(rms * rms, 1e-12))
    crossings = sum(1 for left, right in zip(samples, samples[1:]) if (left < 0 <= right) or (left >= 0 > right))
    crossing_frequency = crossings / (2.0 * (len(samples) / sample_rate))
    return {
        "captured_seconds": round(len(pcm) / sample_rate, 2),
        "window_start_seconds": start_second,
        "analysis_seconds": round(len(samples) / sample_rate, 2),
        "rms_dbfs": round(20 * math.log10(max(rms, 1e-12)), 2),
        # A 10 Hz Goertzel survey is useful for ambient interference, but it can
        # land on a sinc null for a finite non-bin-centred tone. Zero crossings
        # provide the accurate estimate once exact-tone energy is dominant.
        "dominant_frequency_hz": round(crossing_frequency, 2),
        "coarse_spectral_peak_hz": dominant,
        "expected_frequency_hz": EXPECTED_FREQUENCY,
        "tone_energy_fraction": round(tone_energy_fraction, 4),
        "zero_crossings_per_second": round(crossings / (len(samples) / sample_rate), 1),
        "classification": "clean_tone" if abs(crossing_frequency - EXPECTED_FREQUENCY) <= 20 and tone_energy_fraction >= 0.25 else "not_clean_tone",
    }


def strongest_tone_window(raw: bytes, sample_rate: int, duration_seconds: int = 4) -> float:
    pcm = array.array("h", raw)
    if sys.byteorder != "little":
        pcm.byteswap()
    window = duration_seconds * sample_rate
    if len(pcm) < window:
        return 0
    best_start = 0
    best_power = -1.0
    step = sample_rate // 2
    for start in range(0, len(pcm) - window + 1, step):
        samples = [value / 32768.0 for value in pcm[start:start + window]]
        power = goertzel_power(samples, sample_rate, EXPECTED_FREQUENCY)
        if power > best_power:
            best_power = power
            best_start = start
    return best_start / sample_rate


async def verify(device: str, target: str, tone_seconds: int, builtin: bool = False, airplay1: bool = False, volume: int = 30) -> dict:
    sample_rate = 16000
    # Discovery, pairing and AirPlay SETUP can take 8-15 seconds before the first
    # audible frame. Keep recording through the entire tone and locate it by signal.
    capture_seconds = tone_seconds + 40
    # subprocess passes this as one argv item, so embedded shell quotes become
    # literal characters and make DirectShow reject otherwise valid names.
    input_spec = f"audio={device}"
    command = [
        "ffmpeg", "-hide_banner", "-loglevel", "error",
        "-f", "dshow", "-i", input_spec,
        "-ac", "1", "-ar", str(sample_rate), "-t", str(capture_seconds),
        "-f", "s16le", "pipe:1",
    ]
    process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    # Drain immediately: a filled stdout pipe otherwise blocks FFmpeg before
    # the receiver tone starts and invalidates the acoustic observation window.
    capture = asyncio.create_task(asyncio.to_thread(process.communicate, timeout=capture_seconds + 10))
    try:
        await asyncio.sleep(2.0)
        await stream_tone(target, float(tone_seconds), builtin, airplay1, volume)
        raw, error = await capture
    except BaseException:
        if process.poll() is None:
            process.kill()
        await asyncio.gather(capture, return_exceptions=True)
        await asyncio.to_thread(process.wait, timeout=5)
        raise
    if process.returncode:
        raise RuntimeError(error.decode("utf-8", "replace"))
    window_seconds = min(4, max(2, tone_seconds - 2))
    start_second = strongest_tone_window(raw, sample_rate, window_seconds)
    return analyze(raw, sample_rate, start_second=start_second, duration_seconds=window_seconds)


async def baseline(device: str, seconds: int) -> dict:
    sample_rate = 16000
    input_spec = f"audio={device}"
    command = [
        "ffmpeg", "-hide_banner", "-loglevel", "error",
        "-f", "dshow", "-i", input_spec,
        "-ac", "1", "-ar", str(sample_rate), "-t", str(seconds),
        "-f", "s16le", "pipe:1",
    ]
    process = await asyncio.to_thread(subprocess.run, command, capture_output=True, timeout=seconds + 10)
    if process.returncode:
        raise RuntimeError(process.stderr.decode("utf-8", "replace"))
    return analyze(process.stdout, sample_rate, start_second=1, duration_seconds=max(2, seconds - 2))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--device", required=True)
    parser.add_argument("--target", required=True)
    parser.add_argument("--seconds", type=int, default=8)
    parser.add_argument("--volume", type=int, choices=range(0, 101), default=30)
    parser.add_argument("--baseline", action="store_true")
    parser.add_argument("--builtin", action="store_true")
    parser.add_argument("--airplay1", action="store_true")
    arguments = parser.parse_args(argv)
    operation = baseline(arguments.device, arguments.seconds) if arguments.baseline else verify(arguments.device, arguments.target, arguments.seconds, arguments.builtin, arguments.airplay1, arguments.volume)
    try:
        with hardware_lease():
            result = asyncio.run(operation)
        passed = arguments.baseline or result["classification"] == "clean_tone"
        result.update({"status": "passed" if passed else "failed",
                       "mode": "ambient_baseline" if arguments.baseline else "acoustic_verification",
                       "acoustic_scope": "direct_pyatv_test_tone",
                       "acoustic_output_proven": not arguments.baseline and passed,
                       "criteria": {"frequency_tolerance_hz": 20, "minimum_tone_energy_fraction": 0.25}})
        print(json.dumps(result, indent=2))
        return 0 if passed else 1
    except Exception as error:
        operation.close()
        print(json.dumps({"status": "failed", "error": str(error), "acoustic_output_proven": False}, indent=2))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
