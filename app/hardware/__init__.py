"""Deteccion y seleccion persistente de hardware de inferencia."""

from app.hardware.detect import DeviceInfo, RuntimeInfo, detect_devices, detect_runtimes
from app.hardware.selection import DeviceSelection, recommend_device

__all__ = [
    "DeviceInfo",
    "DeviceSelection",
    "RuntimeInfo",
    "detect_devices",
    "detect_runtimes",
    "recommend_device",
]

