"""Narrow pyatv live-source adapter for permanently open raw PCM."""
from __future__ import annotations

import asyncio
import array
import contextvars
import math
import os
import struct
import sys
from typing import BinaryIO

from pyatv.interface import MediaMetadata
from pyatv.protocols.raop.audio_source import AudioSource

_deferred_initial_volume: contextvars.ContextVar[float | None] = contextvars.ContextVar(
    "airbridge_deferred_initial_volume", default=None
)
_stream_ready_event: contextvars.ContextVar[asyncio.Event | None] = contextvars.ContextVar(
    "airbridge_stream_ready_event", default=None
)
_source_patch_installed = False
_volume_patch_installed = False
_stream_volume_patch_installed = False
_airplay2_alac_patch_installed = False
SENDER_NAME = "AirBridge"


class _DeferVolumeUntilRecord(Exception):
    """Internal signal consumed by pyatv's stream_file fallback path."""


def s16le_to_airplay(data: bytes) -> bytes:
    """Convert canonical s16le PCM to the byte order pyatv's RAOP encoder expects.

    pyatv's built-in sources run every decoded buffer through its internal
    _to_audio_samples helper, which swaps 16-bit words on little-endian hosts.
    Custom AudioSource implementations must do the equivalent themselves.
    """
    if len(data) % 2:
        raise ValueError("16-bit PCM must contain a whole number of samples")
    if sys.byteorder != "little" or not data:
        return data
    samples = array.array("h", data)
    samples.byteswap()
    return samples.tobytes()


def pcm_s16be_to_uncompressed_alac(data: bytes) -> bytes:
    """Wrap one big-endian stereo PCM block in an uncompressed ALAC frame.

    AirPlay 2 realtime receivers such as Sonos expect type-96 packets to carry
    ALAC even when they accept an LPCM SETUP. This lossless ALAC form is
    packet-local, has four bytes of overhead, and needs no encoder state.
    """
    if len(data) % 4:
        raise ValueError("Stereo 16-bit PCM must contain whole frames")

    # ALAC element header: stereo, no explicit size, and is-not-compressed.
    # It is 23 bits long, so every following PCM byte straddles two bytes.
    output = bytearray(b"\x20\x00\x02")
    for value in data:
        output[-1] |= value >> 7
        output.append((value & 0x7F) << 1)
    # Three-bit end tag, beginning in the one remaining bit of the last byte.
    output[-1] |= 1
    output.append(0xC0)
    return bytes(output)


class LivePcmSource(AudioSource):
    """Raw s16le source that reports EOF only after an explicit close."""

    def __init__(self, pipe_name: str, sample_rate: int = 44100, channels: int = 2):
        self._path = rf"\\.\pipe\{pipe_name}"
        self._sample_rate = sample_rate
        self._channels = channels
        self._closed = False
        self._pipe: BinaryIO | None = None

    async def open(self) -> "LivePcmSource":
        self._pipe = await asyncio.to_thread(open, self._path, "rb", buffering=0)
        return self

    async def readframes(self, nframes: int) -> bytes:
        if self._closed or self._pipe is None:
            return self.NO_FRAMES
        requested = nframes * self.channels * self.sample_size
        data = bytearray()
        while len(data) < requested and not self._closed:
            chunk = await asyncio.to_thread(self._pipe.read, requested - len(data))
            if not chunk:
                self._closed = True
                break
            data.extend(chunk)
        return s16le_to_airplay(bytes(data))

    async def close(self) -> None:
        self._closed = True
        if self._pipe is not None:
            await asyncio.to_thread(self._pipe.close)
            self._pipe = None

    async def get_metadata(self) -> MediaMetadata:
        return MediaMetadata(title="Live Windows audio", artist="AirBridge", album="Live route", duration=0)

    @property
    def sample_rate(self) -> int:
        return self._sample_rate

    @property
    def channels(self) -> int:
        return self._channels

    @property
    def sample_size(self) -> int:
        return 2

    @property
    def duration(self) -> int:
        return 0


class DiagnosticToneSource(AudioSource):
    """Finite in-memory tone used to validate the exact packaged RAOP host."""

    def __init__(self, seconds: float, frequency: float = 523.25):
        self._total_frames = max(1, int(seconds * 44100))
        self._frames_left = self._total_frames
        self._position = 0
        self._frequency = frequency

    async def readframes(self, nframes: int) -> bytes:
        count = min(nframes, self._frames_left)
        if count <= 0:
            return self.NO_FRAMES
        output = bytearray(count * 4)
        for index in range(count):
            fade_frames = 441
            gain = min(1.0, self._position / fade_frames, (self._total_frames - self._position) / fade_frames)
            sample = int(12000 * gain * math.sin(2 * math.pi * self._frequency * self._position / 44100))
            struct.pack_into("<hh", output, index * 4, sample, sample)
            self._position += 1
        self._frames_left -= count
        return s16le_to_airplay(bytes(output))

    async def close(self) -> None:
        self._frames_left = 0

    async def get_metadata(self) -> MediaMetadata:
        return MediaMetadata(title="AirBridge packaged-host test", artist="AirBridge", duration=self.duration)

    @property
    def sample_rate(self) -> int:
        return 44100

    @property
    def channels(self) -> int:
        return 2

    @property
    def sample_size(self) -> int:
        return 2

    @property
    def duration(self) -> int:
        return round(self._total_frames / 44100)


def install_pyatv_adapter() -> None:
    """Teach only RAOP's source-opening seam about our raw live source."""
    global _source_patch_installed, _volume_patch_installed, _stream_volume_patch_installed
    global _airplay2_alac_patch_installed
    import pyatv.protocols.raop as raop
    from pyatv import exceptions
    from pyatv.protocols.airplay.auth import hap_transient
    from pyatv.protocols.raop.packets import AudioPacketHeader
    from pyatv.protocols.raop.protocols import airplayv2
    from pyatv.protocols.raop.stream_client import StreamClient
    from pyatv.support.chacha20 import Chacha20Cipher8byteNonce
    from pyatv.support.http import decode_bplist_from_body
    from pyatv.support.rtsp import FRAMES_PER_PACKET

    # macOS uses this header for the sender label in its first-use approval
    # prompt. pyatv 0.18 omits it, which produces an empty quoted device name.
    hap_transient._AIRPLAY_HEADERS["X-Apple-Client-Name"] = SENDER_NAME

    if not _source_patch_installed:
        original_open_source = raop.open_source

        async def open_live_source(source, sample_rate: int, channels: int, sample_size: int):
            # An AudioSource already provides canonical PCM frames and metadata. Sending
            # it through miniaudio would incorrectly treat it as a finite encoded file.
            if isinstance(source, AudioSource):
                return source
            return await original_open_source(source, sample_rate, channels, sample_size)

        raop.open_source = open_live_source
        _source_patch_installed = True

    if not _volume_patch_installed:
        original_set_volume = raop.RaopAudio.set_volume

        async def defer_pre_record_volume(audio, level: float, output_device=None):
            if _deferred_initial_volume.get() is not None:
                # RaopStream catches this and forwards audio.volume into
                # StreamClient.send_audio, which applies it after RECORD.
                raise _DeferVolumeUntilRecord()
            await original_set_volume(audio, level, output_device)

        raop.RaopAudio.set_volume = defer_pre_record_volume
        _volume_patch_installed = True

    if not _stream_volume_patch_installed:
        original_stream_set_volume = StreamClient.set_volume

        async def signal_post_record_volume(stream_client, level: float):
            await original_stream_set_volume(stream_client, level)
            ready = _stream_ready_event.get()
            if ready is not None:
                ready.set()

        StreamClient.set_volume = signal_post_record_volume
        _stream_volume_patch_installed = True

    if not _airplay2_alac_patch_installed:
        original_send_packet = StreamClient._send_packet

        async def setup_realtime_alac(protocol, control_client_port: int):
            if protocol._verifier is None:
                raise exceptions.InvalidStateError("base stream not set up")

            out_key, _ = protocol._verifier.encryption_keys(
                airplayv2.EVENTS_SALT,
                airplayv2.EVENTS_WRITE_INFO,
                airplayv2.EVENTS_READ_INFO,
            )
            shared_secret = out_key[:32]
            setup_response = await protocol.rtsp.setup(body={
                "streams": [{
                    "audioFormat": 0x40000,  # ALAC/44100/16/2
                    "audioMode": "default",
                    "controlPort": control_client_port,
                    "ct": 2,  # ALAC
                    "isMedia": True,
                    "latencyMax": 88200,
                    "latencyMin": 11025,
                    "shk": shared_secret,
                    "spf": FRAMES_PER_PACKET,
                    "sr": 44100,
                    "type": 0x60,
                    "supportsDynamicStreamID": False,
                    "streamConnectionID": protocol.rtsp.session_id,
                }]
            })
            stream_info = decode_bplist_from_body(setup_response)["streams"][0]
            protocol.context.control_port = stream_info["controlPort"]
            protocol.context.server_port = stream_info["dataPort"]
            protocol._cipher = Chacha20Cipher8byteNonce(shared_secret, shared_secret)

        async def send_realtime_alac_packet(stream_client, source, first_packet, transport):
            if not isinstance(stream_client._protocol, airplayv2.AirPlayV2):
                return await original_send_packet(stream_client, source, first_packet, transport)
            if stream_client.context.padding_sent >= stream_client.context.latency:
                return 0

            frames = await source.readframes(FRAMES_PER_PACKET)
            if not frames:
                frames = stream_client.context.packet_size * b"\x00"
                stream_client.context.padding_sent += FRAMES_PER_PACKET
            elif len(frames) != stream_client.context.packet_size:
                frames += (stream_client.context.packet_size - len(frames)) * b"\x00"

            header = AudioPacketHeader.encode(
                0x80,
                0xE0 if first_packet else 0x60,
                stream_client.context.rtpseq,
                stream_client.context.rtptime,
                stream_client.rtsp.session_id,
            )
            payload = pcm_s16be_to_uncompressed_alac(frames)
            rtpseq, packet = await stream_client._protocol.send_audio_packet(
                transport, header, payload
            )
            stream_client._packet_backlog[rtpseq] = packet
            stream_client.context.rtpseq = (stream_client.context.rtpseq + 1) % (2**16)
            stream_client.context.head_ts += FRAMES_PER_PACKET
            return FRAMES_PER_PACKET

        airplayv2.AirPlayV2.setup_audio_stream = setup_realtime_alac
        StreamClient._send_packet = send_realtime_alac_packet
        _airplay2_alac_patch_installed = True


async def stream_with_initial_volume(
    stream,
    source: AudioSource,
    initial_volume: float = 30.0,
    ready_event: asyncio.Event | None = None,
) -> None:
    """Start a stream with the first network volume command after RECORD."""
    from pyatv.protocols.airplay.utils import pct_to_dbfs

    # pyatv 0.18 uses a truthiness check before its post-RECORD setter. Keep a
    # requested mute truthy but acoustically negligible so it cannot fall back
    # to the receiver's manual volume.
    level = max(0.01, min(100.0, float(initial_volume)))
    playback_manager = getattr(stream, "playback_manager", None)
    if playback_manager is not None:
        playback_manager.context.volume = pct_to_dbfs(level)
    elif ready_event is not None:
        # Test and alternate adapters perform RECORD inside stream_file but do not
        # expose pyatv's low-level StreamClient readiness seam.
        ready_event.set()
    token = _deferred_initial_volume.set(level)
    ready_token = _stream_ready_event.set(ready_event)
    try:
        # pyatv currently accepts but ignores this kwarg at the public layer;
        # the context + deferred setter above route it to send_audio(volume=).
        await stream.stream_file(source, initial_volume=level)
    finally:
        _stream_ready_event.reset(ready_token)
        _deferred_initial_volume.reset(token)
