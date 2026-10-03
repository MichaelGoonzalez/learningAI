# Guía de Configuración (`appsettings.json`)

La configuración principal del Host se encuentra en `src/HandRaise.Host/appsettings.json` (y sus equivalentes `snake_case` para el entorno de escritorio).

```json
{
  "nodeId": "local-node",
  "siteId": "default-site",
  "capacity": {
    "targetFramesPerSecond": 5.0
  },
  "api": {
    "port": 5080,
    "bindAddress": "127.0.0.1",
    "apiKeyHeader": "X-Api-Key",
    "apiKey": null,
    "allowedOrigins": [ "http://localhost:3000", "http://localhost:4200" ]
  },
  "model": {
    "path": "models/yolo26n-pose.onnx",
    "name": "YOLO26n Pose",
    "inputWidth": 640,
    "inputHeight": 640,
    "classCount": 1,
    "keypointCount": 17,
    "confidenceThreshold": 0.25,
    "iouThreshold": 0.7,
    "maximumDetections": 300
  },
  "capture": {
    "fallbackFps": 30.0,
    "reconnectInitialMs": 500,
    "reconnectMaximumMs": 10000,
    "openTimeoutMs": 5000,
    "readTimeoutMs": 3000
  },
  "hands": {
    "keypointConfidence": 0.5,
    "shoulderMarginRatio": 0.15,
    "strictMode": false,
    "strictMarginPixels": 0.0,
    "consecutiveFrames": 3,
    "lowerConsecutiveFrames": 3,
    "cooldownMs": 1000,
    "trackTimeToLiveMs": 5000
  },
  "tracker": {
    "highConfidenceThreshold": 0.5,
    "lowConfidenceThreshold": 0.1,
    "newTrackThreshold": 0.5,
    "firstMatchIouThreshold": 0.3,
    "secondMatchIouThreshold": 0.2,
    "lostTrackBufferFrames": 30,
    "nominalFramesPerSecond": 30.0,
    "positionProcessNoise": 1.0,
    "velocityProcessNoise": 0.1,
    "measurementNoise": 10.0
  },
  "storage": {
    "databasePath": "%LOCALAPPDATA%/HandRaiseDetection/events.db",
    "snapshotDirectory": "%LOCALAPPDATA%/HandRaiseDetection/snapshots",
    "cameraStorePath": "%LOCALAPPDATA%/HandRaiseDetection/cameras.json",
    "cameraCredentialStorePath": "%LOCALAPPDATA%/HandRaiseDetection/camera-credentials.json",
    "queueCapacity": 256,
    "batchSize": 32,
    "snapshotMarginRatio": 0.1,
    "jpegQuality": 88,
    "retentionDays": 30,
    "maximumSnapshotMegabytes": 1024,
    "cleanupIntervalMinutes": 60
  },
  "streaming": {
    "metricsIntervalSeconds": 2.0,
    "maxStreamClientsPerCamera": 4,
    "defaultStreamFps": 15.0,
    "defaultStreamQuality": 80,
    "eventChannelCapacity": 128
  },
  "cameras": []
}
```

## Secciones Principales

- **`nodeId` / `siteId`:** Identificadores únicos del nodo en el borde y su ubicación física.
- **`capacity.targetFramesPerSecond`:** FPS objetivo base para el cálculo de capacidad máxima estimada de cámaras.
- **`api`:** Configuración del servidor Kestrel, puerto, IP de escucha, cabecera de autenticación y lista blanca de CORS.
- **`model`:** Ruta relativa o absoluta del modelo ONNX y parámetros de inferencia NMS.
- **`capture`:** Parámetros de reconexión exponencial y timeouts para fuentes RTSP y webcams.
- **`hands`:** Umbrales anatómicos (proporción hombro-muñeca), filtros de frames consecutivos para confirmación de evento y tiempos de enfriamiento.
- **`tracker`:** Parámetros del algoritmo ByteTrack y covarianzas del filtro Kalman.
- **`storage`:** Rutas de base de datos SQLite, almacenamiento de snapshots JPEG y políticas de retención automática.
- **`streaming`:** Parámetros de emisión en tiempo real (intervalo de métricas, límite de clientes concurrentes por cámara, FPS y calidad por defecto de MJPEG, y capacidad del buffer de eventos).
