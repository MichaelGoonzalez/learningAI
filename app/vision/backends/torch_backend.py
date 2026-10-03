"""Backend Ultralytics sobre PyTorch CUDA o CPU."""

from __future__ import annotations

import importlib
from pathlib import Path
from typing import Any

import numpy as np

from app.core.config import ModelConfig
from app.hardware.detect import DeviceInfo
from app.vision.backends.base import BackendDetections, InferenceBackend


class TorchCudaBackend(InferenceBackend):
    def __init__(self, cfg: ModelConfig) -> None:
        self._cfg = cfg
        self._model: Any = None
        self._device = "cpu"

    def load(self, model: str | Path, device: DeviceInfo) -> None:
        try:
            ultralytics = importlib.import_module("ultralytics")
        except Exception as exc:
            raise RuntimeError("Ultralytics/PyTorch runtime is not installed") from exc
        self._device = (
            f"cuda:{device.device_index}" if device.backend == "cuda" else "cpu"
        )
        self._model = ultralytics.YOLO(str(model))

    def infer(self, frame: np.ndarray) -> BackendDetections:
        if self._model is None:
            raise RuntimeError("backend is not loaded")
        results = self._model.predict(
            frame,
            conf=self._cfg.confidence,
            iou=self._cfg.iou,
            imgsz=self._cfg.image_size,
            device=self._device,
            half=self._cfg.half_precision and self._device.startswith("cuda"),
            verbose=False,
        )
        if not results:
            return BackendDetections.empty()
        result = results[0]
        boxes = getattr(result, "boxes", None)
        if boxes is None or len(boxes) == 0:
            return BackendDetections.empty()
        xyxy = boxes.xyxy.detach().cpu().numpy().astype(np.float32, copy=False)
        scores = boxes.conf.detach().cpu().numpy().astype(np.float32, copy=False)
        pose = getattr(result, "keypoints", None)
        if pose is None or pose.data is None:
            keypoints = np.zeros((len(xyxy), 17, 3), dtype=np.float32)
        else:
            keypoints = pose.data.detach().cpu().numpy().astype(np.float32, copy=False)
        return BackendDetections(xyxy, scores, keypoints)

    def close(self) -> None:
        self._model = None
        if self._device.startswith("cuda"):
            try:
                torch = importlib.import_module("torch")
                torch.cuda.empty_cache()
            except Exception:
                pass
