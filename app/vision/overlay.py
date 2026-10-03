"""Render reutilizable del resultado de pose sobre un frame."""

from __future__ import annotations

from collections.abc import Sequence

import cv2
import numpy as np

from app.core.config import ZoneConfig
from app.vision.engine import InferenceStats, PersonDetection


COCO_SKELETON = (
    (0, 1),
    (0, 2),
    (1, 3),
    (2, 4),
    (5, 6),
    (5, 7),
    (7, 9),
    (6, 8),
    (8, 10),
    (5, 11),
    (6, 12),
    (11, 12),
    (11, 13),
    (13, 15),
    (12, 14),
    (14, 16),
)

NORMAL_COLOR = (70, 200, 70)
RAISED_COLOR = (0, 80, 255)
SKELETON_COLOR = (255, 190, 40)
ZONE_COLOR = (220, 150, 40)
TEXT_COLOR = (255, 255, 255)


def draw(
    frame: np.ndarray,
    detections: Sequence[PersonDetection],
    zones: Sequence[ZoneConfig],
    stats: InferenceStats,
) -> np.ndarray:
    """Retorna una copia anotada sin modificar el frame de entrada."""
    annotated = frame.copy()
    _draw_zones(annotated, zones)
    for detection in detections:
        _draw_person(annotated, detection, stats.keypoint_confidence)
    _draw_stats(annotated, stats)
    return annotated


def _draw_zones(frame: np.ndarray, zones: Sequence[ZoneConfig]) -> None:
    for zone in zones:
        points = np.asarray(zone.points, dtype=np.int32).reshape((-1, 1, 2))
        cv2.polylines(frame, [points], True, ZONE_COLOR, 2, cv2.LINE_AA)
        label_origin = tuple(int(value) for value in points[0, 0])
        cv2.putText(
            frame,
            zone.name,
            label_origin,
            cv2.FONT_HERSHEY_SIMPLEX,
            0.6,
            ZONE_COLOR,
            2,
            cv2.LINE_AA,
        )


def _draw_person(
    frame: np.ndarray,
    detection: PersonDetection,
    keypoint_confidence: float,
) -> None:
    raised = detection.hands_state.any_raised
    color = RAISED_COLOR if raised else NORMAL_COLOR
    x1, y1, x2, y2 = (int(round(value)) for value in detection.bbox)
    cv2.rectangle(frame, (x1, y1), (x2, y2), color, 2)

    points = detection.keypoints
    for start_index, end_index in COCO_SKELETON:
        start = points[start_index]
        end = points[end_index]
        if start[2] < keypoint_confidence or end[2] < keypoint_confidence:
            continue
        cv2.line(
            frame,
            (int(round(start[0])), int(round(start[1]))),
            (int(round(end[0])), int(round(end[1]))),
            SKELETON_COLOR,
            2,
            cv2.LINE_AA,
        )
    for x, y, confidence in points:
        if confidence >= keypoint_confidence:
            cv2.circle(
                frame,
                (int(round(x)), int(round(y))),
                3,
                SKELETON_COLOR,
                -1,
                cv2.LINE_AA,
            )

    identity = f"ID {detection.track_id}" if detection.track_id is not None else "ID --"
    labels = [identity]
    hand = detection.hands_state.hand
    if hand is not None:
        hand_label = {"left": "L", "right": "R", "both": "AMBAS"}[hand]
        labels.append(f"MANO {hand_label}")
    if detection.zone is not None:
        labels.append(detection.zone)
    label = " | ".join(labels)
    text_y = max(y1 - 8, 20)
    cv2.putText(
        frame,
        label,
        (x1, text_y),
        cv2.FONT_HERSHEY_SIMPLEX,
        0.6,
        color,
        2,
        cv2.LINE_AA,
    )


def _draw_stats(frame: np.ndarray, stats: InferenceStats) -> None:
    label = f"FPS {stats.fps:.1f} | Latencia {stats.latency_ms:.1f} ms"
    cv2.putText(
        frame,
        label,
        (12, 26),
        cv2.FONT_HERSHEY_SIMPLEX,
        0.65,
        TEXT_COLOR,
        2,
        cv2.LINE_AA,
    )
