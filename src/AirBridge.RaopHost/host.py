"""AirBridge RAOP subprocess. JSON lines on stdin/stdout; logs only on stderr."""
from __future__ import annotations

import asyncio
import json
import logging
import random
import sys
from dataclasses import dataclass
from typing import Any

import pyatv
from pyatv.const import Protocol

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

    async def discover(self, timeout: int = 5) -> list[dict]:
        # Scan all protocols so AirPlay 2/Companion properties are merged into the
        # configuration used by HomePods; filtering the scan to RAOP loses that context.
        configs = await pyatv.scan(asyncio.get_running_loop(), timeout=timeout)
        receivers = []
        self.devices.clear()
        for config in configs:
            service = config.get_service(Protocol.RAOP)
            if service is None:
                continue
            stable_id = service.identifier or config.identifier or f"raop-{config.name.casefold()}"
            self.devices[stable_id] = config
            receivers.append({
                "id": stable_id,
                "name": config.name,
                "address": str(config.address),
                "requires_password": bool(service.password),
            })
        return sorted(receivers, key=lambda value: value["name"].casefold())

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
            atv=await pyatv.connect(config, asyncio.get_running_loop()),
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
                    replacement = await pyatv.connect(session.config, asyncio.get_running_loop())
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
        atv = await pyatv.connect(matches[0], asyncio.get_running_loop())
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


async def emit(value: dict) -> None:
    async with _emit_lock:
        print(json.dumps(value, separators=(",", ":")), flush=True)


_emit_lock = asyncio.Lock()


async def main() -> None:
    host = Host()
    while True:
        line = await asyncio.to_thread(sys.stdin.readline)
        if not line:
            await host.stop_all()
            return
        request = json.loads(line)
        request_id = request.get("request_id")
        try:
            command = request.get("command")
            if command == "discover":
                result = await host.discover(int(request.get("timeout", 5)))
            elif command == "start":
                result = await host.start(request.get("receiver_id"), request.get("receiver_name"), request["pipe_name"], int(request.get("initial_volume", 30)))
            elif command == "stop":
                result = await host.stop(request.get("receiver_id"))
            elif command == "stop_all":
                result = await host.stop_all()
            elif command == "set_volume":
                result = await host.set_volume(int(request["percent"]), request.get("receiver_id"))
            elif command == "diagnostic_tone":
                result = await host.diagnostic_tone(request.get("receiver_name", "Kitchen"), float(request.get("seconds", 4)))
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
