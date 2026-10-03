"""Backends intercambiables de inferencia de pose."""

from __future__ import annotations

from pathlib import Path

from app.core.config import ModelConfig
from app.hardware.detect import DeviceInfo, detect_runtimes
from app.vision.backends.base import BackendDetections, InferenceBackend
from app.vision.backends.onnx_backend import OnnxBackend
from app.vision.backends.torch_backend import TorchCudaBackend


def create_backend(
    cfg: ModelConfig,
    model: str | Path,
    device: DeviceInfo,
) -> InferenceBackend:
    """Construye y carga el backend apropiado para un destino concreto."""
    model_path = Path(model)
    runtimes = detect_runtimes()
    if device.backend == "directml":
        backend: InferenceBackend = OnnxBackend(cfg)
        model_path = _onnx_path(model_path)
    elif device.backend == "cuda":
        backend = TorchCudaBackend(cfg)
    elif model_path.suffix.lower() == ".onnx" or not runtimes.torch_installed:
        backend = OnnxBackend(cfg)
        model_path = _onnx_path(model_path)
    else:
        backend = TorchCudaBackend(cfg)
    try:
        backend.load(model_path, device)
    except Exception:
        backend.close()
        raise
    return backend


def _onnx_path(model_path: Path) -> Path:
    path = (
        model_path
        if model_path.suffix.lower() == ".onnx"
        else model_path.with_suffix(".onnx")
    )
    if not path.is_file():
        raise FileNotFoundError(
            f"ONNX model not found: {path}. Run scripts/export_onnx.py first."
        )
    return path


__all__ = [
    "BackendDetections",
    "InferenceBackend",
    "OnnxBackend",
    "TorchCudaBackend",
    "create_backend",
]
