"""Contrato comun y tipos normalizados de los backends de pose."""

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from pathlib import Path

import numpy as np

from app.hardware.detect import DeviceInfo


@dataclass(frozen=True, slots=True)
class BackendDetections:
    boxes: np.ndarray
    scores: np.ndarray
    keypoints: np.ndarray

    @classmethod
    def empty(cls) -> BackendDetections:
        return cls(
            boxes=np.empty((0, 4), dtype=np.float32),
            scores=np.empty((0,), dtype=np.float32),
            keypoints=np.empty((0, 17, 3), dtype=np.float32),
        )


class InferenceBackend(ABC):
    @abstractmethod
    def load(self, model: str | Path, device: DeviceInfo) -> None:
        """Carga modelo y recursos del dispositivo."""

    @abstractmethod
    def infer(self, frame: np.ndarray) -> BackendDetections:
        """Retorna cajas, scores y keypoints en coordenadas del frame original."""

    @abstractmethod
    def close(self) -> None:
        """Libera recursos nativos del runtime."""

    def __enter__(self) -> InferenceBackend:
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

