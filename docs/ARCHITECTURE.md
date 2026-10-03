# Arquitectura del Sistema (HandRaiseDetection)

Nodo de analítica de video en el borde para Windows 11 x64 (.NET 10). Diseñado para procesar video continuo en tiempo real (webcam, RTSP, archivo), detectar poses y emitir eventos deterministas de mano levantada con zonas poligonales y snapshots JPEG.

## Diagrama de Capas

```
┌────────────────────────────────────────────────────────┐
│                   Entradas / UI                        │
│   HandRaise.Host (REST/WS)    HandRaise.Desktop (WPF)  │
│   HandRaise.DebugApp          HandRaise.DeviceProbe    │
└──────────────────────────┬─────────────────────────────┘
                           │
┌──────────────────────────▼─────────────────────────────┐
│              HandRaise.Infrastructure.Windows          │
│   OpenCvSharp (Captura / Overlay / JPEG)               │
│   Windows ML (DirectML / CPU Inference)                │
│   DXGI / Hardware Inventory                            │
│   SQLite / EF Core (Persistencia de Eventos)           │
│   DPAPI (Protección de credenciales RTSP)              │
└──────────────────────────┬─────────────────────────────┘
                           │
┌──────────────────────────▼─────────────────────────────┐
│                 HandRaise.Application                  │
│   CameraPipeline & VideoSource contracts               │
│   ByteTrack Multi-Object Tracker                       │
│   TrackedHandEvaluator & ContextualEventPublisher      │
│   HandEventBus (Pub/Sub en proceso)                    │
│   IInferenceBackend / IPersonSnapshotEncoder           │
└──────────────────────────┬─────────────────────────────┘
                           │
┌──────────────────────────▼─────────────────────────────┐
│                   HandRaise.Domain                     │
│   Puro: Cero dependencias externas                     │
│   HandPoseEvaluator (Geometría anatómica de manos)     │
│   HandStateMachine (Máquina de estados de elevación)   │
│   PolygonZoneEvaluator (Point-in-polygon raycasting)   │
└────────────────────────────────────────────────────────┘
```

## Flujo de Procesamiento por Frame

1. **Captura (`IVideoSource`):** Decodifica frame BGR vía OpenCV (`WebcamVideoSource`, `RtspVideoSource`, `FileVideoSource`).
2. **Inferencia (`IInferenceBackend`):** Preprocesamiento letterbox (640x640), ejecución YOLO26 Pose en GPU (DirectML) o CPU fallback, NMS y extracción de 17 keypoints.
3. **Tracking (`ITracker` / `ByteTrack`):** Asociación espacial de bboxes con filtro Kalman y matching IoU bi-etápico.
4. **Evaluación de Dominio (`TrackedHandEvaluator`):**
   - Validación de keypoints por umbral de confianza.
   - Cálculo de muñeca sobre hombro con margen anatómico proporcional.
   - Evaluación de zonas poligonales activas.
   - Transición de estados temporales (`HandStateMachine`) para confirmación/enfriamiento.
5. **Emisión de Eventos:** Si hay transición, se extrae el snapshot JPEG recortado de la persona y se publica al `HandEventBus`.
6. **Overlay y Métricas:** Dibujado de esqueleto, cajas y estadísticas (`OpenCvOverlayRenderer`), actualización de métricas del pipeline y despacho a consumidores en vivo (MJPEG / WebSockets).
