"""Router aislado para inventario y seleccion de hardware."""

from __future__ import annotations

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel, ConfigDict
from starlette.concurrency import run_in_threadpool

from app.hardware.detect import DeviceInfo, RuntimeInfo
from app.vision.engine import PoseEngine


class DevicesResponse(BaseModel):
    model_config = ConfigDict(frozen=True)

    devices: list[DeviceInfo]
    active_device_id: str


class DeviceChangeRequest(BaseModel):
    device_id: str


class DeviceChangeResponse(BaseModel):
    success: bool
    active_device: DeviceInfo


def create_system_router(engine: PoseEngine) -> APIRouter:
    """Crea las rutas sin introducir estado global ni arrancar FastAPI."""
    router = APIRouter(prefix="/system", tags=["system"])

    @router.get("/devices", response_model=DevicesResponse)
    async def get_devices() -> DevicesResponse:
        devices = await run_in_threadpool(engine.refresh_devices)
        return DevicesResponse(
            devices=list(devices),
            active_device_id=engine.active_device.id,
        )

    @router.put("/device", response_model=DeviceChangeResponse)
    async def change_device(payload: DeviceChangeRequest) -> DeviceChangeResponse:
        try:
            active = await run_in_threadpool(
                engine.reload_device, payload.device_id
            )
        except (FileNotFoundError, RuntimeError, ValueError) as exc:
            raise HTTPException(status_code=409, detail=str(exc)) from exc
        return DeviceChangeResponse(success=True, active_device=active)

    @router.get("/runtime", response_model=RuntimeInfo)
    async def get_runtime() -> RuntimeInfo:
        return await run_in_threadpool(lambda: engine.runtime_info)

    return router

