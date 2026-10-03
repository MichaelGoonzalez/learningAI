"""Exporta un modelo Ultralytics pose a ONNX sin instalar paquetes solo."""

from __future__ import annotations

import argparse
import os
import shutil
import sys
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[1]
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="Exporta YOLO pose a ONNX.")
    parser.add_argument("--model", default="yolo26n-pose.pt")
    parser.add_argument("--imgsz", type=int, default=640)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--dynamic", action="store_true")
    return parser


def main() -> int:
    args = build_parser().parse_args()
    os.environ["YOLO_AUTOINSTALL"] = "False"
    try:
        from ultralytics import YOLO
    except ImportError as exc:
        raise SystemExit("Ultralytics no esta instalado") from exc

    exported = Path(
        YOLO(args.model).export(
            format="onnx",
            imgsz=args.imgsz,
            dynamic=args.dynamic,
            simplify=False,
            nms=None,
        )
    )
    destination = args.output or Path(args.model).with_suffix(".onnx")
    destination.parent.mkdir(parents=True, exist_ok=True)
    if exported.resolve() != destination.resolve():
        shutil.move(str(exported), str(destination))
    print(f"Modelo ONNX exportado en: {destination.resolve()}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
