from __future__ import annotations

from pathlib import Path

import pytest

from app.hardware import detect as hardware_detect
from app.hardware.detect import DeviceInfo, RuntimeInfo
from app.hardware.selection import (
    DeviceSelection,
    load_selection,
    recommend_device,
    resolve_selection,
    save_selection,
)
from app.core.config import HardwareConfig, load_settings


def device(
    device_id: str,
    backend: str,
    *,
    runtime_installed: bool = True,
    vram_mb: int | None = None,
) -> DeviceInfo:
    vendor = "nvidia" if backend == "cuda" else "amd" if backend == "directml" else "cpu"
    return DeviceInfo(
        id=device_id,
        name=device_id,
        vendor=vendor,
        backend=backend,
        device_index=0,
        vram_mb=vram_mb,
        runtime_installed=runtime_installed,
    )


def test_recommendation_prefers_nvidia_then_directml_then_cpu() -> None:
    devices = [
        device("cpu", "cpu"),
        device("amd", "directml", vram_mb=8192),
        device("nvidia", "cuda", vram_mb=4096),
    ]
    assert recommend_device(devices).id == "nvidia"


def test_recommendation_ignores_device_with_missing_runtime() -> None:
    devices = [
        device("cpu", "cpu"),
        device("nvidia", "cuda", runtime_installed=False),
    ]
    assert recommend_device(devices).id == "cpu"


def test_selection_round_trip(tmp_path: Path) -> None:
    path = tmp_path / "settings.json"
    save_selection(path, DeviceSelection(device_id="gpu-stable-id"))
    assert load_selection(path) == DeviceSelection(device_id="gpu-stable-id")


def test_missing_saved_gpu_falls_back_to_cpu(tmp_path: Path) -> None:
    path = tmp_path / "settings.json"
    save_selection(path, DeviceSelection(device_id="removed-gpu"))
    selected, used_fallback = resolve_selection(
        [device("cpu", "cpu")],
        path,
    )
    assert selected.id == "cpu"
    assert used_fallback


def test_detection_always_contains_cpu(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    runtimes = RuntimeInfo(
        torch_installed=False,
        onnxruntime_installed=False,
    )
    monkeypatch.setattr(hardware_detect, "detect_runtimes", lambda: runtimes)
    monkeypatch.setattr(hardware_detect, "_detect_nvidia", lambda *_: [])
    monkeypatch.setattr(hardware_detect, "_detect_nvidia_with_torch", lambda *_: [])
    monkeypatch.setattr(hardware_detect, "_detect_windows_adapters", lambda *_: [])
    devices = hardware_detect.detect_devices()
    assert [item.id for item in devices] == ["cpu"]
    assert not devices[0].runtime_installed


def test_runtime_detection_survives_missing_optional_packages(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def missing_import(_: str) -> object:
        raise ModuleNotFoundError("not installed")

    monkeypatch.setattr(hardware_detect.importlib, "import_module", missing_import)
    runtime = hardware_detect.detect_runtimes()
    assert not runtime.torch_installed
    assert not runtime.onnxruntime_installed
    assert len(runtime.errors) == 2


def test_engine_changes_device_without_restart(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
) -> None:
    import app.vision.engine as engine_module

    cpu = device("cpu", "cpu")
    gpu = device("gpu", "directml")
    created_backends = []

    class FakeBackend:
        def __init__(self) -> None:
            self.closed = False

        def close(self) -> None:
            self.closed = True

    class FakeTracker:
        def __init__(self, _: str) -> None:
            pass

    def create_fake_backend(*_: object) -> FakeBackend:
        backend = FakeBackend()
        created_backends.append(backend)
        return backend

    monkeypatch.setattr(engine_module, "detect_devices", lambda: [cpu, gpu])
    monkeypatch.setattr(
        engine_module,
        "resolve_selection",
        lambda *_: (cpu, False),
    )
    monkeypatch.setattr(engine_module, "create_backend", create_fake_backend)
    monkeypatch.setattr(engine_module, "_ByteTrackAdapter", FakeTracker)
    settings = load_settings().model_copy(
        update={
            "hardware": HardwareConfig(settings_path=tmp_path / "settings.json")
        }
    )
    engine = engine_module.PoseEngine(settings)
    old_backend = created_backends[0]

    selected = engine.reload_device("gpu")

    assert selected.id == "gpu"
    assert engine.active_device.id == "gpu"
    assert old_backend.closed
    assert load_selection(tmp_path / "settings.json") == DeviceSelection(
        device_id="gpu"
    )
