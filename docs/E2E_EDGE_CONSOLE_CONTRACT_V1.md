# CONTRATO DE INTEGRACIÓN WIRE V1 — VISIONCONTROL EDGE ↔ VISIONCONTROL CONSOLE

**Versión:** 1.0.0 (Freeze Baseline I1-Windows)  
**Fecha:** Octubre 2026  
**Estado:** SELLADO Y VALIDADO

---

## 1. Propósito y Alcance

Este documento especifica formalmente el contrato de comunicaciones (REST API, WebSockets, MJPEG streaming y esquemas JSON) entre el runtime de borde **VisionControl Edge (C# .NET 10 en Windows)** y el módulo frontend de gestión operativa **VisionControl Console (React + Vite)**.

Ambos repositorios quedan formalmente alineados y congelados bajo este contrato sin suposiciones de datos inventados (Live-Only).

---

## 2. Red y Seguridad

### 2.1 Puertos y Enlaces de Red
* **Puerto Predeterminado:** `5080` (Configurable en runtime / `api:port`).
* **Bind Local (Predeterminado):** `http://127.0.0.1:5080`.
* **Bind LAN (Acceso Remoto):** `http://0.0.0.0:5080` (Expone en IP LAN de la máquina).

### 2.2 Política de Orígenes Cruzados (CORS)
El nodo Edge valida y responde con los encabezados CORS correspondientes para los siguientes orígenes autorizados:
* `http://localhost:3000`
* `http://127.0.0.1:3000`
* `https://vision-control-module.ai.studio`

**Preflight OPTIONS:** Retorna `204 No Content` con encabezados:
* `Access-Control-Allow-Origin: <origin>`
* `Access-Control-Allow-Methods: GET, POST, PUT, DELETE, OPTIONS`
* `Access-Control-Allow-Headers: Content-Type, Accept, X-Api-Key`

### 2.3 Autenticación y Autorización
* **Encabezado Primario:** `X-Api-Key: <key>`
* **Parámetro Query Alternativo:** `?apiKey=<key>` o `?api_key=<key>` (Utilizado para `<img>` streams MJPEG y WebSockets en clientes sin soporte de custom headers).
* **Almacenamiento Local:** Cifrado en disco mediante DPAPI (`node-credentials.json`).
* **Regla de Rechazo:** Si la clave es inválida o no se provee en un endpoint protegido, retorna `401 Unauthorized` con formato RFC 7807 (ProblemDetails).

---

## 3. Especificación de Endpoints REST

### 3.1 Salud, Diagnóstico y Capacidad
* `GET /api/v1/health`
  * **Respuesta:** `{ "status": "healthy", "node_id": "...", "site_id": "...", "uptime_seconds": 12.3 }`
* `GET /api/v1/health/readiness`
  * **Respuesta:** Objeto `ReadinessReport` con verificación de subsistemas (Storage, Inference, CameraEngine).
* `GET /api/v1/system/diagnostics`
  * **Respuesta:** Diagnóstico detallado del nodo de borde, CPU, GPU, memoria y subprocesos.
* `GET /api/v1/system/capacity`
  * **Respuesta:** `ExtendedCapacitySnapshot` con:
    * `node_id`: string
    * `site_id`: string
    * `capacity_status`: `"healthy"` | `"near_capacity"` | `"over_capacity"`
    * `device`: string (e.g. `"gpu:DirectML:NVIDIA GeForce RTX 2050"`)
    * `active_models`: string[]
    * `target_fps`: double
    * `cameras`: CameraCapacityDetail[]

### 3.2 Gestión de Cámaras y Captura
* `GET /api/v1/cameras`
  * **Respuesta:** `CameraView[]` con `id`, `name`, `source`, `enabled`, `running`, `online`, `framesPerSecond`, `error`.
* `GET /api/v1/cameras/{id}`
  * **Respuesta:** `CameraView` (200 OK) o 404 NotFound.
* `POST /api/v1/cameras`
  * **Payload:** `{ "id": "cam-1", "name": "Entrada", "source": "0", "enabled": true, "loop": false, "username": null, "password": null }`
  * **Respuesta:** 201 Created con `CameraView`.
* `PUT /api/v1/cameras/{id}`
  * **Payload:** `{ "name": "Entrada Modificada", "source": "0", "enabled": true, "loop": false }`
  * **Respuesta:** 200 OK con `CameraView` actualizado.
* `DELETE /api/v1/cameras/{id}`
  * **Respuesta:** 204 NoContent.
* `POST /api/v1/cameras/{id}/start`
  * **Respuesta:** 200 OK con `CameraView` con `running: true`.
* `POST /api/v1/cameras/{id}/stop`
  * **Respuesta:** 200 OK con `CameraView` con `running: false`.
* `POST /api/v1/cameras/test` (Probe de origen sin persistencia)
  * **Payload:** `{ "source": "...", "username": null, "password": null }`
  * **Respuesta:** `{ "ok": true, "width": 1920, "height": 1080, "framesPerSecond": 30.0, "connectionMilliseconds": 120.5, "previewJpeg": "<base64>" }`
* `GET /api/v1/cameras/{id}/snapshot`
  * **Respuesta:** `image/jpeg` binario directo con el último frame disponible.
* `GET /api/v1/cameras/{id}/stream`
  * **Query Params:** `?fps=15&quality=75&apiKey=...`
  * **Content-Type:** `multipart/x-mixed-replace; boundary=--frame`
  * **Respuesta:** Stream MJPEG continuo de frames JPEG con boundary estándar.

### 3.3 Catálogo y Soluciones de IA
* `GET /api/v1/analytics/catalog`
  * **Respuesta:** Array con las 5 analíticas soportadas:
    1. `hand_raise` (Detección de levantamiento de mano en aula/entorno colaborativo).
    2. `person_presence` (Presencia y tiempo de permanencia de personas).
    3. `zone_intrusion` (Intrusión y permanencia en zonas poligonales restringidas).
    4. `line_crossing` (Cruce de líneas direccionales con control de tripwire).
    5. `person_counting` (Conteo y aforo bidireccional de personas en pasos).
* `GET /api/v1/cameras/{cameraId}/analytics`
  * **Respuesta:** `CameraAnalyticInstance[]`.
* `POST /api/v1/cameras/{cameraId}/analytics`
  * **Payload:** `{ "id": "an-1", "analytic_type_id": "person_presence", "name": "Presencia", "enabled": true, "configuration": { "confidence_threshold": 0.7 }, "assigned_zone_ids": ["z1"], "assigned_line_ids": [] }`
  * **Respuesta:** 201 Created con `CameraAnalyticInstance`.
* `PUT /api/v1/cameras/{cameraId}/analytics/{instanceId}`
  * **Payload:** `CameraAnalyticWriteRequest` con campos modificados.
  * **Respuesta:** 200 OK.
* `DELETE /api/v1/cameras/{cameraId}/analytics/{instanceId}`
  * **Respuesta:** 204 NoContent.

### 3.4 Espacios Espaciales (Zonas Poligonales y Líneas Tripwire)
* `GET /api/v1/cameras/{cameraId}/zones`
  * **Respuesta:** `NormalizedZone[]` con `name` y `polygon: NormalizedPoint[]` (coordenadas 0.0 - 1.0).
* `PUT /api/v1/cameras/{cameraId}/zones`
  * **Payload:** `NormalizedZone[]` (reemplazo completo de zonas para la cámara).
  * **Respuesta:** 200 OK con array guardado.
* `GET /api/v1/cameras/{cameraId}/lines`
  * **Respuesta:** `LineDefinition[]` con `id`, `camera_id`, `name`, `point_a`, `point_b`, `direction_mode` (`"bidirectional"`, `"a_to_b"`, `"b_to_a"`), `enabled`.
* `POST /api/v1/cameras/{cameraId}/lines`
  * **Payload:** `LineDefinition`
  * **Respuesta:** 201 Created.
* `PUT /api/v1/cameras/{cameraId}/lines/{lineId}`
  * **Payload:** `LineDefinition`
  * **Respuesta:** 200 OK.
* `DELETE /api/v1/cameras/{cameraId}/lines/{lineId}`
  * **Respuesta:** 204 NoContent.

### 3.5 Motor de Reglas y Centro de Alertas
* `GET /api/v1/cameras/{cameraId}/analytics/{instanceId}/rules`
  * **Respuesta:** `AlertRule[]`.
* `POST /api/v1/cameras/{cameraId}/analytics/{instanceId}/rules`
  * **Payload:** `{ "id": "r1", "name": "Regla", "enabled": true, "severity": "high", "event_types": ["person_presence_detected"], "conditions": [{ "field": "confidence", "operator": "greater_or_equal", "value": 0.7 }], "cooldown_ms": 5000, "title_template": "...", "description_template": "..." }`
  * **Respuesta:** 201 Created.
* `PUT /api/v1/cameras/{cameraId}/analytics/{instanceId}/rules/{ruleId}`
  * **Payload:** `AlertRule`
  * **Respuesta:** 200 OK.
* `DELETE /api/v1/cameras/{cameraId}/analytics/{instanceId}/rules/{ruleId}`
  * **Respuesta:** 204 NoContent.
* `GET /api/v1/alerts`
  * **Query Params:** `?limit=100&severity=high&status=open`
  * **Respuesta:** `OperationalAlert[]` con `id`, `rule_id`, `camera_id`, `analytic_instance_id`, `source_event_id`, `severity`, `status` (`"open"`, `"acknowledged"`, `"resolved"`), `title`, `description`, `created_at`, `acknowledged_at`, `resolved_at`, `metadata`.
* `POST /api/v1/alerts/{alertId}/acknowledge`
  * **Respuesta:** 200 OK con alerta actualizada.
* `POST /api/v1/alerts/{alertId}/resolve`
  * **Respuesta:** 200 OK con alerta actualizada.

### 3.6 Registro de Modelos y Runtime Plan
* `GET /api/v1/models`
  * **Respuesta:** `ModelDescriptor[]` con capacidades de inferencia, resolución de entrada, ruta, umbrales y dispositivo preferido.
* `GET /api/v1/cameras/{cameraId}/runtime-plan`
  * **Respuesta:** `CapabilityExecutionPlan` con:
    * `camera_id`: string
    * `required_capabilities`: InferenceCapability[]
    * `selected_providers`: SelectedProviderInfo[]
    * `active_analytics_count`: int
    * `dependent_analytic_ids`: string[]

### 3.7 Integraciones y Políticas de Notificación
* `GET /api/v1/notifications/destinations`
  * **Respuesta:** `NotificationDestination[]` (Tipos: `Webhook`, `Mqtt`).
* `POST /api/v1/notifications/destinations`
  * **Payload:** `{ "id": "dest-1", "name": "Webhook Central", "type": "Webhook", "enabled": true, "configuration": { "url": "https://server.com/hook", "method": "POST" } }`
  * **Respuesta:** 201 Created.
* `POST /api/v1/notifications/destinations/{id}/test`
  * **Respuesta:** `{ "ok": true, "status_code": 200, "duration_ms": 45.2, "error": null }`
* `GET /api/v1/notifications/policies`
  * **Respuesta:** `NotificationPolicy[]`.
* `POST /api/v1/notifications/policies`
  * **Payload:** `{ "id": "pol-1", "name": "Notificar Críticas", "enabled": true, "destination_id": "dest-1", "min_severity": "high", "camera_ids": [] }`
  * **Respuesta:** 201 Created.
* `GET /api/v1/notifications/attempts`
  * **Respuesta:** `NotificationAttempt[]` (historial de despachos).

---

## 4. Especificación de WebSockets

### 4.1 Transmisión de Eventos en Tiempo Real (`/api/v1/ws/events`)
* **URL:** `ws://127.0.0.1:5080/api/v1/ws/events?apiKey=<key>` (o con header `X-Api-Key`).
* **Query Opcional:** `?camera_id=<id>` (filtra eventos exclusivamente para una cámara).
* **Payload JSON Emitido:**
```json
{
  "id": "evt-12345",
  "camera_id": "cam-1",
  "timestamp": "2026-10-04T10:00:00.123Z",
  "event_type": "person_presence_detected",
  "analytic_instance_id": "an-cam1-presence",
  "confidence": 0.88,
  "metadata": {
    "dwell_time_ms": 3200,
    "zone_name": "Zona E2E"
  }
}
```

### 4.2 Métricas Periódicas de Pipeline (`/api/v1/ws/metrics`)
* **URL:** `ws://127.0.0.1:5080/api/v1/ws/metrics?apiKey=<key>&interval=1.0`
* **Payload JSON Emitido:**
```json
{
  "node_id": "local-node",
  "site_id": "default-site",
  "timestamp": "2026-10-04T10:00:01.000Z",
  "cameras": [
    {
      "camera_id": "cam-1",
      "fps": 29.8,
      "inference_ms": 14.2,
      "status": "running"
    }
  ],
  "aggregate_fps": 29.8,
  "device": "DirectML: NVIDIA GeForce RTX 2050"
}
```

---

## 5. Validación de Integración y Sello

| Requisito | Estado | Observaciones |
| :--- | :--- | :--- |
| HandRaise Solution C# Tests | **374/374 SUPERADOS** | 100% verde (Domain, Application, Windows Infra, Host, Desktop) |
| React Test Suite | **224/224 SUPERADOS** | 100% verde (Contratos R8, R9, R10, R11) |
| React TypeScript & Vite Build | **0 ERRORES** | Build exitoso y limpio |
| Publicación Standalone | **VALIDADO** | `dist\HandRaise\VisionControl.Edge.exe` operativo |
| DirectML Hardware Probe | **VALIDADO** | RTX 2050 (3962 MB), Radeon Graphics y CPU |
| Prospección de Archivo Local | **VALIDADO** | `.local-test/bus.mp4` probado con resolución y FPS reales |
| Prospección de Cámara USB | **VALIDADO** | Enumerador WinRT con fallback seguro a índice 0 |

