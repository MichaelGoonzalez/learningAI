"""Backend ONNX Runtime para CUDA, DirectML y CPU."""

from __future__ import annotations

import importlib
from pathlib import Path
from typing import Any

import cv2
import numpy as np

from app.core.config import ModelConfig
from app.hardware.detect import DeviceInfo
from app.vision.backends.base import BackendDetections, InferenceBackend


KEYPOINT_COUNT = 17
KEYPOINT_VALUES = KEYPOINT_COUNT * 3


class OnnxBackend(InferenceBackend):
    def __init__(self, cfg: ModelConfig) -> None:
        self._cfg = cfg
        self._session: Any = None
        self._input_name = ""

    def load(self, model: str | Path, device: DeviceInfo) -> None:
        try:
            ort = importlib.import_module("onnxruntime")
        except Exception as exc:
            raise RuntimeError("ONNX Runtime is not installed") from exc

        providers = _providers_for(device)
        available = set(ort.get_available_providers())
        requested = providers[0][0] if isinstance(providers[0], tuple) else providers[0]
        if requested not in available:
            raise RuntimeError(
                f"ONNX provider {requested} is not available; installed: {sorted(available)}"
            )

        options = ort.SessionOptions()
        if device.backend == "directml":
            options.enable_mem_pattern = False
            options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
        self._session = ort.InferenceSession(
            str(model),
            sess_options=options,
            providers=providers,
        )
        self._input_name = self._session.get_inputs()[0].name

    def infer(self, frame: np.ndarray) -> BackendDetections:
        if self._session is None:
            raise RuntimeError("backend is not loaded")
        tensor, scale, padding = _preprocess(frame, self._cfg.image_size)
        outputs = self._session.run(None, {self._input_name: tensor})
        prediction = _find_prediction_output(outputs)
        return _postprocess(
            prediction,
            frame.shape[:2],
            scale,
            padding,
            self._cfg.confidence,
            self._cfg.iou,
        )

    def close(self) -> None:
        self._session = None
        self._input_name = ""


def _providers_for(device: DeviceInfo) -> list[str | tuple[str, dict[str, str]]]:
    index = str(device.device_index)
    if device.backend == "cuda":
        return [
            ("CUDAExecutionProvider", {"device_id": index}),
            "CPUExecutionProvider",
        ]
    if device.backend == "directml":
        return [
            ("DmlExecutionProvider", {"device_id": index}),
            "CPUExecutionProvider",
        ]
    return ["CPUExecutionProvider"]


def _preprocess(
    frame: np.ndarray, image_size: int
) -> tuple[np.ndarray, float, tuple[float, float]]:
    height, width = frame.shape[:2]
    scale = min(image_size / width, image_size / height)
    resized_width = int(round(width * scale))
    resized_height = int(round(height * scale))
    resized = cv2.resize(frame, (resized_width, resized_height))
    pad_width = image_size - resized_width
    pad_height = image_size - resized_height
    left = pad_width // 2
    top = pad_height // 2
    padded = cv2.copyMakeBorder(
        resized,
        top,
        pad_height - top,
        left,
        pad_width - left,
        cv2.BORDER_CONSTANT,
        value=(114, 114, 114),
    )
    rgb = cv2.cvtColor(padded, cv2.COLOR_BGR2RGB)
    tensor = np.ascontiguousarray(rgb.transpose(2, 0, 1)[None], dtype=np.float32)
    tensor /= 255.0
    return tensor, scale, (float(left), float(top))


def _find_prediction_output(outputs: list[np.ndarray]) -> np.ndarray:
    for output in outputs:
        array = np.asarray(output)
        if array.ndim == 3 and min(array.shape[1:]) >= 4 + 1 + KEYPOINT_VALUES:
            return array
    shapes = [np.asarray(output).shape for output in outputs]
    raise RuntimeError(f"unsupported ONNX pose outputs: {shapes}")


def _postprocess(
    raw_output: np.ndarray,
    original_shape: tuple[int, int],
    scale: float,
    padding: tuple[float, float],
    confidence_threshold: float,
    iou_threshold: float,
) -> BackendDetections:
    prediction = np.asarray(raw_output, dtype=np.float32).squeeze(0)
    if prediction.ndim != 2:
        raise RuntimeError(f"unexpected ONNX prediction shape: {prediction.shape}")
    if prediction.shape[0] < prediction.shape[1]:
        prediction = prediction.T

    feature_count = prediction.shape[1]
    class_count = feature_count - 4 - KEYPOINT_VALUES
    if class_count < 1:
        raise RuntimeError(
            f"ONNX output has {feature_count} features; expected pose output"
        )
    class_scores = prediction[:, 4 : 4 + class_count]
    scores = class_scores.max(axis=1)
    keep = scores >= confidence_threshold
    if not np.any(keep):
        return BackendDetections.empty()

    candidates = prediction[keep]
    scores = scores[keep]
    boxes = _xywh_to_xyxy(candidates[:, :4])
    selected = _nms(boxes, scores, iou_threshold)
    boxes = boxes[selected]
    scores = scores[selected]
    keypoints = candidates[selected, 4 + class_count :].reshape(-1, 17, 3)

    pad_x, pad_y = padding
    boxes[:, [0, 2]] = (boxes[:, [0, 2]] - pad_x) / scale
    boxes[:, [1, 3]] = (boxes[:, [1, 3]] - pad_y) / scale
    keypoints[:, :, 0] = (keypoints[:, :, 0] - pad_x) / scale
    keypoints[:, :, 1] = (keypoints[:, :, 1] - pad_y) / scale
    height, width = original_shape
    boxes[:, [0, 2]] = boxes[:, [0, 2]].clip(0, width)
    boxes[:, [1, 3]] = boxes[:, [1, 3]].clip(0, height)
    keypoints[:, :, 0] = keypoints[:, :, 0].clip(0, width)
    keypoints[:, :, 1] = keypoints[:, :, 1].clip(0, height)
    return BackendDetections(boxes, scores.astype(np.float32), keypoints)


def _xywh_to_xyxy(boxes: np.ndarray) -> np.ndarray:
    converted = boxes.astype(np.float32, copy=True)
    converted[:, 0] = boxes[:, 0] - boxes[:, 2] / 2
    converted[:, 1] = boxes[:, 1] - boxes[:, 3] / 2
    converted[:, 2] = boxes[:, 0] + boxes[:, 2] / 2
    converted[:, 3] = boxes[:, 1] + boxes[:, 3] / 2
    return converted


def _nms(boxes: np.ndarray, scores: np.ndarray, iou_threshold: float) -> np.ndarray:
    order = scores.argsort()[::-1]
    selected: list[int] = []
    while order.size:
        current = int(order[0])
        selected.append(current)
        if order.size == 1:
            break
        remaining = order[1:]
        ious = _box_iou(boxes[current], boxes[remaining])
        order = remaining[ious <= iou_threshold]
    return np.asarray(selected, dtype=np.int64)


def _box_iou(box: np.ndarray, boxes: np.ndarray) -> np.ndarray:
    top_left = np.maximum(box[:2], boxes[:, :2])
    bottom_right = np.minimum(box[2:], boxes[:, 2:])
    intersection = np.prod(np.maximum(bottom_right - top_left, 0), axis=1)
    box_area = np.prod(np.maximum(box[2:] - box[:2], 0))
    areas = np.prod(np.maximum(boxes[:, 2:] - boxes[:, :2], 0), axis=1)
    union = box_area + areas - intersection
    return np.divide(intersection, union, out=np.zeros_like(intersection), where=union > 0)

