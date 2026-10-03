"""Reglas puras para evaluar poses y estabilizar eventos por track."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from enum import Enum
from typing import Literal

import numpy as np
from pydantic import BaseModel, ConfigDict, Field

from app.core.config import DetectionConfig


NOSE = 0
LEFT_EYE = 1
RIGHT_EYE = 2
LEFT_SHOULDER = 5
RIGHT_SHOULDER = 6
LEFT_WRIST = 9
RIGHT_WRIST = 10

HandLabel = Literal["left", "right", "both"]
EventType = Literal["hand_raised", "hand_lowered"]


@dataclass(frozen=True, slots=True)
class HandsState:
    """Resultado instantaneo de evaluar las manos de una persona."""

    left_raised: bool
    right_raised: bool
    confidence: float

    @property
    def any_raised(self) -> bool:
        return self.left_raised or self.right_raised

    @property
    def hand(self) -> HandLabel | None:
        if self.left_raised and self.right_raised:
            return "both"
        if self.left_raised:
            return "left"
        if self.right_raised:
            return "right"
        return None


class RaiseEvent(BaseModel):
    """Evento de dominio producido por la maquina de estados."""

    model_config = ConfigDict(frozen=True)

    type: EventType
    track_id: int
    hand: HandLabel
    confidence: float = Field(ge=0.0, le=1.0)
    timestamp: datetime


class TrackPhase(str, Enum):
    IDLE = "IDLE"
    RAISING = "RAISING"
    RAISED = "RAISED"
    LOWERING = "LOWERING"


@dataclass(slots=True)
class _TrackState:
    phase: TrackPhase = TrackPhase.IDLE
    raised_frames: int = 0
    lowered_frames: int = 0
    raising_since: datetime | None = None
    last_event_at: datetime | None = None
    raised_hand: HandLabel | None = None
    candidate_hand: HandLabel | None = None
    candidate_confidence: float = 0.0
    raised_confidence: float = 0.0
    last_update_at: datetime | None = None


def _validate_keypoints(keypoints: np.ndarray) -> np.ndarray:
    points = np.asarray(keypoints, dtype=float)
    if points.shape != (17, 3):
        raise ValueError(
            f"keypoints must have shape (17, 3), received {points.shape}"
        )
    return points


def _shoulder_margin(points: np.ndarray, cfg: DetectionConfig) -> float:
    if cfg.shoulder_margin_ratio is not None:
        shoulder_delta = points[LEFT_SHOULDER, :2] - points[RIGHT_SHOULDER, :2]
        shoulder_width = float(np.linalg.norm(shoulder_delta))
        return shoulder_width * cfg.shoulder_margin_ratio
    if cfg.shoulder_margin_px is None:
        raise ValueError("a shoulder margin must be configured")
    return cfg.shoulder_margin_px


def _strict_reference(
    points: np.ndarray, confidence_threshold: float
) -> tuple[float, float] | None:
    reference_indices = (LEFT_EYE, RIGHT_EYE, NOSE)
    valid_references = [
        points[index]
        for index in reference_indices
        if points[index, 2] >= confidence_threshold
    ]
    if not valid_references:
        return None
    reference_y = min(float(point[1]) for point in valid_references)
    reference_confidence = min(float(point[2]) for point in valid_references)
    return reference_y, reference_confidence


def _evaluate_side(
    points: np.ndarray,
    wrist_index: int,
    shoulder_index: int,
    cfg: DetectionConfig,
    shoulder_margin: float,
    strict_reference: tuple[float, float] | None,
) -> tuple[bool, float]:
    wrist = points[wrist_index]
    shoulder = points[shoulder_index]
    threshold = cfg.keypoint_confidence
    if wrist[2] < threshold or shoulder[2] < threshold:
        return False, 0.0

    confidence = min(float(wrist[2]), float(shoulder[2]))
    if cfg.strict_mode:
        if strict_reference is None:
            return False, 0.0
        reference_y, reference_confidence = strict_reference
        raised = bool(wrist[1] < reference_y - cfg.strict_margin_px)
        return raised, min(confidence, reference_confidence) if raised else 0.0

    raised = bool(wrist[1] < shoulder[1] - shoulder_margin)
    return raised, confidence if raised else 0.0


def evaluate_hands(keypoints: np.ndarray, cfg: DetectionConfig) -> HandsState:
    """Evalua una pose COCO de una persona sin mantener estado temporal."""
    points = _validate_keypoints(keypoints)
    margin = _shoulder_margin(points, cfg)
    strict_reference = (
        _strict_reference(points, cfg.keypoint_confidence)
        if cfg.strict_mode
        else None
    )

    left_raised, left_confidence = _evaluate_side(
        points,
        LEFT_WRIST,
        LEFT_SHOULDER,
        cfg,
        margin,
        strict_reference,
    )
    right_raised, right_confidence = _evaluate_side(
        points,
        RIGHT_WRIST,
        RIGHT_SHOULDER,
        cfg,
        margin,
        strict_reference,
    )
    raised_confidences = [
        confidence
        for raised, confidence in (
            (left_raised, left_confidence),
            (right_raised, right_confidence),
        )
        if raised
    ]
    confidence = min(raised_confidences) if raised_confidences else 0.0
    return HandsState(left_raised, right_raised, confidence)


class RaiseHandTracker:
    """Maquina de estados temporal independiente para cada track."""

    def __init__(self, cfg: DetectionConfig) -> None:
        self._cfg = cfg
        self._tracks: dict[int, _TrackState] = {}

    def update(
        self,
        track_id: int,
        hands_state: HandsState,
        timestamp: datetime,
    ) -> list[RaiseEvent]:
        state = self._tracks.setdefault(track_id, _TrackState())
        if state.last_update_at is not None and timestamp < state.last_update_at:
            raise ValueError("timestamps must be monotonic for each track")
        state.last_update_at = timestamp

        if state.phase is TrackPhase.IDLE:
            if hands_state.any_raised:
                self._start_raising(state, hands_state, timestamp)
            return self._try_raise(track_id, state, timestamp)

        if state.phase is TrackPhase.RAISING:
            if not hands_state.any_raised:
                self._reset_to_idle(state)
                return []
            state.raised_frames += 1
            state.candidate_hand = hands_state.hand
            state.candidate_confidence = hands_state.confidence
            return self._try_raise(track_id, state, timestamp)

        if state.phase is TrackPhase.RAISED:
            if not hands_state.any_raised:
                state.phase = TrackPhase.LOWERING
                state.lowered_frames = 1
            return self._try_lower(track_id, state, timestamp)

        if hands_state.any_raised:
            state.phase = TrackPhase.RAISED
            state.lowered_frames = 0
            return []
        state.lowered_frames += 1
        return self._try_lower(track_id, state, timestamp)

    def prune(self, active_track_ids: set[int]) -> None:
        """Elimina inmediatamente el estado de tracks que ya no estan activos."""
        self._tracks = {
            track_id: state
            for track_id, state in self._tracks.items()
            if track_id in active_track_ids
        }

    def _start_raising(
        self,
        state: _TrackState,
        hands_state: HandsState,
        timestamp: datetime,
    ) -> None:
        state.phase = TrackPhase.RAISING
        state.raised_frames = 1
        state.raising_since = timestamp
        state.candidate_hand = hands_state.hand
        state.candidate_confidence = hands_state.confidence

    def _raise_condition_met(self, state: _TrackState, timestamp: datetime) -> bool:
        if self._cfg.hold_time_ms is not None:
            if state.raising_since is None:
                return False
            elapsed_ms = (timestamp - state.raising_since).total_seconds() * 1000
            return elapsed_ms >= self._cfg.hold_time_ms
        return state.raised_frames >= self._cfg.consecutive_frames

    def _cooldown_elapsed(self, state: _TrackState, timestamp: datetime) -> bool:
        if state.last_event_at is None:
            return True
        elapsed_ms = (timestamp - state.last_event_at).total_seconds() * 1000
        return elapsed_ms >= self._cfg.cooldown_ms

    def _try_raise(
        self,
        track_id: int,
        state: _TrackState,
        timestamp: datetime,
    ) -> list[RaiseEvent]:
        if not self._raise_condition_met(state, timestamp):
            return []
        if not self._cooldown_elapsed(state, timestamp):
            return []
        if state.candidate_hand is None:
            return []

        event = RaiseEvent(
            type="hand_raised",
            track_id=track_id,
            hand=state.candidate_hand,
            confidence=state.candidate_confidence,
            timestamp=timestamp,
        )
        state.phase = TrackPhase.RAISED
        state.raised_hand = state.candidate_hand
        state.raised_confidence = state.candidate_confidence
        state.last_event_at = timestamp
        state.raised_frames = 0
        state.raising_since = None
        return [event]

    def _try_lower(
        self,
        track_id: int,
        state: _TrackState,
        timestamp: datetime,
    ) -> list[RaiseEvent]:
        if state.phase is not TrackPhase.LOWERING:
            return []
        if state.lowered_frames < self._cfg.lower_consecutive_frames:
            return []
        if not self._cooldown_elapsed(state, timestamp):
            return []
        if state.raised_hand is None:
            self._reset_to_idle(state)
            return []

        event = RaiseEvent(
            type="hand_lowered",
            track_id=track_id,
            hand=state.raised_hand,
            confidence=state.raised_confidence,
            timestamp=timestamp,
        )
        state.last_event_at = timestamp
        self._reset_to_idle(state)
        return [event]

    @staticmethod
    def _reset_to_idle(state: _TrackState) -> None:
        state.phase = TrackPhase.IDLE
        state.raised_frames = 0
        state.lowered_frames = 0
        state.raising_since = None
        state.raised_hand = None
        state.candidate_hand = None
        state.candidate_confidence = 0.0
        state.raised_confidence = 0.0
