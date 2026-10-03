"""Deteccion tolerante a fallos de CPU, CUDA y adaptadores DirectX 12."""

from __future__ import annotations

import hashlib
import importlib
import json
import logging
import platform
import subprocess
from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field


LOGGER = logging.getLogger(__name__)
Vendor = Literal["nvidia", "amd", "intel", "cpu", "other"]
BackendName = Literal["cuda", "directml", "cpu"]


class DeviceInfo(BaseModel):
    """Destino de inferencia seleccionable por el usuario."""

    model_config = ConfigDict(frozen=True)

    id: str
    name: str
    vendor: Vendor
    backend: BackendName
    device_index: int = Field(ge=0)
    vram_mb: int | None = Field(default=None, ge=0)
    driver_version: str | None = None
    runtime_installed: bool = True
    runtime_message: str | None = None


class RuntimeInfo(BaseModel):
    """Inventario de runtimes importables y providers registrados."""

    model_config = ConfigDict(frozen=True)

    torch_installed: bool
    torch_version: str | None = None
    torch_cuda_available: bool = False
    onnxruntime_installed: bool
    onnxruntime_version: str | None = None
    onnxruntime_providers: list[str] = Field(default_factory=list)
    errors: list[str] = Field(default_factory=list)


def detect_runtimes() -> RuntimeInfo:
    errors: list[str] = []
    torch_installed = False
    torch_version: str | None = None
    torch_cuda_available = False
    try:
        torch = importlib.import_module("torch")
        torch_installed = True
        torch_version = str(getattr(torch, "__version__", "unknown"))
        torch_cuda_available = bool(torch.cuda.is_available())
    except Exception as exc:  # La deteccion nunca debe impedir el arranque.
        errors.append(f"torch: {exc}")

    ort_installed = False
    ort_version: str | None = None
    ort_providers: list[str] = []
    try:
        ort = importlib.import_module("onnxruntime")
        ort_installed = True
        ort_version = str(getattr(ort, "__version__", "unknown"))
        ort_providers = list(ort.get_available_providers())
    except Exception as exc:
        errors.append(f"onnxruntime: {exc}")

    return RuntimeInfo(
        torch_installed=torch_installed,
        torch_version=torch_version,
        torch_cuda_available=torch_cuda_available,
        onnxruntime_installed=ort_installed,
        onnxruntime_version=ort_version,
        onnxruntime_providers=ort_providers,
        errors=errors,
    )


def detect_devices(command_timeout_s: float = 3.0) -> list[DeviceInfo]:
    """Enumera destinos utilizables sin asumir que sus runtimes existen."""
    runtimes = detect_runtimes()
    devices = _detect_nvidia(command_timeout_s, runtimes)
    if not devices:
        devices.extend(_detect_nvidia_with_torch(runtimes))
    devices.extend(_detect_windows_adapters(command_timeout_s, runtimes))
    devices.append(_cpu_device(runtimes))

    unique: dict[str, DeviceInfo] = {}
    for device in devices:
        unique.setdefault(device.id, device)
    return list(unique.values())


def _detect_nvidia(timeout_s: float, runtimes: RuntimeInfo) -> list[DeviceInfo]:
    command = [
        "nvidia-smi",
        "--query-gpu=index,uuid,name,memory.total,driver_version",
        "--format=csv,noheader,nounits",
    ]
    try:
        completed = subprocess.run(
            command,
            capture_output=True,
            text=True,
            timeout=timeout_s,
            check=False,
            creationflags=_no_window_flag(),
        )
    except (FileNotFoundError, subprocess.SubprocessError, OSError):
        return []
    if completed.returncode != 0:
        LOGGER.debug("nvidia-smi no disponible: %s", completed.stderr.strip())
        return []

    devices: list[DeviceInfo] = []
    for line in completed.stdout.splitlines():
        fields = [field.strip() for field in line.split(",", maxsplit=4)]
        if len(fields) != 5:
            continue
        index, uuid, name, memory, driver = fields
        try:
            device_index = int(index)
            vram_mb = int(float(memory))
        except ValueError:
            continue
        devices.append(
            DeviceInfo(
                id=f"nvidia:{uuid}",
                name=name,
                vendor="nvidia",
                backend="cuda",
                device_index=device_index,
                vram_mb=vram_mb,
                driver_version=driver or None,
                runtime_installed=runtimes.torch_cuda_available,
                runtime_message=(
                    None
                    if runtimes.torch_cuda_available
                    else "PyTorch con CUDA no esta disponible"
                ),
            )
        )
    return devices


def _detect_nvidia_with_torch(runtimes: RuntimeInfo) -> list[DeviceInfo]:
    if not runtimes.torch_cuda_available:
        return []
    try:
        torch = importlib.import_module("torch")
        devices: list[DeviceInfo] = []
        for index in range(torch.cuda.device_count()):
            properties = torch.cuda.get_device_properties(index)
            uuid = getattr(properties, "uuid", None)
            stable_part = str(uuid or _stable_token(properties.name))
            devices.append(
                DeviceInfo(
                    id=f"nvidia:{stable_part}",
                    name=str(properties.name),
                    vendor="nvidia",
                    backend="cuda",
                    device_index=index,
                    vram_mb=int(properties.total_memory // (1024 * 1024)),
                    runtime_installed=True,
                )
            )
        return devices
    except Exception as exc:
        LOGGER.warning("No se pudo consultar CUDA con PyTorch: %s", exc)
        return []


def _detect_windows_adapters(
    timeout_s: float, runtimes: RuntimeInfo
) -> list[DeviceInfo]:
    if platform.system() != "Windows":
        return []
    adapters = _query_adapters_powershell(timeout_s)
    if not adapters:
        adapters = _query_adapters_wmic(timeout_s)

    provider_available = "DmlExecutionProvider" in runtimes.onnxruntime_providers
    devices: list[DeviceInfo] = []
    for index, adapter in enumerate(adapters):
        name = str(adapter.get("Name") or "GPU DirectX")
        pnp_id = str(adapter.get("PNPDeviceID") or name)
        memory_bytes = _optional_int(adapter.get("AdapterRAM"))
        devices.append(
            DeviceInfo(
                id=f"directml:{_stable_token(pnp_id)}",
                name=name,
                vendor=_vendor_from_name(name),
                backend="directml",
                device_index=index,
                vram_mb=(memory_bytes // (1024 * 1024) if memory_bytes else None),
                driver_version=_optional_string(adapter.get("DriverVersion")),
                runtime_installed=provider_available,
                runtime_message=(
                    None
                    if provider_available
                    else "Falta ONNX Runtime con DmlExecutionProvider"
                ),
            )
        )
    return devices


def _query_adapters_powershell(timeout_s: float) -> list[dict[str, Any]]:
    script = (
        "Get-CimInstance Win32_VideoController | "
        "Select-Object Name,AdapterRAM,DriverVersion,PNPDeviceID | "
        "ConvertTo-Json -Compress"
    )
    try:
        completed = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", script],
            capture_output=True,
            text=True,
            timeout=timeout_s,
            check=False,
            creationflags=_no_window_flag(),
        )
    except (FileNotFoundError, subprocess.SubprocessError, OSError):
        return []
    if completed.returncode != 0 or not completed.stdout.strip():
        return []
    try:
        payload = json.loads(completed.stdout)
    except json.JSONDecodeError:
        return []
    if isinstance(payload, dict):
        return [payload]
    return [item for item in payload if isinstance(item, dict)]


def _query_adapters_wmic(timeout_s: float) -> list[dict[str, Any]]:
    try:
        completed = subprocess.run(
            [
                "wmic",
                "path",
                "win32_VideoController",
                "get",
                "Name,AdapterRAM,DriverVersion,PNPDeviceID",
                "/format:csv",
            ],
            capture_output=True,
            text=True,
            timeout=timeout_s,
            check=False,
            creationflags=_no_window_flag(),
        )
    except (FileNotFoundError, subprocess.SubprocessError, OSError):
        return []
    if completed.returncode != 0:
        return []
    lines = [line.strip() for line in completed.stdout.splitlines() if line.strip()]
    if len(lines) < 2:
        return []
    headers = [header.strip() for header in lines[0].split(",")]
    return [dict(zip(headers, line.split(","))) for line in lines[1:]]


def _cpu_device(runtimes: RuntimeInfo) -> DeviceInfo:
    runtime_installed = runtimes.torch_installed or (
        "CPUExecutionProvider" in runtimes.onnxruntime_providers
    )
    return DeviceInfo(
        id="cpu",
        name=f"CPU ({platform.processor() or platform.machine() or 'generic'})",
        vendor="cpu",
        backend="cpu",
        device_index=0,
        runtime_installed=runtime_installed,
        runtime_message=None if runtime_installed else "Falta PyTorch u ONNX Runtime CPU",
    )


def _vendor_from_name(name: str) -> Vendor:
    normalized = name.lower()
    if "nvidia" in normalized:
        return "nvidia"
    if "amd" in normalized or "radeon" in normalized:
        return "amd"
    if "intel" in normalized:
        return "intel"
    return "other"


def _stable_token(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8", errors="replace")).hexdigest()[:16]


def _optional_int(value: Any) -> int | None:
    try:
        return int(value) if value not in (None, "") else None
    except (TypeError, ValueError):
        return None


def _optional_string(value: Any) -> str | None:
    return str(value) if value not in (None, "") else None


def _no_window_flag() -> int:
    return int(getattr(subprocess, "CREATE_NO_WINDOW", 0))

