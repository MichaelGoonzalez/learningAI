"""Captura de video en segundo plano para archivos y fuentes en vivo."""

from __future__ import annotations

import logging
import threading
import time
from pathlib import Path

import cv2
import numpy as np


LOGGER = logging.getLogger(__name__)
FramePacket = tuple[np.ndarray, float]


class VideoSource:
    """Mantiene una captura no bloqueante con un unico frame disponible."""

    def __init__(
        self,
        source: str | int,
        *,
        loop: bool = False,
        reconnect_delay_ms: int,
        max_reconnect_delay_ms: int,
        fallback_fps: float,
    ) -> None:
        self.source = source
        self.loop = loop
        self._reconnect_delay_s = reconnect_delay_ms / 1000.0
        self._max_reconnect_delay_s = max_reconnect_delay_ms / 1000.0
        self._fallback_fps = fallback_fps
        self._is_live = isinstance(source, int) or (
            isinstance(source, str)
            and source.lower().startswith(("rtsp://", "rtsps://"))
        )

        self._condition = threading.Condition()
        self._stop_event = threading.Event()
        self._capture_lock = threading.Lock()
        self._capture: cv2.VideoCapture | None = None
        self._latest: FramePacket | None = None
        self._published_sequence = 0
        self._read_sequence = 0
        self._fps = 0.0
        self._thread = threading.Thread(
            target=self._run,
            name=f"video-source-{source}",
            daemon=True,
        )
        self._thread.start()

    @property
    def is_alive(self) -> bool:
        """Indica si el worker sigue activo o intentando reconectar."""
        return self._thread.is_alive() and not self._stop_event.is_set()

    @property
    def fps(self) -> float:
        with self._condition:
            return self._fps

    def read(self) -> FramePacket | None:
        """Retorna el frame mas reciente una sola vez, sin bloquear."""
        with self._condition:
            if (
                self._latest is None
                or self._published_sequence == self._read_sequence
            ):
                return None
            self._read_sequence = self._published_sequence
            packet = self._latest
            self._condition.notify_all()
            return packet

    def stop(self) -> None:
        """Solicita el cierre, libera OpenCV y espera la salida del worker."""
        if self._stop_event.is_set():
            return
        self._stop_event.set()
        with self._condition:
            self._condition.notify_all()
        with self._capture_lock:
            capture = self._capture
        if capture is not None:
            capture.release()
        if self._thread is not threading.current_thread():
            self._thread.join()

    def _run(self) -> None:
        try:
            if self._is_live:
                self._run_live()
            else:
                self._run_file()
        finally:
            self._release_capture()

    def _run_live(self) -> None:
        backoff_s = self._reconnect_delay_s
        while not self._stop_event.is_set():
            capture = self._open_capture()
            if capture is None:
                LOGGER.warning(
                    "No se pudo abrir la fuente %s; reintento en %.1f s",
                    self.source,
                    backoff_s,
                )
                if self._stop_event.wait(backoff_s):
                    return
                backoff_s = min(backoff_s * 2, self._max_reconnect_delay_s)
                continue

            backoff_s = self._reconnect_delay_s
            while not self._stop_event.is_set():
                ok, frame = capture.read()
                if not ok or frame is None:
                    LOGGER.warning("Lectura perdida en %s; reconectando", self.source)
                    break
                self._publish(frame)
            self._release_capture()
            if not self._stop_event.is_set() and self._stop_event.wait(backoff_s):
                return

    def _run_file(self) -> None:
        capture = self._open_capture()
        if capture is None:
            LOGGER.error("No se pudo abrir el archivo de video %s", self.source)
            return

        frame_interval_s = 1.0 / (self.fps or self._fallback_fps)
        next_frame_at = time.perf_counter()
        while not self._stop_event.is_set():
            ok, frame = capture.read()
            if not ok or frame is None:
                if not self.loop:
                    return
                if not capture.set(cv2.CAP_PROP_POS_FRAMES, 0):
                    self._release_capture()
                    capture = self._open_capture()
                    if capture is None:
                        return
                next_frame_at = time.perf_counter()
                continue

            wait_s = next_frame_at - time.perf_counter()
            if wait_s > 0 and self._stop_event.wait(wait_s):
                return
            if not self._wait_until_consumed():
                return
            self._publish(frame)
            next_frame_at = max(next_frame_at + frame_interval_s, time.perf_counter())

    def _open_capture(self) -> cv2.VideoCapture | None:
        source: str | int = self.source
        if isinstance(source, str) and not self._is_live:
            source = str(Path(source).expanduser())
        capture = cv2.VideoCapture(source)
        if not capture.isOpened():
            capture.release()
            return None
        with self._capture_lock:
            self._capture = capture
        reported_fps = float(capture.get(cv2.CAP_PROP_FPS))
        with self._condition:
            self._fps = reported_fps if reported_fps > 0 else self._fallback_fps
        return capture

    def _publish(self, frame: np.ndarray) -> None:
        packet = (frame, time.time() * 1000.0)
        with self._condition:
            self._latest = packet
            self._published_sequence += 1
            self._condition.notify_all()

    def _wait_until_consumed(self) -> bool:
        with self._condition:
            while (
                self._published_sequence != self._read_sequence
                and not self._stop_event.is_set()
            ):
                self._condition.wait()
            return not self._stop_event.is_set()

    def _release_capture(self) -> None:
        with self._capture_lock:
            capture = self._capture
            self._capture = None
        if capture is not None:
            capture.release()

    def __enter__(self) -> VideoSource:
        return self

    def __exit__(self, *_: object) -> None:
        self.stop()
