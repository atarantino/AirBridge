"""AirBridge RAOP subprocess. JSON lines on stdin/stdout; logs only on stderr."""
from __future__ import annotations

import asyncio
import json
import logging
import os
import random
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import pyatv
from pyatv.const import DeviceModel, OperatingSystem, PairingRequirement, Protocol
from pyatv.storage.file_storage import FileStorage

from live_stream import DiagnosticToneSource, LivePcmSource, install_pyatv_adapter, stream_with_initial_volume

logging.basicConfig(stream=sys.stderr, level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
install_pyatv_adapter()


@dataclass
class Session:
    receiver_id: str
    receiver_name: str
    config: Any
    pipe_name: str
    atv: Any
    source: LivePcmSource | None = None
    stream_task: asyncio.Task | None = None
    stopping: bool = False
    desired_volume: int = 30
    stream_ready: asyncio.Event | None = None


class Host:
    def __init__(self) -> None:
        self.devices = {}
        self.sessions: dict[str, Session] = {}
        self.pairings: dict[str, Any] = {}
        storage_root = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "AirBridge"
        storage_root.mkdir(parents=True, exist_ok=True)
        self.storage = FileStorage((storage_root / "pyatv.conf").as_posix(), asyncio.get_running_loop())

    async def initialize(self) -> None:
        await self.storage.load()

    async def close(self) -> None:
        await self.stop_all()
        pending = list(self.pairings.values())
        self.pairings.clear()
        await asyncio.gather(*(pairing.close() for pairing in pending), return_exceptions=True)

    async def discover(self, timeout: int = 5) -> list[dict]:
        # Scan all protocols so AirPlay 2/Companion properties are merged into the
        # configuration used by HomePods; filtering the scan to RAOP loses that context.
        configs = await pyatv.scan(asyncio.get_running_loop(), timeout=timeout, storage=self.storage)
        receivers = []
        self.devices.clear()
        for config in configs:
            service = config.get_service(Protocol.RAOP)
            if service is None:
                continue
            stable_id = service.identifier or config.identifier or f"raop-{config.name.casefold()}"
            self.devices[stable_id] = config
            pairing = getattr(service, "pairing", PairingRequirement.Unsupported)
            has_credentials = bool(getattr(service, "credentials", None))
            power_protocol = control_protocol(config)
            power_service = config.get_service(power_protocol) if power_protocol is not None else None
            connection_issue = streaming_connection_issue(config)
            receivers.append({
                "id": stable_id,
                "name": config.name,
                "address": str(config.address),
                "requires_password": bool(getattr(service, "requires_password", False)),
                "device_type": device_type(config),
                "requires_pairing": pairing == PairingRequirement.Mandatory and not has_credentials,
                "supports_pairing": pairing in (PairingRequirement.Optional, PairingRequirement.Mandatory),
                "supports_power_control": power_protocol is not None and device_type(config) == "apple-tv",
                "requires_control_pairing": power_service is not None and not bool(getattr(power_service, "credentials", None)),
                "connection_issue": connection_issue,
            })
        return sorted(receivers, key=lambda value: value["name"].casefold())

    async def begin_pairing(self, receiver_id: str, pairing_kind: str = "raop") -> dict:
        if not self.devices:
            await self.discover()
        config = self.devices.get(receiver_id)
        if config is None:
            raise RuntimeError("Receiver was not found; refresh discovery and try again")
        protocol = Protocol.RAOP if pairing_kind == "raop" else control_protocol(config)
        service = config.get_service(protocol) if protocol is not None else None
        if service is None or service.pairing not in (PairingRequirement.Optional, PairingRequirement.Mandatory):
            raise RuntimeError("This receiver does not support AirPlay pairing")
        old = self.pairings.pop(receiver_id, None)
        if old is not None:
            await old.close()
        pairing = await pyatv.pair(config, protocol, asyncio.get_running_loop(), storage=self.storage, name="AirBridge")
        self.pairings[receiver_id] = pairing
        try:
            await pairing.begin()
        except Exception:
            self.pairings.pop(receiver_id, None)
            await pairing.close()
            raise
        return {"receiver_id": receiver_id, "protocol": protocol.name.lower(), "device_provides_pin": pairing.device_provides_pin}

    async def finish_pairing(self, receiver_id: str, pin: str) -> dict:
        pairing = self.pairings.pop(receiver_id, None)
        if pairing is None:
            raise RuntimeError("Pairing was not started")
        try:
            pairing.pin(pin)
            await pairing.finish()
            if not pairing.has_paired:
                raise RuntimeError("The receiver did not accept the pairing code")
            await self.storage.save()
            return {"receiver_id": receiver_id, "paired": True}
        finally:
            await pairing.close()

    async def cancel_pairing(self, receiver_id: str) -> dict:
        pairing = self.pairings.pop(receiver_id, None)
        if pairing is not None:
            await pairing.close()
        return {"receiver_id": receiver_id, "cancelled": True}

    async def sleep(self, receiver_id: str) -> dict:
        if not self.devices:
            await self.discover()
        config = self.devices.get(receiver_id)
        if config is None:
            raise RuntimeError("Receiver was not found; refresh discovery and try again")
        protocol = control_protocol(config)
        if protocol is None or device_type(config) != "apple-tv":
            raise RuntimeError("This receiver does not support Apple TV power control")
        atv = await pyatv.connect(
            config, asyncio.get_running_loop(), protocol=protocol, storage=self.storage
        )
        try:
            await atv.power.turn_off()
        finally:
            atv.close()
        return {"receiver_id": receiver_id, "sleeping": True}

    async def start(self, receiver_id: str | None, receiver_name: str | None, pipe_name: str, initial_volume: int = 30) -> dict:
        if not self.devices:
            await self.discover()
        config = self.devices.get(receiver_id) if receiver_id else None
        if config is None and receiver_name:
            matches = [item for item in self.devices.values() if item.name.casefold() == receiver_name.casefold()]
            if len(matches) != 1:
                raise RuntimeError(f"Expected one receiver named {receiver_name!r}, found {len(matches)}")
            config = matches[0]
        if config is None:
            raise RuntimeError("Receiver was not found; refresh discovery and try again")

        session_id = receiver_id or config.identifier
        await self.stop(session_id)
        session = Session(
            receiver_id=session_id,
            receiver_name=config.name,
            config=config,
            pipe_name=pipe_name,
            atv=await pyatv.connect(config, asyncio.get_running_loop(), storage=self.storage),
            desired_volume=max(0, min(100, int(initial_volume))),
            stream_ready=asyncio.Event(),
        )
        self.sessions[session_id] = session
        await self._emit_state(session, "buffering")
        session.stream_task = asyncio.create_task(self._stream_with_reconnect(session))
        return {"accepted": True, "receiver": config.name, "id": session_id}

    async def _stream_with_reconnect(self, session: Session) -> None:
        backoff = [0.5, 1, 2, 5, 10, 30]
        attempt = 0
        while not session.stopping:
            try:
                session.source = await LivePcmSource(session.pipe_name).open()
                if session.stream_ready is None:
                    session.stream_ready = asyncio.Event()
                else:
                    session.stream_ready.clear()
                playback = asyncio.create_task(stream_with_initial_volume(
                    session.atv.stream,
                    session.source,
                    session.desired_volume,
                    session.stream_ready,
                ))
                ready_wait = asyncio.create_task(session.stream_ready.wait())
                try:
                    done, _ = await asyncio.wait(
                        {playback, ready_wait}, return_when=asyncio.FIRST_COMPLETED
                    )
                    if playback in done:
                        await playback
                    await self._emit_state(session, "streaming")
                    await playback
                finally:
                    ready_wait.cancel()
                    if not playback.done():
                        playback.cancel()
                    await asyncio.gather(ready_wait, playback, return_exceptions=True)
                if not session.stopping:
                    raise RuntimeError("RAOP stream ended unexpectedly")
            except asyncio.CancelledError:
                return
            except Exception as ex:
                if session.stopping:
                    return
                logging.exception("Receiver stream failed for %s", session.receiver_name)
                if session.source:
                    await session.source.close()
                    session.source = None
                await self._emit_state(session, "reconnecting", sanitize_error(ex))
                delay = backoff[min(attempt, len(backoff) - 1)] * random.uniform(0.85, 1.15)
                attempt += 1
                await asyncio.sleep(delay)
                try:
                    if session.atv:
                        session.atv.close()
                    replacement = await pyatv.connect(session.config, asyncio.get_running_loop(), storage=self.storage)
                    if session.stopping:
                        replacement.close()
                        return
                    session.atv = replacement
                    # A broken PCM pipe cannot be replayed; the .NET side keeps it open across RAOP reconnects.
                except Exception as connect_error:
                    logging.exception("Receiver reconnect failed for %s", session.receiver_name)
                    await emit({
                        "event": "telemetry",
                        "receiver_id": session.receiver_id,
                        "receiver": session.receiver_name,
                        "reconnect_error": sanitize_error(connect_error),
                    })

    async def stop(self, receiver_id: str | None = None) -> dict:
        # An omitted target preserves the original single-session command contract.
        if receiver_id is None:
            return await self.stop_all()
        session = self.sessions.pop(receiver_id, None)
        if session is None:
            return {"stopped": True, "receiver_id": receiver_id, "was_active": False}
        session.stopping = True
        if session.stream_task:
            session.stream_task.cancel()
            try:
                await session.stream_task
            except asyncio.CancelledError:
                pass
            session.stream_task = None
        if session.source:
            await session.source.close()
            session.source = None
        if session.atv:
            session.atv.close()
            session.atv = None
        await self._emit_state(session, "idle")
        return {"stopped": True, "receiver_id": receiver_id, "was_active": True}

    async def stop_all(self) -> dict:
        receiver_ids = list(self.sessions)
        await asyncio.gather(*(self.stop(receiver_id) for receiver_id in receiver_ids))
        return {"stopped": True, "receiver_ids": receiver_ids}

    async def set_volume(self, percent: int, receiver_id: str | None = None) -> dict:
        if receiver_id is None:
            if len(self.sessions) != 1:
                raise RuntimeError("receiver_id is required unless exactly one receiver is connected")
            receiver_id = next(iter(self.sessions))
        session = self.sessions.get(receiver_id)
        if session is None or not session.atv:
            raise RuntimeError("The requested receiver is not connected")
        session.desired_volume = max(0, min(100, int(percent)))
        if session.stream_ready is not None and not session.stream_ready.is_set():
            await asyncio.wait_for(session.stream_ready.wait(), timeout=15)
        await session.atv.audio.set_volume(float(session.desired_volume))
        return {"receiver_id": receiver_id, "volume": session.desired_volume}

    @staticmethod
    async def _emit_state(session: Session, state: str, error: str | None = None) -> None:
        event = {
            "event": "state",
            "receiver_id": session.receiver_id,
            "receiver": session.receiver_name,
            "state": state,
        }
        if error is not None:
            event["error"] = error
        await emit(event)

    async def diagnostic_tone(self, receiver_name: str, seconds: float) -> dict:
        """Stream through the same adapter embedded in the packaged host."""
        await self.stop_all()
        if not self.devices:
            await self.discover()
        matches = [item for item in self.devices.values() if item.name.casefold() == receiver_name.casefold()]
        if len(matches) != 1:
            raise RuntimeError(f"Expected one receiver named {receiver_name!r}, found {len(matches)}")
        atv = await pyatv.connect(matches[0], asyncio.get_running_loop(), storage=self.storage)
        try:
            source = DiagnosticToneSource(seconds)
            await atv.stream.stream_file(source, metadata=await source.get_metadata())
        finally:
            atv.close()
        return {"streamed": True, "receiver": receiver_name, "seconds": seconds, "frequency_hz": 523.25}


def sanitize_error(ex: Exception) -> str:
    # Protocol errors can contain addresses. Only surface the type and a bounded generic message.
    status = getattr(ex, "status_code", None)
    status_text = f" (status {int(status)})" if isinstance(status, int) else ""
    return f"{type(ex).__name__}: receiver transport operation failed{status_text}"


def device_type(config: Any) -> str:
    info = getattr(config, "device_info", None)
    model = getattr(info, "model", DeviceModel.Unknown)
    if model in {
        DeviceModel.Gen2, DeviceModel.Gen3, DeviceModel.Gen4, DeviceModel.Gen4K,
        DeviceModel.AppleTV4KGen2, DeviceModel.AppleTV4KGen3, DeviceModel.AppleTVGen1,
    }:
        return "apple-tv"
    if model in {DeviceModel.HomePod, DeviceModel.HomePodMini, DeviceModel.HomePodGen2}:
        return "homepod"
    if model in {DeviceModel.AirPortExpress, DeviceModel.AirPortExpressGen2}:
        return "speaker"
    if getattr(info, "operating_system", OperatingSystem.Unknown) == OperatingSystem.MacOS:
        return "computer"
    identity = f"{getattr(info, 'model_str', '')} {getattr(info, 'raw_model', '')}".casefold()
    if "apple tv" in identity or "appletv" in identity:
        return "apple-tv"
    return "speaker"


def control_protocol(config: Any) -> Protocol | None:
    for protocol in (Protocol.Companion, Protocol.MRP):
        service = config.get_service(protocol)
        if service is not None and getattr(service, "pairing", PairingRequirement.Unsupported) not in {
            PairingRequirement.Disabled, PairingRequirement.Unsupported
        }:
            return protocol
    return None


def streaming_connection_issue(config: Any) -> str | None:
    """Explain advertised access-control modes that a Windows sender cannot satisfy."""
    if device_type(config) != "computer":
        return None
    airplay = config.get_service(Protocol.AirPlay)
    if airplay is not None and airplay.properties.get("act", "0") == "2":
        return "On the Mac, change AirPlay Receiver access from Current User to Anyone on the Same Network"
    return None


async def emit(value: dict) -> None:
    async with _emit_lock:
        print(json.dumps(value, separators=(",", ":")), flush=True)


_emit_lock = asyncio.Lock()


async def main() -> None:
    host = Host()
    await host.initialize()
    while True:
        line = await asyncio.to_thread(sys.stdin.readline)
        if not line:
            await host.close()
            return
        request = json.loads(line)
        request_id = request.get("request_id")
        try:
            command = request.get("command")
            if command == "discover":
                result = await host.discover(int(request.get("timeout", 5)))
            elif command == "start":
                result = await host.start(request.get("receiver_id"), request.get("receiver_name"), request["pipe_name"], int(request.get("initial_volume", 30)))
            elif command == "begin_pairing":
                result = await host.begin_pairing(request["receiver_id"], request.get("pairing_kind", "raop"))
            elif command == "finish_pairing":
                result = await host.finish_pairing(request["receiver_id"], request["pin"])
            elif command == "cancel_pairing":
                result = await host.cancel_pairing(request["receiver_id"])
            elif command == "sleep":
                result = await host.sleep(request["receiver_id"])
            elif command == "stop":
                result = await host.stop(request.get("receiver_id"))
            elif command == "stop_all":
                result = await host.stop_all()
            elif command == "set_volume":
                result = await host.set_volume(int(request["percent"]), request.get("receiver_id"))
            elif command == "diagnostic_tone":
                result = await host.diagnostic_tone(request["receiver_name"], float(request.get("seconds", 4)))
            elif command == "ping":
                result = {"ok": True, "pyatv": pyatv.__version__ if hasattr(pyatv, "__version__") else "0.18.0"}
            else:
                raise ValueError("Unknown command")
            await emit({"request_id": request_id, "ok": True, "result": result})
        except Exception as ex:
            logging.exception("Command failed")
            await emit({"request_id": request_id, "ok": False, "error": sanitize_error(ex)})


if __name__ == "__main__":
    asyncio.run(main())
