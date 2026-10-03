"""Carga y validacion de la configuracion de la aplicacion."""

from __future__ import annotations

import os
import re
from functools import lru_cache
from pathlib import Path
from typing import Any, Literal

import yaml
from dotenv import load_dotenv
from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator


ENV_PREFIX = "HAND_RAISE__"
ENV_NESTED_DELIMITER = "__"
DEFAULT_CONFIG_PATH = Path("config.yaml")
ENV_VARIABLE_PATTERN = re.compile(
    r"\$\{(?P<name>[A-Za-z_][A-Za-z0-9_]*)(?::-\s*(?P<default>[^}]*))?\}"
)


class StrictModel(BaseModel):
    """Modelo base que rechaza errores tipograficos en la configuracion."""

    model_config = ConfigDict(extra="forbid")


class ZoneConfig(StrictModel):
    name: str = Field(min_length=1)
    points: list[tuple[int, int]] = Field(min_length=3)

    @field_validator("points")
    @classmethod
    def validate_distinct_points(
        cls, points: list[tuple[int, int]]
    ) -> list[tuple[int, int]]:
        if len(set(points)) < 3:
            raise ValueError("a zone requires at least three distinct points")
        return points


class CameraConfig(StrictModel):
    id: str = Field(min_length=1, pattern=r"^[A-Za-z0-9_-]+$")
    name: str = Field(min_length=1)
    source: str | int
    enabled: bool
    loop: bool = False
    zone_filter_enabled: bool
    zones: list[ZoneConfig]


class ModelConfig(StrictModel):
    weights: str = Field(min_length=1)
    device: str = Field(min_length=1)
    image_size: int = Field(gt=0)
    confidence: float = Field(ge=0.0, le=1.0)
    iou: float = Field(ge=0.0, le=1.0)
    half_precision: bool
    tracker_config: str = Field(min_length=1)


class DetectionConfig(StrictModel):
    keypoint_confidence: float = Field(ge=0.0, le=1.0)
    shoulder_margin_px: float | None = Field(default=None, ge=0)
    shoulder_margin_ratio: float | None = Field(default=None, ge=0)
    strict_mode: bool
    strict_margin_px: float = Field(ge=0)
    zone_reference: Literal["bbox_center", "shoulder_midpoint"] = "shoulder_midpoint"
    consecutive_frames: int = Field(gt=0)
    hold_time_ms: int | None = Field(default=None, gt=0)
    lower_consecutive_frames: int = Field(gt=0)
    cooldown_ms: int = Field(ge=0)
    track_ttl_ms: int = Field(gt=0)

    @model_validator(mode="after")
    def validate_shoulder_margin(self) -> DetectionConfig:
        configured_margins = (
            self.shoulder_margin_px is not None,
            self.shoulder_margin_ratio is not None,
        )
        if sum(configured_margins) != 1:
            raise ValueError(
                "configure exactly one of shoulder_margin_px or "
                "shoulder_margin_ratio"
            )
        return self


class RuntimeConfig(StrictModel):
    batch_size: int = Field(gt=0)
    inference_interval_ms: int = Field(ge=0)
    capture_buffer_size: int = Field(gt=0)
    reconnect_delay_ms: int = Field(gt=0)
    max_reconnect_delay_ms: int = Field(gt=0)
    fallback_fps: float = Field(default=30.0, gt=0)
    jpeg_quality: int = Field(ge=1, le=100)

    @model_validator(mode="after")
    def validate_reconnect_delays(self) -> RuntimeConfig:
        if self.max_reconnect_delay_ms < self.reconnect_delay_ms:
            raise ValueError(
                "max_reconnect_delay_ms must be greater than or equal to "
                "reconnect_delay_ms"
            )
        return self


class StorageConfig(StrictModel):
    database_url: str = Field(min_length=1)
    snapshots_dir: Path
    snapshot_quality: int = Field(ge=1, le=100)


class ApiConfig(StrictModel):
    host: str = Field(min_length=1)
    port: int = Field(ge=1, le=65535)
    cors_origins: list[str]
    api_key: str | None


class HardwareConfig(StrictModel):
    settings_path: Path | None = None


class AppSettings(StrictModel):
    model: ModelConfig
    detection: DetectionConfig
    runtime: RuntimeConfig
    cameras: list[CameraConfig] = Field(min_length=1)
    storage: StorageConfig
    api: ApiConfig
    hardware: HardwareConfig = Field(default_factory=HardwareConfig)

    @field_validator("cameras")
    @classmethod
    def validate_unique_camera_ids(
        cls, cameras: list[CameraConfig]
    ) -> list[CameraConfig]:
        camera_ids = [camera.id for camera in cameras]
        if len(camera_ids) != len(set(camera_ids)):
            raise ValueError("camera ids must be unique")
        return cameras


def _parse_env_value(raw_value: str) -> Any:
    """Interpreta escalares y colecciones usando la sintaxis YAML."""
    parsed = yaml.safe_load(raw_value)
    return raw_value if parsed is None and raw_value.strip() else parsed


def _expand_environment_variables(value: Any) -> Any:
    """Expande ${VAR} y ${VAR:-default} de forma recursiva en el YAML."""
    if isinstance(value, dict):
        return {
            key: _expand_environment_variables(child)
            for key, child in value.items()
        }
    if isinstance(value, list):
        return [_expand_environment_variables(child) for child in value]
    if not isinstance(value, str):
        return value

    def replace(match: re.Match[str]) -> str:
        variable_name = match.group("name")
        environment_value = os.getenv(variable_name)
        if environment_value is not None:
            return environment_value
        default = match.group("default")
        if default is not None:
            return default
        raise ValueError(f"required environment variable is not set: {variable_name}")

    return ENV_VARIABLE_PATTERN.sub(replace, value)


def _apply_environment_overrides(config: dict[str, Any]) -> dict[str, Any]:
    """Aplica HAND_RAISE__SECTION__KEY sobre un diccionario de configuracion."""
    for env_key, raw_value in os.environ.items():
        if not env_key.startswith(ENV_PREFIX):
            continue

        key_path = env_key.removeprefix(ENV_PREFIX).lower().split(
            ENV_NESTED_DELIMITER
        )
        if not all(key_path):
            raise ValueError(f"invalid environment key: {env_key}")

        target = config
        for key in key_path[:-1]:
            existing = target.get(key)
            if existing is None:
                target[key] = {}
            elif not isinstance(existing, dict):
                raise ValueError(
                    f"environment key {env_key} traverses non-mapping value {key}"
                )
            target = target[key]
        target[key_path[-1]] = _parse_env_value(raw_value)
    return config


def load_settings(config_path: str | Path = DEFAULT_CONFIG_PATH) -> AppSettings:
    """Carga .env, combina sus overrides con YAML y valida el resultado."""
    load_dotenv()
    path = Path(config_path)
    if not path.is_file():
        raise FileNotFoundError(f"configuration file not found: {path.resolve()}")

    with path.open("r", encoding="utf-8") as config_file:
        raw_config = yaml.safe_load(config_file) or {}
    if not isinstance(raw_config, dict):
        raise ValueError("the root of the configuration file must be a mapping")

    expanded_config = _expand_environment_variables(raw_config)
    merged_config = _apply_environment_overrides(expanded_config)
    return AppSettings.model_validate(merged_config)


@lru_cache(maxsize=1)
def get_settings() -> AppSettings:
    """Retorna una unica instancia validada para el proceso."""
    return load_settings()


if __name__ == "__main__":
    loaded = load_settings()
    print(
        "Valid configuration: "
        f"{len(loaded.cameras)} camera(s), "
        f"device={loaded.model.device}, "
        f"database={loaded.storage.database_url}"
    )
