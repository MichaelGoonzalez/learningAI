# Estado Actual del Nodo Windows (Vision Edge Node)

Fecha de auditoría: 2 de Octubre de 2026  
Entorno objetivo: Windows 10/11 x64 (Build 18362+) / Windows Server 2022+  
Stack tecnológico: .NET 10 (`net10.0-windows10.0.18362.0`), C# 13, xUnit (110 pruebas unitarias en verde).

---

## 1. Arquitectura y Separación de Responsabilidades

El repositorio mantiene una arquitectura limpia desacoplada en capas estrictas:

```
+--------------------------------------------------------------------------------+
|                     src/HandRaise.Host (Generic Host / Kestrel)                |
|  - API REST /api/v1 (Cámaras, Eventos, Dispositivos, Métricas, Zonas)          |
|  - WebSockets (/ws/events, /ws/metrics)                                        |
|  - MJPEG Stream (/cameras/{id}/stream)                                         |
|  - Orquestación de ciclo de vida (HeadlessEngineService)                       |
+--------------------------------------------------------------------------------+
                                       |
+--------------------------------------------------------------------------------+
|                   src/HandRaise.Infrastructure.Windows                         |
|  - Inferencia: Windows ML / ONNX Runtime (DirectML GPU + CPU Fallback)         |
|  - Detección de hardware: WindowsDeviceDetector (DXGI + NVAPI/Win32)          |
|  - Captura y Video: OpenCvSharp4 (Webcam, RTSP, Video File)                    |
|  - Almacenamiento y Snapshots: SQLite (EF Core Migrations) + File System       |
|  - Seguridad: MachineDpapiProtector (DPAPI local machine para secretos RTSP)   |
|  - Logging: Serilog File Logger estructurado                                  |
+--------------------------------------------------------------------------------+
                                       |
+--------------------------------------------------------------------------------+
|                        src/HandRaise.Application                               |
|  - Pipeline de procesamiento: CameraPipeline & CameraPipelineManager          |
|  - Tracking: ByteTrack (implementación determinista C#)                        |
|  - Bus de eventos: InMemoryEventBus                                            |
|  - Contratos y Repositorios: ICameraRepository, IEventRepository, etc.         |
|  - Métricas: PipelineMetricsRegistry                                           |
+--------------------------------------------------------------------------------+
                                       |
+--------------------------------------------------------------------------------+
|                           src/HandRaise.Domain                                 |
|  - Pure C#: Sin dependencias externas (Sin OpenCV, ONNX, SQLite ni ASP.NET)   |
|  - Reglas geométricas: HandRaiseRule, Pose, Landmark, NormalizedBoundingBox    |
|  - Geometría de Zonas: DetectionZone (polígonos normalizados)                  |
|  - Máquina de estados: TrackHandState, Eventos de dominio                     |
+--------------------------------------------------------------------------------+
```

---

## 2. Experiencia de Instalación y Primer Arranque

- **Empaquetado**: Publicación portable win-x64 (`publish.cmd`) autocontenida o dependiente del framework.
- **Runtimes del Sistema**: Requiere DirectX 12 / DirectML nativo en Windows 10/11. No requiere CUDA ni librerías C++ propietarias adicionales gracias a los runtimes empaquetados de OpenCvSharp y Microsoft.AI.MachineLearning.
- **Almacenamiento Local**: Se crea automáticamente bajo `%LOCALAPPDATA%\HandRaiseDetection\`:
  - `events.db`: Base de datos SQLite migrada en arranque vía `StorageDatabase.MigrateAsync`.
  - `snapshots/`: Directorio de capturas JPEG con retención configurable (máx. GB / días).
  - `logs/`: Logs rotativos Serilog.
  - `cameras.json` y `camera-credentials.json`: Persistencia de cámaras y credenciales cifradas con DPAPI.
  - `settings.json`: Preferencia de acelerador de inferencia (CPU / GPU DXGI persistida).
- **Modelo de Inferencia**: Empaquetado en `models/` y verificado en arranque mediante `model.manifest.json` (SHA256 y metadatos).

---

## 3. Inferencia, Hardware y Aceleración

- **Detección de Hardware**: `WindowsDeviceDetector` enumera adaptadores DXGI (NVIDIA, AMD, Intel, WARP) con VRAM y soporte de DirectML.
- **Selección Automática / Preferencia**:
  - `DevicePreferenceService` lee el dispositivo configurado o guardado.
  - Si la GPU configurada falla o no soporta el modelo, `SwitchableInferenceBackend` cae automáticamente a **CPU Fallback** determinista sin interrumpir la ejecución general.
- **Cambio en Caliente**: La API (`PUT /api/v1/system/device`) y el motor permiten cambiar el backend de inferencia activo en tiempo de ejecución validando la sesión nueva antes de sustituir la anterior.

---

## 4. Captura de Video, Pipelines y Streaming

- **Fuentes soportadas**: Índice de Webcam (`0`, `1`), URL RTSP (`rtsp://...`) y archivo de video local (`.mp4`, `.avi`, `.mkv`).
- **Pipeline por Cámara**: Cada cámara activa corre en su propio hilo/tarea desacoplada con decodificación, inferencia, tracking (ByteTrack) y evaluación de reglas.
- **Overlay y Streaming**:
  - `OpenCvOverlayRenderer` dibuja keypoints, bounding boxes y zonas.
  - `GET /api/v1/cameras/{id}/stream` emite un stream MJPEG HTTP `multipart/x-mixed-replace` con FPS configurables, calidad JPEG y desconexión limpia sin saturar memoria ni CPU.
- **Snapshots**: Almacenados en disco de forma asíncrona ante eventos o servidos en vivo mediante `GET /api/v1/cameras/{id}/snapshot`.

---

## 5. API REST, WebSockets y Seguridad

- **REST API `/api/v1`**:
  - Endpoints de salud y métricas: `/health`, `/metrics`, `/system/runtime`, `/system/capacity`.
  - CRUD de cámaras y ciclo de vida: `/cameras`, `/cameras/{id}`, `/cameras/{id}/start`, `/cameras/{id}/stop`, `/cameras/test`.
  - Zonas y Dispositivos: `/cameras/{id}/zones`, `/system/device`.
  - Historial y Estadísticas: `/events`, `/events/{id}/snapshot`, `/stats`.
- **WebSockets en Tiempo Real**:
  - `/api/v1/ws/events`: Transmisión de eventos del bus en vivo con suscripción opcional por `camera_id` y canales acotados por cliente (`Channel<T>` con descarte de clientes lentos).
  - `/api/v1/ws/metrics`: Snapshot periódico de métricas de procesamiento por cámara y globales.
- **Seguridad**:
  - Autenticación por `X-Api-Key` en headers o `api_key` en query param para streams/WebSockets.
  - Lecturas en `localhost` permitidas sin API key para facilitar diagnósticos locales.
  - Escrituras requieren API key explícita si está configurada.
  - Credenciales RTSP cifradas con DPAPI máquina (`MachineDpapiProtector`), nunca expuestas en respuestas JSON ni en logs.
