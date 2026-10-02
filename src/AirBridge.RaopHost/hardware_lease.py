"""An explicit, machine-local lease shared by .NET and Python diagnostics."""
from contextlib import contextmanager
import os
import tempfile


@contextmanager
def hardware_lease():
    if os.environ.get("AIRBRIDGE_RUN_HARDWARE_TESTS") != "1":
        raise RuntimeError("Set AIRBRIDGE_RUN_HARDWARE_TESTS=1 to explicitly enable receiver and microphone diagnostics")
    path = os.path.join(tempfile.gettempdir(), "AirBridge.HardwareDiagnostics.lock")
    try:
        handle = open(path, "a+b")
    except OSError as error:
        raise RuntimeError("Another worktree owns the hardware diagnostic lease") from error
    try:
        try:
            if os.name == "nt":
                import msvcrt
                handle.seek(0)
                if not handle.read(1):
                    handle.write(b"\0")
                    handle.flush()
                handle.seek(0)
                msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
            else:
                import fcntl
                fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError as error:
            raise RuntimeError("Another worktree owns the hardware diagnostic lease") from error
        yield
    finally:
        # Closing releases the kernel lock even if the operation raised.
        handle.close()
