# Propuesta de Siguiente Bloque: Bloque D2c — Edge Node Appliance Hardening & Zero-Config Autonomy

Fecha: 2 de Octubre de 2026  
Estado: Propuesto para revisión y aprobación antes de implementar.

---

## 1. Objetivo del Bloque

Convertir el nodo Windows existente en un **Appliance Industrial Zero-Config**:
Garantizar que el servicio arranque de forma autónoma y auto-reparable en cualquier máquina Windows x64 sin requerir configuración manual previa de archivos, verificando proactivamente sus dependencias en arranque (pre-flight checks), implementando reconexión automática resiliente con backoff exponencial para cámaras RTSP/Webcam caídas, y exponiendo diagnósticos completos y readiness checks para el frontend React.

---

## 2. Alcance (In Scope)

1. **Pre-flight Health & Storage Initialization (Zero-Config Startup)**:
   - Inicialización automática de carpetas (`snapshots`, `logs`, `db`) y generación de `nodeId` por defecto si falta la configuración.
   - Verificación de arranque: integridad de modelos ONNX, permisos de escritura de almacenamiento y disponibilidad de puertos.
   - Endpoint `GET /api/v1/health/readiness` (RFC 7807 en 503 si el nodo aún está cargando o falla un componente crítico).
2. **Resilience & Reconnection Watchdog en Pipelines**:
   - Monitoreo activo de streams de cámara en `CameraPipeline`: si la fuente RTSP/Webcam se desconecta o falla, activar bucle de reintento automático con backoff exponencial con jitter (2s, 5s, 10s, 30s) sin bloquear otros pipelines ni saturar CPU.
   - Aislamiento de excepciones en ciclo de inferencia para evitar caída del proceso Host.
3. **Diagnósticos del Sistema y Telemetría Consolidada**:
   - Endpoint `GET /api/v1/system/diagnostics`: payload consolidado con uso de memoria RAM del proceso, espacio libre en disco para snapshots, estado detallado de cada cámara/pipeline (uptime, reconexiones, FPS reales vs objetivo, errores recientes) y backend activo.
4. **Desacoplamiento Base de Módulos Analíticos (Preparación D3/D4)**:
   - Refactorizar `CameraPipeline` para usar la interfaz interna `IAnalyticEvaluator` en lugar de acoplar directamente `TrackedHandEvaluator`, permitiendo encadenar futuros módulos analíticos sin tocar la captura ni el tracking.

---

## 3. Fuera de Alcance (Out of Scope)

- Nuevos modelos ONNX o analíticas de visión no solicitadas (ej. conteo de paquetes D4 o clasificación PPE).
- Integración con brokers externos (MQTT, Webhooks D5 o NVRs).
- Alteraciones a las reglas matemáticas puras de `HandRaise.Domain`.
- Runtimes fuera de Windows x64 / .NET 10.

---

## 4. Archivos a Modificar / Crear

### Modificar:
1. `src/HandRaise.Host/HostApplication.cs` (Registro de endpoints `/health/readiness`, `/system/diagnostics` y pipeline middleware).
2. `src/HandRaise.Host/Services/HeadlessEngineService.cs` (Pre-flight checks, arranque tolerante a fallos, telemetría de diagnóstico).
3. `src/HandRaise.Application/Pipelines/CameraPipeline.cs` (Watchdog de reconexión con backoff exponencial y desacoplamiento de analítica).
4. `src/HandRaise.Application/Pipelines/ICameraPipeline.cs` (Propiedades de estado de salud, reconexiones y diagnóstico del pipeline).
5. `src/HandRaise.Infrastructure.Windows/Storage/StorageDatabase.cs` o `MachineDpapiProtector.cs` (Manejo tolerante si los directorios no existen previamente).
6. `tests-csharp/HandRaise.Host.Tests/` (Nuevas pruebas unitarias para readiness, diagnostics y reconexión simulada).
7. `tests-csharp/HandRaise.Application.Tests/` (Pruebas unitarias para el watchdog de reconexión y `IAnalyticEvaluator`).

---

## 5. Pruebas Unitarias y Estrategia de Validación

- **Host Tests**:
  - `GetHealthReadiness_WhenReady_Returns200WithComponents`.
  - `GetHealthReadiness_WhenStorageUnhealthy_Returns503ProblemDetails`.
  - `GetSystemDiagnostics_ReturnsStructuredSystemMetrics`.
- **Application Tests**:
  - `CameraPipeline_WhenSourceFails_RetriesWithExponentialBackoff`.
  - `CameraPipeline_WhenReconnected_ResumesProcessingWithoutStateCorruption`.
  - `CameraPipeline_WithAnalyticEvaluator_ProcessesDetectionsDeterministically`.
- **Validación Final**:
  - 100% pruebas superadas en `Release` con `dotnet test HandRaiseDetection.slnx -c Release --no-restore`.

---

## 6. Criterios de Aceptación

1. El Host inicia con éxito en un entorno limpio sin requerir que el usuario cree directorios manualmente ni configure `appsettings.json` previamente.
2. Si una fuente de video falla o se corta, el pipeline entra en estado `Reconnecting` y se recupera automáticamente cuando la fuente vuelve a estar disponible.
3. El frontend React puede consultar `/health/readiness` y `/system/diagnostics` para obtener el estado completo del nodo industrial en tiempo real.
4. No se rompe ningún contrato existente de `/api/v1`.

---

## 7. Análisis de Riesgos y Mitigación

| Riesgo | Impacto | Mitigación |
|---|---|---|
| Reconexión agresiva satura red o logs | Medio | Backoff exponencial con jitter y limitación de frecuencia de logging (`Log.Warning` solo en cambios de estado). |
| Pre-flight checks demoran el arranque | Bajo | Chequeos asíncronos y no bloqueantes; estado `Starting` reportado en `/health/readiness`. |
