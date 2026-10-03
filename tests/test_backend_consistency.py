from __future__ import annotations

import os

import numpy as np
import pytest

from app.core.config import ModelConfig
from app.hardware.detect import DeviceInfo
from app.vision.backends.base import BackendDetections
from app.vision.backends.validation import compare_backend_outputs


def output(keypoint_offset: float = 0.0) -> BackendDetections:
    keypoints = np.zeros((1, 17, 3), dtype=np.float32)
    keypoints[0, :, 0] = np.arange(17) + keypoint_offset
    keypoints[0, :, 1] = np.arange(17) * 2 + keypoint_offset
    keypoints[0, :, 2] = 0.9
    return BackendDetections(
        boxes=np.asarray([[10, 20, 110, 220]], dtype=np.float32),
        scores=np.asarray([0.9], dtype=np.float32),
        keypoints=keypoints,
    )


def test_backend_keypoints_match_inside_tolerance() -> None:
    comparison = compare_backend_outputs(
        output(),
        output(keypoint_offset=0.5),
        tolerance_px=1.0,
        keypoint_confidence=0.5,
    )
    assert comparison.within_tolerance
    assert comparison.matches == 1


def test_backend_keypoints_fail_outside_tolerance() -> None:
    comparison = compare_backend_outputs(
        output(),
        output(keypoint_offset=2.0),
        tolerance_px=1.0,
        keypoint_confidence=0.5,
    )
    assert not comparison.within_tolerance


INTEGRATION_MODEL = os.getenv("TEST_POSE_MODEL")
INTEGRATION_ONNX = os.getenv("TEST_POSE_ONNX")
INTEGRATION_IMAGE = os.getenv("TEST_POSE_IMAGE")


@pytest.mark.skipif(
    not all((INTEGRATION_MODEL, INTEGRATION_ONNX, INTEGRATION_IMAGE)),
    reason="define TEST_POSE_MODEL, TEST_POSE_ONNX and TEST_POSE_IMAGE",
)
def test_real_torch_and_onnx_outputs_match() -> None:
    import cv2

    from app.vision.backends.onnx_backend import OnnxBackend
    from app.vision.backends.torch_backend import TorchCudaBackend

    cfg = ModelConfig(
        weights=str(INTEGRATION_MODEL),
        device="cpu",
        image_size=640,
        confidence=0.35,
        iou=0.5,
        half_precision=False,
        tracker_config="bytetrack.yaml",
    )
    cpu = DeviceInfo(
        id="cpu",
        name="CPU",
        vendor="cpu",
        backend="cpu",
        device_index=0,
    )
    image = cv2.imread(str(INTEGRATION_IMAGE))
    assert image is not None
    torch_backend = TorchCudaBackend(cfg)
    onnx_backend = OnnxBackend(cfg)
    try:
        torch_backend.load(str(INTEGRATION_MODEL), cpu)
        onnx_backend.load(str(INTEGRATION_ONNX), cpu)
        comparison = compare_backend_outputs(
            torch_backend.infer(image),
            onnx_backend.infer(image),
            tolerance_px=float(os.getenv("TEST_POSE_TOLERANCE_PX", "3.0")),
            keypoint_confidence=0.5,
        )
        assert comparison.within_tolerance, comparison.message
    finally:
        torch_backend.close()
        onnx_backend.close()

