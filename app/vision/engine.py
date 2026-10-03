"""Motor de pose independiente del runtime y dispositivo seleccionados."""

from __future__ import annotations

import logging
import threading
import time
from dataclasses import dataclass
from pathlib import Path
import numpy as np

from app.core.config import AppSettings, ZoneConfig
from app.hardware.detect import DeviceInfo, RuntimeInfo, detect_devices, detect_runtimes
from app.hardware.selection import (
    DeviceSelection,
    default_settings_path,
    resolve_selection,
    save_selection,
)
from app.vision.backends import BackendDetections, InferenceBackend, create_backend
from app.vision.rules import (
    LEFT_SHOULDER,
    RIGHT_SHOULDER,
    HandsState,
    evaluate_hands,
)
from app.vision.zones import find_zone


LOGGER = logging.getLogger(__name__)
BBox = tuple[float, float, float, float]


@dataclass(frozen=True, slots=True)
class PersonDetection:
    track_id: int | None
    bbox: BBox
    keypoints: np.ndarray
    hands_state: HandsState
    zone: str | None


@dataclass(frozen=True, slots=True)
class InferenceStats:
    fps: float
    latency_ms: float
    average_latency_ms: float
    frames_processed: int
    keypoint_confidence: float


class _ByteTrackAdapter:
    """Adapta las detecciones normalizadas al tracker de Ultralytics."""

    def __init__(self, tracker_config: str) -> None:
        try:
            from ultralytics.engine.results import Boxes
            from ultralytics.trackers.byte_tracker import BYTETracker
            from ultralytics.utils import IterableSimpleNamespace, YAML
            from ultralytics.utils.checks import check_yaml
        except Exception as exc:
            raise RuntimeError("Ultralytics ByteTrack is not installed") from exc
        tracker_args = IterableSimpleNamespace(**YAML.load(check_yaml(tracker_config)))
        self._boxes_type = Boxes
        self._tracker = BYTETracker(tracker_args)

    def update(
        self, detections: BackendDetections, frame: np.ndarray
    ) -> list[int | None]:
        if len(detections.boxes):
            classes = np.zeros((len(detections.boxes), 1), dtype=np.float32)
            data = np.column_stack(
                (detections.boxes, detections.scores.reshape(-1, 1), classes)
            ).astype(np.float32, copy=False)
        else:
            data = np.empty((0, 6), dtype=np.float32)
        boxes = self._boxes_type(data, frame.shape[:2])
        tracks = np.asarray(self._tracker.update(boxes, frame))
        track_ids: list[int | None] = [None] * len(detections.boxes)
        if tracks.ndim != 2 or tracks.shape[1] < 8:
            return track_ids
        for track in tracks:
            detection_index = int(track[7])
            if 0 <= detection_index < len(track_ids):
                track_ids[detection_index] = int(track[4])
        return track_ids


class PoseEngine:
    """Coordina backend, ByteTrack, reglas y cambio de dispositivo en caliente."""

    def __init__(
        self,
        cfg: AppSettings,
        *,
        camera_id: str | None = None,
        model_weights: str | None = None,
    ) -> None:
        self._cfg = cfg
        self._zones = self._select_zones(cfg, camera_id)
        self._model = Path(model_weights or cfg.model.weights)
        self._settings_path = (
            cfg.hardware.settings_path or default_settings_path()
        ).expanduser()
        self._lock = threading.RLock()
        self._frames_processed = 0
        self._total_inference_s = 0.0
        self._last_latency_ms = 0.0
        self._devices = detect_devices()
        selected, used_fallback = resolve_selection(
            self._devices, self._settings_path
        )
        self._backend: InferenceBackend
        self._tracker: _ByteTrackAdapter
        self._active_device: DeviceInfo
        try:
            self._backend = create_backend(cfg.model, self._model, selected)
            self._active_device = selected
        except Exception as exc:
            if selected.id == "cpu":
                raise
            LOGGER.warning(
                "No se pudo iniciar %s (%s); se usara CPU: %s",
                selected.name,
                selected.id,
                exc,
            )
            cpu = self._device_by_id("cpu")
            self._backend = create_backend(cfg.model, self._model, cpu)
            self._active_device = cpu
            used_fallback = True
        self._tracker = _ByteTrackAdapter(cfg.model.tracker_config)
        if used_fallback or not self._settings_path.is_file():
            self._persist_active_device()
        LOGGER.info(
            "Motor cargado en %s mediante %s",
            self._active_device.name,
            self._active_device.backend,
        )

    @property
    def zones(self) -> tuple[ZoneConfig, ...]:
        return self._zones

    @property
    def devices(self) -> tuple[DeviceInfo, ...]:
        return tuple(self._devices)

    @property
    def active_device(self) -> DeviceInfo:
        return self._active_device

    @property
    def runtime_info(self) -> RuntimeInfo:
        return detect_runtimes()

    @property
    def stats(self) -> InferenceStats:
        with self._lock:
            average_latency_ms = (
                self._total_inference_s * 1000.0 / self._frames_processed
                if self._frames_processed
                else 0.0
            )
            fps = (
                self._frames_processed / self._total_inference_s
                if self._total_inference_s > 0
                else 0.0
            )
            return InferenceStats(
                fps=fps,
                latency_ms=self._last_latency_ms,
                average_latency_ms=average_latency_ms,
                frames_processed=self._frames_processed,
                keypoint_confidence=self._cfg.detection.keypoint_confidence,
            )

    def process(self, frame: np.ndarray) -> list[PersonDetection]:
        """Procesa un frame de forma serializada respecto a una recarga."""
        started_at = time.perf_counter()
        with self._lock:
            try:
                backend_detections = self._backend.infer(frame)
                track_ids = self._tracker.update(backend_detections, frame)
                return self._build_detections(backend_detections, track_ids)
            finally:
                elapsed_s = time.perf_counter() - started_at
                self._frames_processed += 1
                self._total_inference_s += elapsed_s
                self._last_latency_ms = elapsed_s * 1000.0

    def reload_device(self, device_id: str) -> DeviceInfo:
        """Carga el nuevo backend y lo intercambia sin reiniciar el proceso."""
        with self._lock:
            device = self._device_by_id(device_id)
            if not device.runtime_installed:
                raise RuntimeError(
                    device.runtime_message or f"runtime unavailable for {device.id}"
                )
            if device.id == self._active_device.id:
                return device

            new_backend = create_backend(self._cfg.model, self._model, device)
            try:
                new_tracker = _ByteTrackAdapter(self._cfg.model.tracker_config)
            except Exception:
                new_backend.close()
                raise
            previous_backend = self._backend
            self._backend = new_backend
            self._tracker = new_tracker
            self._active_device = device
            self._persist_active_device()
            previous_backend.close()
            LOGGER.info(
                "Dispositivo cambiado a %s mediante %s", device.name, device.backend
            )
            return device

    def refresh_devices(self) -> tuple[DeviceInfo, ...]:
        with self._lock:
            self._devices = detect_devices()
            return tuple(self._devices)

    def close(self) -> None:
        with self._lock:
            self._backend.close()

    def _build_detections(
        self,
        backend_detections: BackendDetections,
        track_ids: list[int | None],
    ) -> list[PersonDetection]:
        detections: list[PersonDetection] = []
        for index, raw_bbox in enumerate(backend_detections.boxes):
            bbox: BBox = tuple(float(value) for value in raw_bbox[:4])  # type: ignore[assignment]
            keypoints = self._normalize_keypoints(
                backend_detections.keypoints[index]
                if index < len(backend_detections.keypoints)
                else np.zeros((17, 3), dtype=np.float32)
            )
            hands_state = evaluate_hands(keypoints, self._cfg.detection)
            reference_point = self._reference_point(bbox, keypoints)
            detections.append(
                PersonDetection(
                    track_id=track_ids[index] if index < len(track_ids) else None,
                    bbox=bbox,
                    keypoints=keypoints,
                    hands_state=hands_state,
                    zone=find_zone(reference_point, self._zones),
                )
            )
        return detections

    def _device_by_id(self, device_id: str) -> DeviceInfo:
        for device in self._devices:
            if device.id == device_id:
                return device
        raise ValueError(f"device is not available: {device_id}")

    def _persist_active_device(self) -> None:
        save_selection(
            self._settings_path,
            DeviceSelection(device_id=self._active_device.id),
        )

    def _reference_point(
        self, bbox: BBox, keypoints: np.ndarray
    ) -> tuple[float, float]:
        if self._cfg.detection.zone_reference == "shoulder_midpoint":
            left_shoulder = keypoints[LEFT_SHOULDER]
            right_shoulder = keypoints[RIGHT_SHOULDER]
            confidence = self._cfg.detection.keypoint_confidence
            if left_shoulder[2] >= confidence and right_shoulder[2] >= confidence:
                return (
                    float((left_shoulder[0] + right_shoulder[0]) / 2.0),
                    float((left_shoulder[1] + right_shoulder[1]) / 2.0),
                )
        x1, y1, x2, y2 = bbox
        return ((x1 + x2) / 2.0, (y1 + y2) / 2.0)

    @staticmethod
    def _normalize_keypoints(keypoints: np.ndarray) -> np.ndarray:
        points = np.asarray(keypoints, dtype=float)
        if points.shape == (17, 3):
            return points
        normalized = np.zeros((17, 3), dtype=float)
        rows = min(len(points), len(normalized)) if points.ndim == 2 else 0
        columns = min(points.shape[1], 3) if rows else 0
        if rows and columns:
            normalized[:rows, :columns] = points[:rows, :columns]
        return normalized

    @staticmethod
    def _select_zones(
        cfg: AppSettings, camera_id: str | None
    ) -> tuple[ZoneConfig, ...]:
        if camera_id is not None:
            for camera in cfg.cameras:
                if camera.id == camera_id:
                    return tuple(camera.zones)
            raise ValueError(f"camera not found in configuration: {camera_id}")
        return tuple(cfg.cameras[0].zones) if cfg.cameras else ()

    def __enter__(self) -> PoseEngine:
        return self

    def __exit__(self, *_: object) -> None:
        self.close()
