# Auditoría Técnica de la API `/api/v1` (Vision Edge Node)

Fecha de auditoría: 2 de Octubre de 2026  
Estándares aplicados: RESTful, RFC 9110 (HTTP Semantics), RFC 7807 (ProblemDetails JSON), WebSocket RFC 6455.

---

## 1. Matriz de Endpoints Implementados vs Comportamiento

| Método | Ruta | Auth | Estado HTTP Éxito | Códigos de Error | ProblemDetails RFC 7807 | Estado de Implementación |
|---|---|---|---|---|---|---|
| **GET** | `/api/v1/health` | Lectura (Pública/Localhost) | 200 OK | 503 Service Unavailable | Sí | Completo y verificado |
| **GET** | `/api/v1/metrics` | Lectura | 200 OK | 500 Internal Error | Sí | Completo y verificado |
| **GET** | `/api/v1/devices` | Lectura | 200 OK | 500 Internal Error | Sí | Completo (DXGI + CPU) |
| **GET** | `/api/v1/system/runtime` | Lectura | 200 OK | 500 Internal Error | Sí | Completo |
| **GET** | `/api/v1/system/capacity`| Lectura | 200 OK | 500 Internal Error | Sí | Completo |
| **PUT** | `/api/v1/system/device`  | Escritura (`X-Api-Key`) | 200 OK | 400 Bad Request, 401, 500 | Sí | Completo con cambio seguro |
| **GET** | `/api/v1/cameras` | Lectura | 200 OK | 401, 500 | Sí | Completo (sin secretos RTSP) |
| **POST**| `/api/v1/cameras` | Escritura (`X-Api-Key`) | 201 Created | 400, 401, 409 Conflict | Sí | Completo (con DPAPI) |
| **GET** | `/api/v1/cameras/{id}` | Lectura | 200 OK | 401, 404 Not Found | Sí | Completo |
| **PUT** | `/api/v1/cameras/{id}` | Escritura (`X-Api-Key`) | 200 OK | 400, 401, 404 | Sí | Completo |
| **DELETE**|`/api/v1/cameras/{id}`| Escritura (`X-Api-Key`) | 204 No Content | 401, 404 | Sí | Completo |
| **POST**| `/api/v1/cameras/{id}/start` | Escritura (`X-Api-Key`) | 200 OK | 401, 404, 409 Conflict | Sí | Completo |
| **POST**| `/api/v1/cameras/{id}/stop`  | Escritura (`X-Api-Key`) | 200 OK | 401, 404 | Sí | Completo |
| **POST**| `/api/v1/cameras/test` | Escritura (`X-Api-Key`) | 200 OK | 400, 401, 504 Gateway Timeout | Sí | Completo con timeout seguro |
| **GET** | `/api/v1/cameras/{id}/snapshot` | Lectura | 200 OK (`image/jpeg`) | 404 Not Found | No (Binario) | Completo (último frame) |
| **GET** | `/api/v1/cameras/{id}/zones` | Lectura | 200 OK | 404 Not Found | Sí | Completo |
| **PUT** | `/api/v1/cameras/{id}/zones` | Escritura (`X-Api-Key`) | 200 OK | 400, 401, 404 | Sí | Completo con hot-apply |
| **GET** | `/api/v1/cameras/{id}/stream`| Lectura (`api_key` param/header) | 200 OK (`multipart/x-mixed-replace`) | 401, 404, 503 (Max clients) | Sí (antes de multipart) | Completo (MJPEG desacoplado) |
| **GET** | `/api/v1/events` | Lectura | 200 OK | 400, 401 | Sí | Completo (paginado/filtros) |
| **GET** | `/api/v1/events/{id}/snapshot` | Lectura | 200 OK (`image/jpeg`) | 404 Not Found | No (Binario) | Completo |
| **GET** | `/api/v1/stats` | Lectura | 200 OK | 400, 401 | Sí | Completo (agregaciones UTC) |
| **WS**  | `/api/v1/ws/events` | Lectura (Auth en query/header) | 101 Switching Protocols | 401, 400 | N/A | Completo (Bounded Channel) |
| **WS**  | `/api/v1/ws/metrics`| Lectura (Auth en query/header) | 101 Switching Protocols | 401, 400 | N/A | Completo (Snapshot periódico) |

---

## 2. Consistencia y Cumplimiento de Contratos

### Fortalezas de la Implementación:
1. **Zero Secret Leaks**: Las credenciales de cámaras RTSP almacenadas con DPAPI nunca son retornadas en `GET /api/v1/cameras` ni `/cameras/{id}`. Solo se devuelven campos higienizados (`has_password: true`, URLs sanitizadas `rtsp://user:***@host...`).
2. **Formato ProblemDetails RFC 7807 Homogéneo**: Todas las respuestas de error 4xx y 5xx devuelven un JSON estructurado con `type`, `title`, `status`, `detail` y `traceId`.
3. **Resiliencia de Streaming y WebSockets**: Los WebSockets y streams MJPEG implementan canales desacoplados (`Channel<T>` con `BoundedChannelFullMode.DropOldest`), evitando que un cliente de red lento o descongelado bloquee el pipeline de visión de la cámara.

---

## 3. Extensiones Recomendadas para el Frontend React

Para completar la experiencia industrial del frontend React sin necesidad de acceso a consola de Windows:

1. **`GET /api/v1/health/readiness`**:
   - Devuelve `200 OK` si el nodo está listo para procesar (modelos validados, storage listo, backends inicializados) o `503 Service Unavailable` con detalle de lo que falta durante el arranque.
2. **`GET /api/v1/system/diagnostics`**:
   - Genera un payload JSON consolidado con uptime, memoria del proceso, temperatura/VRAM de GPU si está disponible, espacio en disco remanente en `%LOCALAPPDATA%`, conteo de eventos y estado de cada pipeline.
3. **`POST /api/v1/system/restart`**:
   - Permite reiniciar el servicio del nodo de forma remota y controlada desde el frontend administrativo.
