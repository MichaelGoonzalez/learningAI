# VisionControl Edge — Contrato de API REST v1

## 1. Convenciones Globales

- **Prefijo base**: `/api/v1`
- **Formato**: JSON con nomenclatura `snake_case` estricta.
- **Autenticación**: Encabezado HTTP `X-Api-Key: <token>` en todas las rutas protegidas.
- **Respuestas de Error Estándar**:
```json
{
  "error": "DESCRIPCION_DEL_ERROR",
  "status_code": 400,
  "timestamp": "2026-10-07T00:00:00Z"
}
```

---

## 2. Resumen de Endpoints Principales

### Sistema y Salud
| Método | Ruta | Descripción |
| :--- | :--- | :--- |
| `GET` | `/api/v1/health` | Estado de salud del nodo (liveness probe). |
| `GET` | `/api/v1/system/info` | Información del nodo, versión, uptime y hardware. |
| `GET` | `/api/v1/system/diagnostics` | Métricas de CPU, memoria, GPU y latencias. |

### Administración de Cámaras
| Método | Ruta | Descripción |
| :--- | :--- | :--- |
| `GET` | `/api/v1/cameras` | Lista todas las cámaras configuradas. |
| `POST` | `/api/v1/cameras` | Registra una nueva cámara. |
| `GET` | `/api/v1/cameras/{id}` | Obtiene el detalle y estado de una cámara. |
| `PUT` | `/api/v1/cameras/{id}` | Actualiza el nombre, origen o credenciales de una cámara. |
| `DELETE` | `/api/v1/cameras/{id}` | Elimina una cámara y sus recursos asociados. |
| `POST` | `/api/v1/cameras/{id}/start` | Inicia el procesamiento y streaming de la cámara. |
| `POST` | `/api/v1/cameras/{id}/stop` | Detiene el procesamiento y streaming de la cámara. |
| `GET` | `/api/v1/cameras/{id}/stream` | Flujo de video en vivo (Multipart Motion JPEG). |
| `POST` | `/api/v1/cameras/probe` | Prueba temporal de conectividad con una cámara sin guardarla. |

### Espacios (Zonas y Líneas)
| Método | Ruta | Descripción |
| :--- | :--- | :--- |
| `GET` | `/api/v1/cameras/{id}/zones` | Obtiene las zonas poligonales de una cámara. |
| `PUT` | `/api/v1/cameras/{id}/zones` | Reemplaza las zonas configuradas de una cámara. |
| `GET` | `/api/v1/cameras/{id}/lines` | Obtiene las líneas virtuales de cruce. |
| `PUT` | `/api/v1/cameras/{id}/lines` | Reemplaza las líneas configuradas de una cámara. |

### Soluciones IA y Analíticas
| Método | Ruta | Descripción |
| :--- | :--- | :--- |
| `GET` | `/api/v1/analytics/definitions` | Catálogo de tipos de analíticas y esquemas de parámetros. |
| `GET` | `/api/v1/cameras/{id}/analytics` | Lista las instancias de analíticas asociadas a una cámara. |
| `POST` | `/api/v1/cameras/{id}/analytics` | Asocia una nueva analítica a la cámara. |
| `DELETE` | `/api/v1/cameras/{id}/analytics/{instance_id}` | Elimina una analítica asociada. |

### Reglas y Alertas
| Método | Ruta | Descripción |
| :--- | :--- | :--- |
| `GET` | `/api/v1/cameras/{id}/rules` | Lista las reglas de alerta configuradas para la cámara. |
| `POST` | `/api/v1/cameras/{id}/rules` | Crea una nueva regla para la cámara. |
| `DELETE` | `/api/v1/cameras/{id}/rules/{rule_id}` | Elimina una regla de alerta. |
| `GET` | `/api/v1/alerts` | Consulta el listado de alertas operacionales generadas. |
| `GET` | `/api/v1/alerts/{id}/snapshot` | Descarga la imagen JPEG de evidencia de una alerta. |
| `POST` | `/api/v1/alerts/{id}/acknowledge` | Marca una alerta como reconocida / atendida. |
