"""Hardware smoke test: stream an in-memory tone to one exact receiver name."""
from __future__ import annotations

import argparse
import asyncio
import io
import json
import math
import struct
import wave

import pyatv
from pyatv.const import Protocol
from pyatv.protocols.raop import stream_client
from pyatv.settings import AirPlayVersion
from pyatv.storage.memory_storage import MemoryStorage

from hardware_lease import hardware_lease
from live_stream import DiagnosticToneSource, install_pyatv_adapter, stream_with_initial_volume


def built_in_wav(seconds: float) -> io.BytesIO:
    frame_count = max(1, int(seconds * 44100))
    output = io.BytesIO()
    with wave.open(output, "wb") as wav:
        wav.setnchannels(2)
        wav.setsampwidth(2)
        wav.setframerate(44100)
        frames = bytearray(frame_count * 4)
        for position in range(frame_count):
            fade_frames = 441
            gain = min(1.0, position / fade_frames, (frame_count - position) / fade_frames)
            sample = int(12000 * gain * math.sin(2 * math.pi * 523.25 * position / 44100))
            struct.pack_into("<hh", frames, position * 4, sample, sample)
        wav.writeframes(frames)
    output.seek(0)
    return output


async def main(name: str, seconds: float, builtin: bool = False, airplay1: bool = False, volume: int = 30) -> dict:
    install_pyatv_adapter()
    stats = {"timing_requests": 0, "retransmit_requests": 0, "lost_packets_requested": 0}
    original_timing_received = stream_client.TimingServer.datagram_received
    original_control_received = stream_client.ControlClient.datagram_received

    def timing_received(instance, data, address):
        stats["timing_requests"] += 1
        return original_timing_received(instance, data, address)

    def control_received(instance, data, address):
        if len(data) > 1 and data[1] & 0x7F == 0x55:
            request = stream_client.RetransmitReqeust.decode(data)
            stats["retransmit_requests"] += 1
            stats["lost_packets_requested"] += request.lost_packets
        return original_control_received(instance, data, address)

    stream_client.TimingServer.datagram_received = timing_received
    stream_client.ControlClient.datagram_received = control_received
    atv = None
    try:
        devices = await pyatv.scan(asyncio.get_running_loop(), timeout=8)
        matches = [device for device in devices if device.name.casefold() == name.casefold() and device.get_service(Protocol.RAOP)]
        if len(matches) != 1:
            raise RuntimeError(f"Expected one exact receiver match, found {len(matches)}")
        storage = MemoryStorage()
        settings = await storage.get_settings(matches[0])
        if airplay1:
            settings.protocols.raop.protocol_version = AirPlayVersion.V1
        atv = await asyncio.wait_for(pyatv.connect(matches[0], asyncio.get_running_loop(), storage=storage), timeout=30)
        # Use the production post-RECORD volume/readiness seam. A public volume
        # call before stream_file can regress receivers that reject it pre-RECORD.
        source = built_in_wav(seconds) if builtin else DiagnosticToneSource(seconds)
        ready = asyncio.Event()
        await asyncio.wait_for(stream_with_initial_volume(atv.stream, source, float(volume), ready), timeout=seconds + 40)
        if not ready.is_set():
            raise RuntimeError("Transport completed without post-RECORD readiness acknowledgement")
    finally:
        if atv is not None:
            atv.close()
        stream_client.TimingServer.datagram_received = original_timing_received
        stream_client.ControlClient.datagram_received = original_control_received
    path = "pyatv built-in WAV" if builtin else "AirBridge live PCM"
    protocol = "AirPlay 1" if airplay1 else "automatic AirPlay"
    return {"status": "passed", "seconds": seconds, "volume": volume, "path": path, "protocol": protocol,
            "transport": stats, "criteria": {"transport_completed": True, "post_record_ready": True},
            "acoustic_output_proven": False}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", required=True)
    parser.add_argument("--seconds", type=float, default=2.0)
    parser.add_argument("--volume", type=int, choices=range(0, 101), default=30)
    parser.add_argument("--builtin", action="store_true")
    parser.add_argument("--airplay1", action="store_true")
    args = parser.parse_args()
    try:
        with hardware_lease():
            result = asyncio.run(main(args.target, args.seconds, args.builtin, args.airplay1, args.volume))
        print(json.dumps(result, indent=2))
    except Exception as error:
        print(json.dumps({"status": "failed", "error": str(error), "acoustic_output_proven": False}, indent=2))
        raise SystemExit(1)
