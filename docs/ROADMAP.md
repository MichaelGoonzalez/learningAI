# Roadmap y Estado de Desarrollo

## Bloques Completados

- **C1 (Dominio Puro):** Reglas geométricas y anatómicas de evaluación de manos (`HandPoseEvaluator`), máquina de estados determinista (`HandStateMachine`) y polígonos de zonas (`PolygonZoneEvaluator`). Cero dependencias de framework.
- **C2 (Inventario de Hardware):** Detección de adaptadores DXGI, runtimes Windows ML/DirectML, heurística de selección óptima y persistencia de preferencias de dispositivo.
- **C3 (Inferencia ONNX):** Backend Windows ML con DirectML y CPU fallback para YOLO26 Pose, pre/postprocesamiento letterbox normalizado y validación de equivalencia numérica.
- **C4 (Captura y Pipeline):** Fuentes de video robustas (webcam, RTSP con reconexión exponencial y archivos con bucle), pipeline `CameraPipeline`, renderizado de overlay `OpenCvOverlayRenderer` y benchmarks de throughput.
- **C5 (Tracking y Eventos):** Algoritmo ByteTrack implementado en C# con filtro Kalman y matching bi-etápico de IoU. Bus de eventos determinista (`HandEventBus`).
- **C6 (Persistencia SQLite):** Almacenamiento con Entity Framework Core / SQLite (`events.db`), migraciones automáticas, extracción y retención de snapshots JPEG de personas, retención configurable por días y espacio en disco.
- **C8 (Desktop WPF):** Interfaz gráfica de escritorio con patrón MVVM, mosaico multivista, histórico de eventos en vivo, cambio de acelerador en caliente y editor visual de zonas poligonales.
- **C9 (Empaquetado Portable):** Script de publicación Windows x64 autocontenido (`publish.cmd`), cálculo de hash SHA-256 de integridad del modelo ONNX y manifiesto de licencias de terceros.
- **D1 (Host Headless & Métricas):** Generic Host Kestrel como servicio de Windows o consola headless, endpoints REST `/api/v1/health`, `/metrics`, `/devices`, `/system/runtime`, `/system/capacity` y middleware de autenticación.
- **D2 (Gestión de Cámaras & DPAPI):** CRUD dinámico de cámaras por API REST, encriptación de credenciales RTSP vía Windows DPAPI a nivel de máquina, prueba de fuentes en caliente y aplicación de zonas normalizadas sin reiniciar.
- **D2b (Tiempo Real & Streaming):** WebSocket `/api/v1/ws/events` con filtrado opcional por cámara y buffers acotados no bloqueantes, WebSocket `/api/v1/ws/metrics` periódico y endpoint MJPEG `/api/v1/cameras/{id}/stream` con overlay en vivo y límite de clientes concurrentes.

---

## Bloques Pendientes

- **D3:** Manifest/modelos v2 e importación/activar/revertir con validación de hash y licencia.
- **D4:** Módulos de analítica adicionales (`PackageCounting`, `ConveyorFlow`) con pruebas sintéticas deterministas.
- **D5:** Despacho saliente de eventos mediante Webhooks HTTP y MQTT con reintentos acotados y clips pre/post evento.
- **D6:** Descubrimiento ONVIF, plantillas de configuración de cámaras y calibración de capacidad en hardware real.
- **D7:** Pipeline Python de dataset, entrenamiento y exportación ONNX reproducible.
- **D8:** Instalador de servicio de Windows limpio con gestión de ciclo de vida.
- **D9:** Agente de flota por conexión saliente hacia hub centralizado.
