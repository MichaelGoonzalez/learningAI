from __future__ import annotations

import numpy as np

from app.core.config import DetectionConfig
from app.vision.rules import evaluate_hands


def detection_config(**overrides: object) -> DetectionConfig:
    values: dict[str, object] = {
        "keypoint_confidence": 0.5,
        "shoulder_margin_px": None,
        "shoulder_margin_ratio": 0.2,
        "strict_mode": False,
        "strict_margin_px": 0,
        "consecutive_frames": 3,
        "hold_time_ms": None,
        "lower_consecutive_frames": 2,
        "cooldown_ms": 0,
        "track_ttl_ms": 1000,
    }
    values.update(overrides)
    return DetectionConfig(**values)


def pose(scale: float = 1.0) -> np.ndarray:
    points = np.zeros((17, 3), dtype=float)
    points[:, 2] = 0.9
    points[0, :2] = (150, 80)
    points[1, :2] = (140, 75)
    points[2, :2] = (160, 75)
    points[5, :2] = (100, 150)
    points[6, :2] = (200, 150)
    points[9, :2] = (100, 190)
    points[10, :2] = (200, 190)
    points[:, :2] *= scale
    return points


def test_hands_below_shoulders_are_not_raised() -> None:
    state = evaluate_hands(pose(), detection_config())
    assert not state.left_raised
    assert not state.right_raised
    assert state.confidence == 0.0


def test_wrist_at_shoulder_height_is_not_raised() -> None:
    points = pose()
    points[9, 1] = points[5, 1]
    assert not evaluate_hands(points, detection_config()).left_raised


def test_left_hand_above_shoulder() -> None:
    points = pose()
    points[9, 1] = 100
    state = evaluate_hands(points, detection_config())
    assert state.left_raised
    assert not state.right_raised
    assert state.hand == "left"


def test_right_hand_above_shoulder() -> None:
    points = pose()
    points[10, 1] = 100
    state = evaluate_hands(points, detection_config())
    assert not state.left_raised
    assert state.right_raised
    assert state.hand == "right"


def test_both_hands_above_shoulders() -> None:
    points = pose()
    points[9, 1] = 100
    points[10, 1] = 100
    state = evaluate_hands(points, detection_config())
    assert state.left_raised and state.right_raised
    assert state.hand == "both"
    assert state.confidence == 0.9


def test_missing_wrist_is_ignored() -> None:
    points = pose()
    points[9] = (0, 0, 0)
    assert not evaluate_hands(points, detection_config()).left_raised


def test_low_confidence_wrist_and_shoulder_are_ignored() -> None:
    points = pose()
    points[9, 1] = 50
    points[9, 2] = 0.49
    points[10, 1] = 50
    points[6, 2] = 0.49
    state = evaluate_hands(points, detection_config())
    assert not state.left_raised
    assert not state.right_raised


def test_strict_mode_requires_wrist_above_face() -> None:
    points = pose()
    points[9, 1] = 100
    shoulder_state = evaluate_hands(points, detection_config())
    strict_state = evaluate_hands(points, detection_config(strict_mode=True))
    assert shoulder_state.left_raised
    assert not strict_state.left_raised


def test_strict_mode_accepts_wrist_above_eyes_and_nose() -> None:
    points = pose()
    points[9, 1] = 60
    state = evaluate_hands(points, detection_config(strict_mode=True))
    assert state.left_raised


def test_fractional_margin_is_scale_invariant() -> None:
    near = pose(scale=1.0)
    near[9, 1] = 120
    far = pose(scale=0.5)
    far[9, 1] = 60
    cfg = detection_config(shoulder_margin_ratio=0.2)
    assert evaluate_hands(near, cfg).left_raised
    assert evaluate_hands(far, cfg).left_raised

