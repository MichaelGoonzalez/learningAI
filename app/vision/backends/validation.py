"""Comparacion normalizada de resultados entre runtimes."""

from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from app.vision.backends.base import BackendDetections


@dataclass(frozen=True, slots=True)
class BackendComparison:
    matches: int
    max_keypoint_error_px: float
    within_tolerance: bool
    message: str


def compare_backend_outputs(
    reference: BackendDetections,
    candidate: BackendDetections,
    *,
    tolerance_px: float,
    keypoint_confidence: float,
) -> BackendComparison:
    """Asocia personas por IoU y compara keypoints visibles en pixeles."""
    if tolerance_px < 0:
        raise ValueError("tolerance_px must be non-negative")
    if not 0 <= keypoint_confidence <= 1:
        raise ValueError("keypoint_confidence must be between zero and one")
    if len(reference.boxes) != len(candidate.boxes):
        return BackendComparison(
            matches=0,
            max_keypoint_error_px=float("inf"),
            within_tolerance=False,
            message=(
                f"detection count differs: {len(reference.boxes)} != "
                f"{len(candidate.boxes)}"
            ),
        )
    if not len(reference.boxes):
        return BackendComparison(0, 0.0, True, "both backends returned no people")

    unmatched = set(range(len(candidate.boxes)))
    errors: list[float] = []
    matches = 0
    for reference_index, reference_box in enumerate(reference.boxes):
        candidate_index = max(
            unmatched,
            key=lambda index: _single_iou(reference_box, candidate.boxes[index]),
        )
        unmatched.remove(candidate_index)
        reference_points = reference.keypoints[reference_index]
        candidate_points = candidate.keypoints[candidate_index]
        visible = (
            (reference_points[:, 2] >= keypoint_confidence)
            & (candidate_points[:, 2] >= keypoint_confidence)
        )
        if np.any(visible):
            distances = np.linalg.norm(
                reference_points[visible, :2] - candidate_points[visible, :2],
                axis=1,
            )
            errors.extend(float(distance) for distance in distances)
        matches += 1

    max_error = max(errors, default=0.0)
    within_tolerance = max_error <= tolerance_px
    return BackendComparison(
        matches=matches,
        max_keypoint_error_px=max_error,
        within_tolerance=within_tolerance,
        message=(
            f"maximum keypoint error {max_error:.3f}px "
            f"(tolerance {tolerance_px:.3f}px)"
        ),
    )


def _single_iou(first: np.ndarray, second: np.ndarray) -> float:
    top_left = np.maximum(first[:2], second[:2])
    bottom_right = np.minimum(first[2:], second[2:])
    intersection = float(np.prod(np.maximum(bottom_right - top_left, 0)))
    first_area = float(np.prod(np.maximum(first[2:] - first[:2], 0)))
    second_area = float(np.prod(np.maximum(second[2:] - second[:2], 0)))
    union = first_area + second_area - intersection
    return intersection / union if union > 0 else 0.0

