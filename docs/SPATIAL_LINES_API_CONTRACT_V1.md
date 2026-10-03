# SPATIAL_LINES_API_CONTRACT_V1

Este documento define el contrato de API REST, estructuras de datos, semántica geométrica y comportamiento de runtime para **Líneas Virtuales (Tripwires)** y las analíticas basadas en líneas (**Line Crossing** y **Person Counting**) en VisionControl Edge.

---

## 1. Estructura de Datos: LineDefinition

Una línea virtual se define como un segmento dirigido en coordenadas normalizadas `[0.0, 1.0]`.

### Wire JSON Schema

```json
{
  "id": "line-01",
  "camera_id": "cam-01",
  "name": "Entrada Principal",
  "point_a": {
    "x": 0.1,
    "y": 0.5
  },
  "point_b": {
    "x": 0.9,
    "y": 0.5
  },
  "direction_mode": "bidirectional",
  "enabled": true
}
```

### Campos y Restricciones

| Campo | Tipo | Requerido | Descripción / Validación |
|---|---|---|---|
| `id` | `string` | Sí | Identificador único de la línea en la cámara (no vacío). |
| `camera_id` | `string` | Sí | Identificador de la cámara asociada. |
| `name` | `string` | Sí | Nombre legible para visualización y dashboards. |
| `point_a` | `{ "x": float, "y": float }` | Sí | Punto de origen del segmento. Coordenadas `0.0 <= x, y <= 1.0`. |
| `point_b` | `{ "x": float, "y": float }` | Sí | Punto de fin del segmento. Coordenadas `0.0 <= x, y <= 1.0`. No idéntico a `point_a`. |
| `direction_mode` | `string` | No (def: `"bidirectional"`) | Modo de cruce permitido: `"bidirectional"`, `"a_to_b"`, `"b_to_a"`. |
| `enabled` | `boolean` | No (def: `true`) | Activa o pausa la evaluación de la línea. |

### Valores de DirectionMode

- `"bidirectional"`: Registra transiciones en ambos sentidos (`Side A -> Side B` y `Side B -> Side A`).
- `"a_to_b"`: Solo registra transiciones desde el Lado A hacia el Lado B.
- `"b_to_a"`: Solo registra transiciones desde el Lado B hacia el Lado A.

---

## 2. Semántica Geométrica y Lados de Línea

Dado un segmento orientado desde $A(x_a, y_a)$ hacia $B(x_b, y_b)$, la distancia con signo hacia un punto $P(x_p, y_p)$ viene dada por:

$$\text{SignedDistance}(P, A, B) = \frac{(x_b - x_a)(y_p - y_a) - (y_b - y_a)(x_p - x_a)}{\sqrt{(x_b - x_a)^2 + (y_b - y_a)^2}}$$

- **Side A**: $\text{SignedDistance} > +0.015$ (banda de histeresis).
- **Side B**: $\text{SignedDistance} < -0.015$ (banda de histeresis).
- **OnLine**: $|\text{SignedDistance}| \le 0.015$ (zona neutra anti-rebote).

### Punto de Referencia de Persona

El cruce de una persona se calcula evaluando el **punto inferior central** de su caja delimitadora:
$$P_x = \frac{\text{Box.Left} + \text{Box.Right}}{2 \cdot \text{FrameWidth}}, \quad P_y = \frac{\text{Box.Bottom}}{\text{FrameHeight}}$$

---

## 3. Endpoints REST de Líneas (`/api/v1/cameras/{cameraId}/lines`)

Todos los endpoints requieren autenticación mediante encabezado `X-Api-Key` (o parámetro de consulta autorizado).

### 3.1 `GET /api/v1/cameras/{cameraId}/lines`
Devuelve la lista completa de líneas configuradas para la cámara.

- **Respuesta Exitosa**: `200 OK`
  ```json
  [
    {
      "id": "line-01",
      "camera_id": "cam-01",
      "name": "Entrada Principal",
      "point_a": { "x": 0.1, "y": 0.5 },
      "point_b": { "x": 0.9, "y": 0.5 },
      "direction_mode": "bidirectional",
      "enabled": true
    }
  ]
  ```
- **Errores**: `404 Not Found` si la cámara no existe.

### 3.2 `PUT /api/v1/cameras/{cameraId}/lines`
Reemplazo atómico de la colección de líneas de la cámara.

- **Body**: `LineDefinition[]`
- **Respuesta Exitosa**: `200 OK` con las líneas guardadas.
- **Errores**:
  - `400 Bad Request` (`application/problem+json`) si las coordenadas o identificadores son inválidos.
  - `404 Not Found` si la cámara no existe.

### 3.3 `POST /api/v1/cameras/{cameraId}/lines`
Creación o inserción de una línea individual.

- **Body**: `LineDefinition`
- **Respuesta Exitosa**: `201 Created` con encabezado `Location: /api/v1/cameras/{cameraId}/lines/{id}`.
- **Errores**: `400 Bad Request`, `404 Not Found`.

### 3.4 `PUT /api/v1/cameras/{cameraId}/lines/{lineId}`
Actualización de una línea individual existente.

- **Body**: `LineDefinition`
- **Respuesta Exitosa**: `200 OK`
- **Errores**: `400 Bad Request`, `404 Not Found`.

### 3.5 `DELETE /api/v1/cameras/{cameraId}/lines/{lineId}`
Eliminación de una línea individual.

- **Respuesta Exitosa**: `204 No Content`
- **Errores**: `404 Not Found`.

---

## 4. Analítica: `line_crossing` (Cruce de Línea)

### Parámetros Canónicos

| Parámetro | Tipo | Default | Rango | Descripción |
|---|---|---|---|---|
| `confidence_threshold` | `number` | `0.60` | `[0.10, 0.95]` | Confianza mínima de detección para considerar el track. |
| `crossing_cooldown_ms` | `number` | `1500` | `[0, 30000]` | Tiempo mínimo (ms) antes de admitir un nuevo cruce del mismo track. |
| `emit_snapshot` | `boolean` | `true` | `true/false` | Adjunta snapshot del cruce al evento emitido. |

### Requisitos de Instancia

- `assigned_line_ids`: Array obligatorio con al menos un ID de línea existente en la cámara.
- Emite evento: `line_crossed`.

### Evento Emitido (`line_crossed`)

```json
{
  "id": "ev-lc-a1b2c3d4",
  "camera_id": "cam-01",
  "analytic_instance_id": "an-cam-01-lc-1",
  "analytic_type": "line_crossing",
  "event_type": "line_crossed",
  "timestamp_utc": "2026-10-03T12:00:00.000Z",
  "track_id": 42,
  "confidence": 0.88,
  "metadata": {
    "direction": "a_to_b",
    "line_id": "line-01",
    "line_name": "Entrada Principal"
  },
  "snapshot_url": "/api/v1/events/ev-lc-a1b2c3d4/snapshot"
}
```

---

## 5. Analítica: `person_counting` (Conteo y Aforo)

### Parámetros Canónicos

| Parámetro | Tipo | Default | Rango | Descripción |
|---|---|---|---|---|
| `confidence_threshold` | `number` | `0.60` | `[0.10, 0.95]` | Confianza mínima de detección. |
| `entry_direction` | `select` | `"a_to_b"` | `["a_to_b", "b_to_a"]` | Sentido del cruce que incrementa las entradas (`entries++`). El opuesto incrementa las salidas (`exits++`). |
| `initial_occupancy` | `number` | `0` | `[0, 100000]` | Ocupación base al iniciar la analítica. |
| `minimum_occupancy` | `number` | `0` | `[0, 100000]` | Cota inferior de aforo para evitar números negativos. |
| `maximum_occupancy` | `number` | `0` | `[0, 100000]` | Umbral de aforo que dispara alertas. `0` = deshabilitado. |

### Fórmula de Ocupación en Tiempo Real

$$\text{current\_occupancy} = \max(\text{minimum\_occupancy}, \text{initial\_occupancy} + \text{entries} - \text{exits})$$

### Comportamiento de Alerta y Rearmado (`occupancy_threshold_reached`)

- Dispara `occupancy_threshold_reached` cuando `current_occupancy >= maximum_occupancy` (con `maximum_occupancy > 0`).
- No vuelve a disparar duplicados mientras la ocupación permanezca por encima o igual al umbral.
- Se rearma automáticamente cuando `current_occupancy < maximum_occupancy`.

### Comportamiento al Reiniciar (Restart)

El estado en memoria (`entries`, `exits`, `current_occupancy`) es un estado de runtime. Al reiniciar el proceso, nodo o pipeline de la cámara:
- `entries` y `exits` se inicializan en `0`.
- `current_occupancy` se reconstruye en $\max(\text{minimum\_occupancy}, \text{initial\_occupancy})$.

### Eventos Emitidos

1. `person_count_updated`: Disparado en cada cruce de entrada o salida.
   ```json
   {
     "id": "ev-pc-11223344",
     "camera_id": "cam-01",
     "analytic_instance_id": "an-cam-01-pc-1",
     "analytic_type": "person_counting",
     "event_type": "person_count_updated",
     "timestamp_utc": "2026-10-03T12:00:00.000Z",
     "track_id": 42,
     "confidence": 0.88,
     "metadata": {
       "entries": 5,
       "exits": 2,
       "current_occupancy": 13,
       "direction": "a_to_b",
       "line_id": "line-01",
       "line_name": "Entrada Principal"
     }
   }
   ```

2. `occupancy_threshold_reached`: Disparado al alcanzar el aforo máximo configurado.
   ```json
   {
     "id": "ev-pc-thresh-998877",
     "camera_id": "cam-01",
     "analytic_instance_id": "an-cam-01-pc-1",
     "analytic_type": "person_counting",
     "event_type": "occupancy_threshold_reached",
     "timestamp_utc": "2026-10-03T12:00:00.000Z",
     "track_id": 42,
     "confidence": 0.88,
     "metadata": {
       "current_occupancy": 50,
       "maximum_occupancy": 50,
       "entries": 52,
       "exits": 2,
       "line_id": "line-01",
       "line_name": "Entrada Principal"
     }
   }
   ```

---

## 6. Persistencia y Formato en Disco

Las líneas virtuales se persisten en formato JSON bajo:
`%LOCALAPPDATA%\HandRaiseDetection\lines.json` (o la ruta configurada en `storage.linesStorePath`).

Formato:
```json
{
  "cam-01": [
    {
      "id": "line-01",
      "camera_id": "cam-01",
      "name": "Entrada Principal",
      "point_a": { "x": 0.1, "y": 0.5 },
      "point_b": { "x": 0.9, "y": 0.5 },
      "direction_mode": "bidirectional",
      "enabled": true
    }
  ]
}
```
Operaciones de lectura/escritura son thread-safe y utilizan escritura atómica en archivo temporal con reemplazo de archivo.
