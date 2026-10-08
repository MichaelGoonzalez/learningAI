# VisionControl Edge — Rutas de Archivos, Almacenamiento y Seguridad

## 1. Estructura de Directorios

Todos los datos persistentes de la aplicación se almacenan en el directorio de datos locales del usuario actual para garantizar aislamiento de permisos y portabilidad:

`%LOCALAPPDATA%\HandRaiseDetection\`  
*(Equivalente típico: `C:\Users\<Usuario>\AppData\Local\HandRaiseDetection\`)*

```text
%LOCALAPPDATA%\HandRaiseDetection\
│
├── cameras.json              # Configuración persistente de cámaras y orígenes
├── zones.json                # Definición de zonas espaciales (polígonos y rectángulos)
├── lines.json                # Definición de líneas virtuales de cruce y conteo
├── analytics.json            # Instancias de analíticas activas por cámara
├── rules.json                # Reglas de alerta locales
├── alerts.json               # Historial de alertas operacionales generadas
├── user-settings.json        # Preferencias de usuario (tema, perfil de rendimiento)
├── node-credentials.json     # Claves API y credenciales cifradas (DPAPI)
│
├── snapshots/                # Evidencia visual JPEG asociada a alertas
│   ├── alert_abc123.jpg
│   └── ...
│
├── models/                   # Modelos ONNX descargados o importados
│   ├── yolov8n-pose.onnx
│   └── ...
│
└── logs/                     # Registros de depuración y errores en rotación
    ├── handraise.log
    └── ...
```

---

## 2. Seguridad y Cifrado de Credenciales (DPAPI)

- **`MachineDpapiProtector` / `UserDpapiProtector`**: Las contraseñas de flujos RTSP y las claves API privadas se cifran utilizando la API nativa de Windows DPAPI (`DataProtectionScope.CurrentUser` o `DataProtectionScope.LocalMachine`).
- **Sin Exposición de Secretos**: Los endpoints REST y los archivos de registro nunca devuelven ni registran contraseñas en texto plano.
- **Rotación de API Key**: La clave de acceso para VisionControl Console puede regenerarse en cualquier momento desde la interfaz de escritorio en `Configuración -> Conexión Web`.

## Training Lab V1

El [núcleo de entrenamiento](CUSTOM_MODEL_TRAINING.md) agrega `training/projects/<guid>` (manifiesto/assets), `training/jobs/<guid>` (snapshot, worker/log/output) y `training/runtime` (bootstrap administrado bajo demanda). Los modelos publicados viven en `models/custom`, recargados por el registro existente. Eliminar un proyecto lo mueve a `training/deleted`; no elimina modelos ni snapshots auditables de jobs. JSON se escribe atómicamente y las rutas se validan contra traversal y enlaces existentes. No se escriben datasets ni componentes runtime en src/docs/bin/obj.

El runtime privado vive en `%LOCALAPPDATA%\HandRaiseDetection\training\runtime\2.0.0-win-x64-cu130\`: `python/python.exe`, `python/Lib/site-packages`, `worker.py`, `requirements.txt`, `yolo26n.pt`, `download-manifest.json` e `installed.json`. No usa `training/base-models` ni el antiguo venv `runtime/Scripts`; no elimina esos archivos previos. Los manifests contienen versiones, fuentes, SHA-256 y hashes de archivos instalados. `settings/` es estado mutable del proveedor, excluido de la verificación de componentes.

`runtime/runtime.lock` coordina preparación y workers. `stage-<guid>` nunca se usa para entrenar; se activa solo después de validar. `rollback-<guid>` permite restaurar el runtime anterior si falla el commit. Cancelación/error limpia el staging de la operación; un cierre abrupto puede dejar staging sin activar. No se modifica PATH ni el registro global. El peso base se copia y verifica por job, sin descargas del worker.

**El usuario elige CPU o GPU para cada entrenamiento.** `job.json` conserva `selected_device`, `configuration.device`, perfil, seed, snapshot, `runtime_version` y `runtime_manifest_sha256`; `request.json` transporta la misma elección al worker. La persistencia de hardware de inferencia por cámara es independiente.
