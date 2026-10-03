# Contratos de API y Tiempo Real (`/api/v1`)

El host headless (`HandRaise.Host`) expone una API REST y WebSockets en `http://127.0.0.1:5080/api/v1`.

## Autenticación

- **Lecturas:** Requieren API key si está configurada en `appsettings.json` (`api.apiKey`). Sin clave configurada, solo aceptan conexiones desde `localhost` (`127.0.0.1`).
- **Escrituras:** Exigen siempre API key configurada y suministrada.
- **Suministro de Clave:**
  - Cabecera HTTP: `X-Api-Key: <clave>`
  - Parámetro de consulta URL (ideal para WebSockets y etiquetas `<img>` de navegador): `?api_key=<clave>` o `?apiKey=<clave>`

---

## Endpoints REST

| Método | Ruta | Auth | Descripción |
|---|---|---|---|
| `GET` | `/health` | Lectura | Estado básico de salud, uptime y IDs de nodo/sitio. |
| `GET` | `/metrics` | Lectura | Snapshot de rendimiento de pipelines, FPS, latencias, modelo y dispositivo activo. |
| `GET` | `/devices` | Lectura | Inventario de GPUs/aceleradores (DirectML) y CPU disponible. |
| `GET` | `/system/runtime` | Lectura | Información del runtime del sistema y compilación. |
| `GET` | `/system/capacity` | Lectura | Estimación de capacidad máxima de cámaras en base al rendimiento actual. |
| `PUT` | `/system/device` | Escritura | Cambio de backend de inferencia en caliente (`{"deviceId": "gpu-0"}`). |
| `GET` | `/cameras` | Lectura | Listado de cámaras registradas y su estado de ejecución. |
| `GET` | `/cameras/{id}` | Lectura | Detalle de una cámara específica. |
| `POST` | `/cameras` | Escritura | Registra una cámara (`id`, `name`, `source`, `enabled`, `loop`, `username`, `password`). |
| `PUT` | `/cameras/{id}` | Escritura | Actualiza la configuración de una cámara existente. |
| `DELETE`| `/cameras/{id}` | Escritura | Elimina la cámara y detiene su ejecución. |
| `POST` | `/cameras/{id}/start` | Escritura | Inicia el procesamiento de la cámara. |
| `POST` | `/cameras/{id}/stop` | Escritura | Detiene el procesamiento de la cámara. |
| `POST` | `/cameras/test` | Escritura | Prueba de conexión a una fuente de video sin guardarla. |
| `GET` | `/cameras/{id}/snapshot` | Lectura | Último frame capturado como imagen `image/jpeg`. |
| `GET` | `/cameras/{id}/stream` | Lectura | Stream continuo MJPEG (`multipart/x-mixed-replace`) con overlay. |
| `GET` | `/cameras/{id}/zones` | Lectura | Obtiene las zonas poligonales configuradas para la cámara. |
| `PUT` | `/cameras/{id}/zones` | Escritura | Actualiza las zonas normalizadas en caliente. |
| `GET` | `/events` | Lectura | Consulta de eventos históricos paginados y filtrados. |
| `GET` | `/events/{id}/snapshot` | Lectura | Descarga el snapshot JPEG asociado al evento. |
| `GET` | `/stats` | Lectura | Estadísticas agregadas por cámara, zona y franja horaria. |

---

## Streaming y Tiempo Real

### 1. MJPEG Stream (`GET /api/v1/cameras/{id}/stream`)
- **Parámetros Opcionales:**
  - `fps` (double, 1-60): Tasa de cuadros por segundo deseada.
  - `quality` (int, 1-100): Calidad de compresión JPEG.
- **Protección:** Límite configurable de clientes concurrentes por cámara (`streaming.maxStreamClientsPerCamera`). Si se excede, responde `429 Too Many Requests`.
- **Desconexión:** Liberación inmediata del slot y recursos al cerrar la conexión del cliente.

### 2. WebSocket de Eventos (`GET /api/v1/ws/events`)
- **Parámetros Opcionales:**
  - `camera_id` (string): Filtra los eventos emitidos solo para la cámara indicada.
- **Aislamiento:** Cada cliente posee un canal desacoplado y acotado (`BoundedChannelFullMode.DropOldest`). Un cliente lento nunca bloquea el pipeline de inferencia ni a otros clientes.
- **Formato del Mensaje JSON:**
```json
{
  "id": "evt-01923485-abc",
  "type": "hand_raised",
  "camera_id": "cam-1",
  "track_id": 2,
  "hand": "left",
  "zone": "ZONA_DESPACHO",
  "confidence": 0.92,
  "timestamp": "2026-10-02T15:30:00.000Z",
  "snapshot_url": "snapshots/cam-1/evt-01923485-abc.jpg",
  "node_id": "local-node",
  "site_id": "default-site"
}
```

### 3. WebSocket de Métricas (`GET /api/v1/ws/metrics`)
- **Parámetros Opcionales:**
  - `interval_seconds` (double, 0.1-60): Frecuencia de emisión de snapshots (por defecto 2.0s).
- Emite periódicamente el snapshot completo de telemetría de todos los pipelines y recursos de inferencia.
