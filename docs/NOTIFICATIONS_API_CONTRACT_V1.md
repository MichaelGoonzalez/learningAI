# NOTIFICATIONS_API_CONTRACT_V1 — VisionControl Edge

## Visión General
El subsistema de notificaciones en VisionControl Edge permite el enrutamiento y despacho asíncrono y desacoplado de alertas operacionales (`OperationalAlert`) hacia destinos externos (`NotificationDestination`) regulados por políticas de notificación (`NotificationPolicy`).

---

## Flujo Arquitectónico
```
AnalyticEvent
  ↓
RuleEngine
  ↓
OperationalAlert
  ↓
Notification Dispatcher (asíncrono con reintentos)
  ├── Policy Matching (Severidad, Cámara, Analítica, Estado)
  ↓
Notification Destination (Webhook HTTP / MQTT Broker)
  ↓
NotificationAttempt (Audit log)
```

---

## 1. Entidades de Dominio

### NotificationDestination
Define un canal o endpoint de transporte externo.
```json
{
  "id": "dest-webhook-main",
  "name": "Servidor Central de Eventos",
  "type": "webhook",
  "enabled": true,
  "configuration": {
    "url": "https://alerts.empresa.com/api/v1/ingest",
    "method": "POST",
    "timeout_ms": 5000,
    "secret_token": "********",
    "headers": {
      "Authorization": "********",
      "X-Custom-Header": "production"
    }
  },
  "created_at": "2026-10-03T12:00:00Z",
  "updated_at": "2026-10-03T12:00:00Z"
}
```

### NotificationPolicy
Regla de enrutamiento que define qué alertas son despachadas a qué destino.
```json
{
  "id": "pol-critical-alerts",
  "name": "Alertas Críticas hacia SOC",
  "enabled": true,
  "destination_id": "dest-webhook-main",
  "severity_filter": ["high", "critical"],
  "camera_ids": ["cam-01", "cam-02"],
  "analytic_type_ids": ["zone_intrusion", "person_counting"],
  "alert_status_filter": ["open"],
  "created_at": "2026-10-03T12:00:00Z",
  "updated_at": "2026-10-03T12:00:00Z"
}
```

### NotificationAttempt
Registro de auditoría de cada intento de despacho.
```json
{
  "id": "att-12345678",
  "alert_id": "alert-001",
  "policy_id": "pol-critical-alerts",
  "destination_id": "dest-webhook-main",
  "attempt_number": 1,
  "status": "succeeded",
  "timestamp_utc": "2026-10-03T12:00:05Z",
  "response_code": 200,
  "error_sanitized": null,
  "metadata": {
    "topic": "visioncontrol/local-node/alerts"
  }
}
```

---

## 2. Tipos de Destino y Configuraciones

### A. Webhook (`type: "webhook"`)
- `url`: (Obligatorio) URL de destino (`https://...` o `http://...` si se permite).
- `method`: `POST` (por defecto) o `PUT`.
- `timeout_ms`: Tiempo de espera en milisegundos (por defecto 5000ms).
- `secret_token`: Token opcional que se adjunta en cabecera `X-Webhook-Token`.
- `headers`: Diccionario de cabeceras HTTP personalizadas.

### B. MQTT (`type: "mqtt"`)
- `broker`: (Obligatorio) Host o IP del broker MQTT.
- `port`: Puerto TCP (1883 sin TLS, 8883 con TLS).
- `tls`: Booleano para activar cifrado TLS (por defecto `false`).
- `topic`: Plantilla de topic MQTT (por defecto `visioncontrol/{node_id}/alerts`). Soporta variables: `{node_id}`, `{site_id}`, `{camera_id}`, `{severity}`.
- `client_id`: Identificador de cliente MQTT (por defecto `vc-edge-{node_id}`).
- `username`: Usuario de autenticación (opcional).
- `password`: Contraseña de autenticación (opcional).
- `qos`: Nivel de calidad de servicio (0 o 1, por defecto 0).
- `timeout_ms`: Tiempo de espera de conexión y publicación (por defecto 5000ms).

---

## 3. Payload de Webhook y MQTT (`WebhookPayload`)
```json
{
  "schema_version": "1.0",
  "node_id": "local-node",
  "site_id": "default-site",
  "alert": {
    "id": "alert-1234abcd",
    "rule_id": "rule-aforo-50",
    "camera_id": "cam-01",
    "analytic_instance_id": "an-cam-01-count",
    "source_event_id": "ev-998877",
    "severity": "high",
    "status": "open",
    "title": "Aforo Excedido en cam-01",
    "description": "Ocupación actual: 52 personas",
    "created_at": "2026-10-03T12:00:00Z",
    "metadata": {
      "current_occupancy": 52
    }
  },
  "dispatched_at": "2026-10-03T12:00:01Z",
  "is_test": false
}
```

---

## 4. Reintentos y Aislamiento de Fallas
- **Reintentos escalonados**: 3 intentos por defecto con demoras de `1s`, `5s`, `15s`.
- **Aislamiento**: Si un destino o webhook externo falla, se registra el fallo en `notification-attempts.json`. El motor de reglas, el bus de eventos y el pipeline de captura de video continúan operando normalmente sin interrupción ni latencia.
- **Sanitización**: Todo error o traza elimina contraseñas, bearer tokens y claves privadas (`[REDACTED]`).
- **Redacción de Secretos en API**: Todo endpoint `GET` enmascara tokens y contraseñas con `"********"`.

---

## 5. Endpoints REST (`/api/v1`)

| Método | Ruta | Descripción |
|---|---|---|
| `GET` | `/notifications/destinations` | Listar destinos configurados (secretos redactados). |
| `POST` | `/notifications/destinations` | Registrar nuevo destino. |
| `GET` | `/notifications/destinations/{id}` | Consultar destino específico. |
| `PUT` | `/notifications/destinations/{id}` | Actualizar destino existente. |
| `DELETE` | `/notifications/destinations/{id}` | Eliminar destino. |
| `POST` | `/notifications/destinations/{id}/test` | Enviar notificación de prueba (`is_test: true`) y verificar conectividad. |
| `GET` | `/notifications/policies` | Listar políticas de notificación. |
| `POST` | `/notifications/policies` | Crear política de notificación. |
| `GET` | `/notifications/policies/{id}` | Consultar política específica. |
| `PUT` | `/notifications/policies/{id}` | Actualizar política existente. |
| `DELETE` | `/notifications/policies/{id}` | Eliminar política. |
| `GET` | `/notifications/attempts` | Consultar auditoría de intentos de entrega (`alert_id`, `destination_id`, `status`, `limit`, `offset`). |
