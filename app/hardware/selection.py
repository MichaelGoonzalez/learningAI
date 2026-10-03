"""Persistencia atomica y recomendacion del dispositivo de inferencia."""

from __future__ import annotations

import logging
import os
from pathlib import Path
from tempfile import NamedTemporaryFile

from pydantic import BaseModel, ConfigDict, ValidationError

from app.hardware.detect import DeviceInfo


LOGGER = logging.getLogger(__name__)
APP_DIRECTORY = "HandRaiseDetection"


class DeviceSelection(BaseModel):
    model_config = ConfigDict(extra="forbid")

    device_id: str


def default_settings_path() -> Path:
    local_app_data = os.getenv("LOCALAPPDATA")
    if local_app_data:
        return Path(local_app_data) / APP_DIRECTORY / "settings.json"
    return Path.home() / ".hand_raise_detection" / "settings.json"


def recommend_device(devices: list[DeviceInfo]) -> DeviceInfo:
    """Prioriza CUDA, luego DirectML y finalmente CPU, solo si es utilizable."""
    usable = [device for device in devices if device.runtime_installed]
    candidates = usable or devices
    if not candidates:
        raise ValueError("no inference devices were detected")
    priority = {"cuda": 0, "directml": 1, "cpu": 2}
    return min(
        candidates,
        key=lambda device: (
            priority[device.backend],
            -(device.vram_mb or 0),
            device.device_index,
        ),
    )


def load_selection(path: Path) -> DeviceSelection | None:
    if not path.is_file():
        return None
    try:
        return DeviceSelection.model_validate_json(path.read_text(encoding="utf-8"))
    except (OSError, ValidationError, ValueError) as exc:
        LOGGER.warning("Settings de hardware invalidos en %s: %s", path, exc)
        return None


def save_selection(path: Path, selection: DeviceSelection) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = selection.model_dump_json(indent=2)
    temporary_name: str | None = None
    try:
        with NamedTemporaryFile(
            "w",
            encoding="utf-8",
            dir=path.parent,
            prefix=f".{path.name}.",
            suffix=".tmp",
            delete=False,
        ) as temporary:
            temporary.write(payload)
            temporary.flush()
            os.fsync(temporary.fileno())
            temporary_name = temporary.name
        Path(temporary_name).replace(path)
    finally:
        if temporary_name is not None:
            Path(temporary_name).unlink(missing_ok=True)


def resolve_selection(
    devices: list[DeviceInfo], path: Path
) -> tuple[DeviceInfo, bool]:
    """Retorna dispositivo y si hubo fallback respecto a una eleccion guardada."""
    saved = load_selection(path)
    by_id = {device.id: device for device in devices}
    if saved is not None:
        selected = by_id.get(saved.device_id)
        if selected is not None and selected.runtime_installed:
            return selected, False
        LOGGER.warning(
            "El dispositivo guardado '%s' ya no esta disponible; se usara CPU",
            saved.device_id,
        )
        cpu = by_id.get("cpu")
        if cpu is None:
            raise RuntimeError("CPU device was not detected")
        return cpu, True
    return recommend_device(devices), False
