from __future__ import annotations

from datetime import datetime, timedelta, timezone

from app.core.config import DetectionConfig
from app.vision.rules import HandsState, RaiseHandTracker


START = datetime(2026, 1, 1, tzinfo=timezone.utc)
UP = HandsState(left_raised=True, right_raised=False, confidence=0.9)
DOWN = HandsState(left_raised=False, right_raised=False, confidence=0.0)


def config(**overrides: object) -> DetectionConfig:
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


def at(milliseconds: int) -> datetime:
    return START + timedelta(milliseconds=milliseconds)


def test_one_or_two_frame_blink_does_not_emit() -> None:
    tracker = RaiseHandTracker(config())
    events = []
    events += tracker.update(1, UP, at(0))
    events += tracker.update(1, UP, at(10))
    events += tracker.update(1, DOWN, at(20))
    assert events == []


def test_n_frames_emit_exactly_one_raised_event() -> None:
    tracker = RaiseHandTracker(config())
    events = []
    for frame in range(3):
        events += tracker.update(1, UP, at(frame * 10))
    assert len(events) == 1
    assert events[0].type == "hand_raised"
    assert events[0].hand == "left"


def test_holding_hand_up_does_not_duplicate() -> None:
    tracker = RaiseHandTracker(config())
    events = []
    for frame in range(10):
        events += tracker.update(1, UP, at(frame * 10))
    assert [event.type for event in events] == ["hand_raised"]


def test_lowering_emits_after_configured_frames() -> None:
    tracker = RaiseHandTracker(config())
    for frame in range(3):
        tracker.update(1, UP, at(frame * 10))
    assert tracker.update(1, DOWN, at(30)) == []
    events = tracker.update(1, DOWN, at(40))
    assert len(events) == 1
    assert events[0].type == "hand_lowered"
    assert events[0].hand == "left"


def test_cooldown_delays_next_event_without_dropping_it() -> None:
    tracker = RaiseHandTracker(
        config(consecutive_frames=1, lower_consecutive_frames=1, cooldown_ms=100)
    )
    raised = tracker.update(1, UP, at(0))
    assert len(raised) == 1
    assert tracker.update(1, DOWN, at(10)) == []
    assert tracker.update(1, DOWN, at(99)) == []
    lowered = tracker.update(1, DOWN, at(100))
    assert [event.type for event in lowered] == ["hand_lowered"]


def test_two_tracks_are_independent() -> None:
    tracker = RaiseHandTracker(config(consecutive_frames=2))
    assert tracker.update(1, UP, at(0)) == []
    assert tracker.update(2, UP, at(0)) == []
    first = tracker.update(1, UP, at(10))
    second = tracker.update(2, UP, at(20))
    assert [event.track_id for event in first] == [1]
    assert [event.track_id for event in second] == [2]


def test_prune_removes_track_progress() -> None:
    tracker = RaiseHandTracker(config(consecutive_frames=2))
    tracker.update(1, UP, at(0))
    tracker.prune(set())
    assert tracker.update(1, UP, at(10)) == []
    events = tracker.update(1, UP, at(20))
    assert len(events) == 1


def test_hold_time_can_replace_frame_threshold() -> None:
    tracker = RaiseHandTracker(config(consecutive_frames=99, hold_time_ms=100))
    assert tracker.update(1, UP, at(0)) == []
    assert tracker.update(1, UP, at(99)) == []
    assert len(tracker.update(1, UP, at(100))) == 1

