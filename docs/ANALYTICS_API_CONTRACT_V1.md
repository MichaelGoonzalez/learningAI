# VisionControl Edge — Analytics API Contract v1

Este documento define el contrato canónico compartido entre el nodo de borde (**VisionControl Edge**) y las consolas/clientes web (**VisionControl Console / React**).

---

## 1. Convenciones JSON y HTTP

- **Naming Convention**: `snake_case` para todas las claves JSON y parámetros de consulta.
- **Enums**: Cadenas en minúsculas / snake_case (ej. `object_detection`, `in_progress`, `active`).
- **Fechas y Tiempos**: Cadenas formateadas en ISO-8601 UTC terminadas en `Z` (ej. `2026-10-03T04:00:00.000Z`).
- **Valores Nulos**: Los campos opcionales omiten la clave o devuelven `null`.
- **Colecciones Vacías**: Se representan como arrays vacíos `[]`, nunca `null`.
- **Metadata**: Objetos JSON clave-valor tipados (`Record<string, unknown>`) para atributos heterogéneos específicos de cada analítica.

---

## 2. Enums Canónicos

### 2.1. `AnalyticStatus`
Representa el estado operativo de una instancia analítica en una cámara:
- `pending`: Registrada pero esperando inicialización de modelos o pipeline.
- `active`: Procesando frames y emitiendo eventos activamente.
- `degraded`: Operando con rendimiento reducido (ej. caída de FPS o pérdida temporal de tracking).
- `inactive`: Deshabilitada por el usuario (`enabled: false`).
- `error`: Fallo crítico durante la ejecución de la analítica.
- `unsupported`: La analítica requiere capacidades de hardware/modelo no disponibles en el nodo.

### 2.2. `AnalyticCategory`
Clasificación temática en el catálogo:
- `general`: Analíticas de propósito general.
- `security`: Seguridad perimetral e intrusiones.
- `safety`: Seguridad industrial y EPP.
- `operations`: Control de operaciones y procesos.
- `logistics`: Conteo, flujos y control de almacenes.
- `quality`: Control de calidad y defectos.

### 2.3. `InferenceCapability`
Requisitos técnicos del runtime de inferencia (hardware/red neuronal):
- `object_detection`: Detección de cajas delimitadoras (Bounding Boxes).
- `pose_estimation`: Estimación de puntos clave corporales (Keypoints).
- `tracking`: Seguimiento temporal e identificación de trayectorias (Track IDs).
- `classification`: Clasificación de objetos o escenas.
- `segmentation`: Máscaras de segmentación por píxel.

### 2.4. `AnalyticFeature`
Capacidades funcionales y configurables soportadas por la analítica:
- `zones`: Soporta asociación con zonas poligonales.
- `lines`: Soporta líneas direccionales de cruce.
- `points`: Soporta puntos de interés.
- `sensitivity`: Parámetro configurable de sensibilidad/umbral.
- `snapshots`: Genera y adjunta capturas visuales en sus eventos.
- `tracking`: Requiere y utiliza identificadores de seguimiento continuo.
- `schedules`: Soporta programación horaria de activación.

### 2.5. `ParameterType`
Tipo de dato para parámetros configurables de analítica:
- `number`: Valor numérico (con `min`, `max`, `step` opcionales).
- `boolean`: Conmutador booleano (`true` / `false`).
- `string`: Texto libre o identificador.
- `select`: Selección única a partir de una lista de `options` (`value`, `label`).

---

## 3. Schemas de Entidades y Ejemplos JSON

### 3.1. `AnalyticDefinition` (Elemento del Catálogo)
Describe una capacidad analítica disponible para ser instalada en cámaras:

```json
{
  "id": "hand_raise",
  "display_name": "Mano Levantada",
  "description": "Detecta personas con una o ambas manos alzadas por encima del hombro.",
  "category": "operations",
  "version": "1.0.0",
  "required_capabilities": [
    "pose_estimation",
    "tracking"
  ],
  "features": [
    "zones",
    "snapshots",
    "tracking",
    "sensitivity"
  ],
  "produced_event_types": [
    "hand_raised",
    "hand_lowered"
  ],
  "parameters": [
    {
      "key": "strict_mode",
      "label": "Modo Estricto",
      "type": "boolean",
      "default_value": false,
      "description": "Exige que la muñeca supere el margen superior con holgura estricta."
    },
    {
      "key": "consecutive_frames",
      "label": "Frames Consecutivos",
      "type": "number",
      "default_value": 3,
      "min": 1,
      "max": 30,
      "step": 1,
      "description": "Frames requeridos para confirmar el gesto."
    },
    {
      "key": "cooldown_ms",
      "label": "Tiempo de Cooldown (ms)",
      "type": "number",
      "default_value": 1000,
      "min": 0,
      "max": 10000,
      "step": 100,
      "description": "Tiempo mínimo antes de reemitir un evento para el mismo track."
    }
  ]
}
```

---

### 3.2. `CameraAnalyticInstance` (Instancia en Cámara)
Representa una analítica configurada y asociada a una cámara física:

```json
{
  "id": "an-cam01-handraise",
  "camera_id": "cam-01",
  "analytic_type_id": "hand_raise",
  "name": "Detección Manos Recepción",
  "enabled": true,
  "status": "active",
  "configuration": {
    "strict_mode": false,
    "consecutive_frames": 3,
    "cooldown_ms": 1000
  },
  "assigned_zone_ids": [
    "zone-mostrador-a"
  ],
  "assigned_line_ids": [],
  "created_at": "2026-10-03T04:00:00Z",
  "updated_at": "2026-10-03T04:00:00Z"
}
```

---

### 3.3. `AnalyticEvent` (Evento Canónico de Dominio)
Evento producido cuando se cumple una condición analítica en un frame:

```json
{
  "id": "cam-01:14:1790971200000:hand_raised",
  "camera_id": "cam-01",
  "analytic_instance_id": "an-cam01-handraise",
  "analytic_type": "hand_raise",
  "event_type": "hand_raised",
  "timestamp_utc": "2026-10-03T04:00:00.000Z",
  "track_id": 14,
  "zone_id": "zone-mostrador-a",
  "confidence": 0.94,
  "metadata": {
    "hand": "right",
    "hand_side": "right",
    "keypoint_confidence": 0.91
  },
  "snapshot_url": "/api/v1/events/cam-01:14:1790971200000:hand_raised/snapshot",
  "node_id": "edge-node-01",
  "site_id": "default-site"
}
```

---

## 4. Endpoints Reservados (Contrato REST v1)

| Método | Ruta | Descripción |
|---|---|---|
| `GET` | `/api/v1/analytics/catalog` | Obtiene la lista completa de definiciones analíticas soportadas. |
| `GET` | `/api/v1/cameras/{cameraId}/analytics` | Lista todas las instancias analíticas asociadas a una cámara. |
| `POST` | `/api/v1/cameras/{cameraId}/analytics` | Instala y configura una nueva analítica en una cámara. |
| `GET` | `/api/v1/cameras/{cameraId}/analytics/{instanceId}` | Obtiene el detalle y estado de una instancia analítica. |
| `PUT` | `/api/v1/cameras/{cameraId}/analytics/{instanceId}` | Actualiza configuración, zonas asignadas o estado de la analítica. |
| `DELETE` | `/api/v1/cameras/{cameraId}/analytics/{instanceId}` | Desinstala y elimina la instancia analítica de la cámara. |

---

## 5. Reglas de Compatibilidad y Migración

1. **Analítica Inicial (`hand_raise`)**:
   - `HandRaise` está registrado como el primer elemento estándar en el catálogo con `id: "hand_raise"`.
   - En cámaras existentes sin colección explícita de analíticas, el nodo genera implícitamente una instancia `CameraAnalyticInstance` con `analytic_type_id: "hand_raise"`.
2. **Compatibilidad Legacy de Eventos**:
   - Los eventos de `hand_raise` continúan emitiéndose en el bus interno y son serializables tanto como `AnalyticEvent` canónico como en el formato histórico `HandEvent` (`hand`, `zone`, etc.).
   - No se introducen cambios de ruptura en `/api/v1/events`, `/api/v1/health` ni en los WebSockets actuales.
