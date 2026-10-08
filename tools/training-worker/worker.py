"""VisionControl detection worker v1. stdin commands, stdout JSONL, stderr bounded by Edge.

No environment installation, network provisioning, camera access or UI dependencies.
"""
from __future__ import annotations

import hashlib
import importlib.metadata
import json
import math
import os
from pathlib import Path
import sys
import threading
import traceback

VERSION = "2.0.0"
cancelled = threading.Event()
protocol = None


def emit(kind, **fields):
    protocol.write(json.dumps({"protocol_version": 1, "type": kind, **fields}, allow_nan=False) + "\n")
    protocol.flush()


def check_cancel(*_):
    if cancelled.is_set():
        raise InterruptedError("Entrenamiento cancelado.")


def commands():
    for line in sys.stdin:
        try:
            if json.loads(line).get("type") == "cancel":
                cancelled.set()
        except (ValueError, AttributeError):
            cancelled.set()
    cancelled.set()  # Parent disappeared; batch callback aborts, Job Object is the backstop.


def select_device(torch, requested, expected_name=None):
    if requested == "cpu":
        return "cpu", "CPU"
    if not requested.startswith("cuda:") or not requested[5:].isdigit():
        raise ValueError("Seleccione CPU o GPU para entrenar.")
    index = int(requested[5:])
    try:
        if not torch.cuda.is_available() or index >= torch.cuda.device_count():
            raise RuntimeError("GPU ausente")
        name = torch.cuda.get_device_name(index)
        if expected_name and expected_name != name:
            raise RuntimeError("GPU diferente")
        tensor = torch.ones(1, device=requested, requires_grad=True)
        tensor.square().sum().backward()
        torch.cuda.synchronize(index)
        return str(index), name  # Ultralytics accepts an explicit single CUDA index.
    except (RuntimeError, AssertionError) as exc:
        raise RuntimeError("La GPU seleccionada ya no está disponible.") from exc


def validate_packages():
    if sys.version_info[:3] != (3, 11, 9):
        raise RuntimeError("Versión del entorno privado incompatible.")
    from packaging.requirements import Requirement
    for line in Path(__file__).with_name("requirements.txt").read_text(encoding="utf-8").splitlines():
        if not line.strip() or line.startswith("#"):
            continue
        name, expected = line.split("==")
        if importlib.metadata.version(name).split("+")[0] != expected:
            raise RuntimeError(f"Componente incompatible: {name}")
        for dependency in importlib.metadata.requires(name) or []:
            requirement = Requirement(dependency)
            if requirement.marker and not requirement.marker.evaluate({"extra": ""}):
                continue
            if importlib.metadata.version(requirement.name) not in requirement.specifier:
                raise RuntimeError(f"Dependencia incompatible: {requirement.name}")


def probe():
    os.environ["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID"
    os.environ.pop("CUDA_VISIBLE_DEVICES", None)
    validate_packages()
    import torch
    import torchvision
    import onnx
    import ultralytics
    base = Path(__file__).with_name("yolo26n.pt")
    if not base.is_file() or ultralytics.YOLO(str(base), task="detect").task != "detect":
        raise RuntimeError("El modelo inicial no es compatible.")
    tensor = torch.ones(1, requires_grad=True)
    tensor.square().sum().backward()
    # Check the compiled object-detection operation too, without a model or training.
    torchvision.ops.nms(torch.tensor([[0., 0., 1., 1.]]), torch.tensor([.9]), .5)
    devices = [{"device": {"kind": "cpu", "device_id": 0, "display_name": "Procesador del equipo"}, "available": True}]
    if torch.cuda.is_available():
        for index in range(torch.cuda.device_count()):
            try:
                _, name = select_device(torch, f"cuda:{index}")
                with torch.cuda.device(index):
                    free, _ = torch.cuda.mem_get_info()
                devices.append({"device": {"kind": "gpu", "device_id": index, "display_name": name},
                                "available": True, "memory_bytes": free})
            except (RuntimeError, AssertionError):
                devices.append({"device": {"kind": "gpu", "device_id": index}, "available": False,
                                "unavailable_reason": "Este equipo no dispone actualmente de una GPU compatible con el entorno de entrenamiento."})
    if not any(d["device"]["kind"] == "gpu" for d in devices):
        devices.append({"device": {"kind": "gpu", "device_id": 0}, "available": False,
                        "unavailable_reason": "Este equipo no dispone actualmente de una GPU compatible con el entorno de entrenamiento."})
    protocol.write(json.dumps({"worker_version": VERSION, "cpu_available": True, "devices": devices}) + "\n")
    protocol.flush()


def run(request_path):
    os.environ["YOLO_AUTOINSTALL"] = "False"
    os.environ["YOLO_OFFLINE"] = "True"
    request = json.loads(request_path.read_text(encoding="utf-8"))
    if request["protocol_version"] != 1 or request["worker_version"] != VERSION:
        raise ValueError("Versión de protocolo/worker incompatible.")
    base = Path(request["base_model"]).resolve(strict=True)
    if base.name != "yolo26n.pt" or hashlib.sha256(base.read_bytes()).hexdigest().upper() != request["base_model_sha256"]:
        raise ValueError("El modelo base no coincide con el snapshot del job.")
    dataset = Path(request["dataset"]).resolve(strict=True)
    output = Path(request["output"]).resolve()
    output.mkdir(parents=True, exist_ok=False)
    config = request["configuration"]
    requested = config["device"]
    if requested != "cpu" and (not requested.startswith("cuda:") or not requested[5:].isdigit()):
        raise ValueError("Seleccione CPU o GPU para entrenar.")
    # Fix visibility BEFORE importing torch. Ultralytics addresses the single visible GPU as 0.
    # Probing cuda:N before narrowing visibility would initialize CUDA and could train on GPU 0 instead.
    os.environ["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID"
    os.environ["CUDA_VISIBLE_DEVICES"] = "-1" if requested == "cpu" else requested[5:]
    check_cancel()

    validate_packages()

    import torch
    import onnx
    from ultralytics import YOLO, settings
    from ultralytics.data import utils as dataset_utils

    # Training/validation run without plots. The pinned library otherwise tries to download fonts
    # during dataset validation even in offline mode. This override is confined to this process.
    dataset_utils.check_font = lambda *_args, **_kwargs: None

    settings.update({"sync": False, "wandb": False, "mlflow": False, "clearml": False,
                     "comet": False, "dvc": False, "raytune": False, "tensorboard": False})
    torch.set_num_threads(config["cpu_threads"])
    device, device_name = select_device(torch, "cpu" if requested == "cpu" else "cuda:0", request["selected_device"].get("display_name"))
    environment = {"worker_version": VERSION, "python": sys.version, "device": requested, "device_name": device_name,
                   "configuration": config, "requested_device": requested, "worker_device": device,
                   "packages": {d.metadata["Name"]: d.version for d in importlib.metadata.distributions()},
                   "base_model_sha256": request["base_model_sha256"],
                   "worker_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest()}
    (output / "environment.json").write_text(json.dumps(environment, indent=2), encoding="utf-8")
    emit("capabilities", worker_version=VERSION, device=requested, message=device_name)
    check_cancel()
    model = YOLO(str(base), task="detect")
    if model.task != "detect":
        raise ValueError("El modelo base debe ser de detección de objetos.")

    def epoch_done(trainer):
        check_cancel()
        epoch = trainer.epoch + 1
        emit("progress", stage="training", percent=10 + 65 * epoch / trainer.epochs,
             current_epoch=epoch, total_epochs=trainer.epochs, message="Entrenando el detector.")

    model.add_callback("on_train_batch_start", check_cancel)
    model.add_callback("on_train_epoch_end", epoch_done)
    model.add_callback("on_val_batch_start", check_cancel)
    emit("progress", stage="training", percent=10, current_epoch=0, total_epochs=config["epochs"])
    model.train(data=str(dataset), epochs=config["epochs"], batch=config["batch"], imgsz=config["image_size"],
                patience=config["patience"], workers=0, device=device, seed=config["seed"], deterministic=True,
                project=str(output), name="train", exist_ok=False, pretrained=True, amp=False, plots=False,
                cache=False, save=True, verbose=False, optimizer="AdamW")
    check_cancel()
    best = Path(model.trainer.best).resolve(strict=True)
    trained = YOLO(str(best), task="detect")
    trained.add_callback("on_val_batch_start", check_cancel)
    emit("progress", stage="validating", percent=80, message="Validando el detector.")
    validation = trained.val(data=str(dataset), split="val", device=device, imgsz=config["image_size"],
                             batch=config["batch"], workers=0, plots=False, project=str(output), name="validation")
    metrics = {"map50": float(validation.box.map50), "map50_95": float(validation.box.map),
               "precision": float(validation.box.mp), "recall": float(validation.box.mr)}
    if not all(math.isfinite(v) for v in metrics.values()):
        raise ValueError("La validación produjo métricas no finitas.")
    check_cancel()
    emit("progress", stage="exporting", percent=90, message="Exportando el detector.", metrics=metrics)
    # Same raw detection layout as the repository's YOLO26 pose artifact, with zero keypoints.
    exported = Path(trained.export(format="onnx", imgsz=config["image_size"], batch=1, dynamic=False,
                                   half=False, simplify=False, opset=18, nms=False, end2end=False, device="cpu"))
    check_cancel()
    artifact = onnx.load(str(exported))
    names = [trained.names[i] for i in range(len(trained.names))]
    if names != request["classes"]:
        raise ValueError("Las clases exportadas no coinciden con el dataset.")
    metadata = {item.key: item.value for item in artifact.metadata_props}
    metadata.update({"task": "detect", "visioncontrol_classes": json.dumps(names), "visioncontrol_worker": VERSION})
    onnx.helper.set_model_props(artifact, metadata)
    onnx.checker.check_model(artifact)
    final = output / "model.onnx"
    onnx.save_model(artifact, str(final), save_as_external_data=False)
    (output / "result.json").write_text(json.dumps({"metrics": metrics, "classes": names, "device": requested,
                                                   "worker_version": VERSION}, indent=2), encoding="utf-8")
    check_cancel()
    emit("completed", model_path=str(final), worker_version=VERSION, device=requested, metrics=metrics)


def main():
    global protocol
    # Preserve a dedicated stdout descriptor. Redirect even native library stdout to stderr logs.
    protocol = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", buffering=1)
    os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
    sys.stdout = sys.stderr
    try:
        start = json.loads(sys.stdin.readline())
        if start != {"type": "start", "protocol_version": 1}:
            raise ValueError("Falta el handshake del proceso propietario.")
        if sys.argv[1] == "--probe":
            probe()
            return 0
        threading.Thread(target=commands, daemon=True).start()
        run(Path(sys.argv[1]).resolve(strict=True))
        return 0
    except InterruptedError:
        return 2
    except Exception as exc:
        traceback.print_exc(file=sys.stderr)
        emit("error", error_code="training_gpu_unavailable" if "GPU seleccionada" in str(exc) else "python_training_failed",
             message=str(exc)[:2000])
        return 1
    finally:
        protocol.close()


if __name__ == "__main__":
    raise SystemExit(main())
