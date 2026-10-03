"""Ejecuta captura, pose y overlay sin iniciar la API."""

from __future__ import annotations

import argparse
import logging
import sys
import time
from pathlib import Path

import cv2


PROJECT_ROOT = Path(__file__).resolve().parents[1]
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from app.core.config import CameraConfig, get_settings  # noqa: E402
from app.vision.capture import VideoSource  # noqa: E402
from app.vision.engine import PoseEngine  # noqa: E402
from app.vision.overlay import draw  # noqa: E402


LOGGER = logging.getLogger("run_video")
WINDOW_NAME = "Hand Raise Detection"


def parse_source(raw_source: str) -> str | int:
    stripped = raw_source.strip()
    if stripped.isdecimal():
        return int(stripped)
    return stripped


def select_camera(cameras: list[CameraConfig], source: str | int) -> CameraConfig:
    for camera in cameras:
        if camera.source == source:
            return camera
        if isinstance(camera.source, str) and str(source) == camera.source:
            return camera
    return cameras[0]


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Depura YOLO pose y ByteTrack sobre video, RTSP o webcam."
    )
    parser.add_argument("--source", required=True, help="Ruta, URL RTSP o indice")
    parser.add_argument("--model", help="Pesos YOLO pose alternativos")
    parser.add_argument(
        "--no-window",
        action="store_true",
        help="Procesa sin abrir una ventana",
    )
    parser.add_argument("--save", type=Path, help="Guarda el video anotado")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s | %(levelname)s | %(name)s | %(message)s",
    )
    settings = get_settings()
    source_value = parse_source(args.source)
    camera = select_camera(settings.cameras, source_value)
    engine = PoseEngine(
        settings,
        camera_id=camera.id,
        model_weights=args.model,
    )
    video_source = VideoSource(
        source_value,
        loop=camera.loop,
        reconnect_delay_ms=settings.runtime.reconnect_delay_ms,
        max_reconnect_delay_ms=settings.runtime.max_reconnect_delay_ms,
        fallback_fps=settings.runtime.fallback_fps,
    )

    writer: cv2.VideoWriter | None = None
    started_at = time.perf_counter()
    processed_frames = 0
    try:
        while True:
            packet = video_source.read()
            if packet is None:
                if not video_source.is_alive:
                    break
                time.sleep(0.002)
                continue

            frame, _timestamp_ms = packet
            detections = engine.process(frame)
            annotated = draw(frame, detections, engine.zones, engine.stats)
            processed_frames += 1

            if args.save is not None:
                if writer is None:
                    args.save.parent.mkdir(parents=True, exist_ok=True)
                    height, width = annotated.shape[:2]
                    fourcc = cv2.VideoWriter_fourcc(*"mp4v")
                    writer = cv2.VideoWriter(
                        str(args.save),
                        fourcc,
                        video_source.fps or settings.runtime.fallback_fps,
                        (width, height),
                    )
                    if not writer.isOpened():
                        raise RuntimeError(f"could not open output video: {args.save}")
                writer.write(annotated)

            if not args.no_window:
                cv2.imshow(WINDOW_NAME, annotated)
                if cv2.waitKey(1) & 0xFF == ord("q"):
                    break
    except KeyboardInterrupt:
        LOGGER.info("Interrupcion solicitada por el usuario")
    finally:
        video_source.stop()
        engine.close()
        if writer is not None:
            writer.release()
        if not args.no_window:
            cv2.destroyAllWindows()

    elapsed_s = time.perf_counter() - started_at
    average_fps = processed_frames / elapsed_s if elapsed_s > 0 else 0.0
    stats = engine.stats
    print(
        "Resumen: "
        f"frames={processed_frames}, "
        f"FPS promedio={average_fps:.2f}, "
        f"latencia media={stats.average_latency_ms:.2f} ms"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
