# VisionControl Edge — Contrato de Control Remoto

## 1. Contexto y Objetivos

VisionControl Edge permite el control bidireccional y la supervisión en tiempo real desde aplicaciones remotas como **VisionControl Console (React)** mediante su API REST v1.

Todas las peticiones autenticadas requieren el encabezado:
```http
X-Api-Key: <clave_generada_en_edge>
```

---

## 2. Endpoints de Control Remoto de Cámaras

### Iniciar Cámara
- **Método**: `POST`
- **Ruta**: `/api/v1/cameras/{id}/start`
- **Headers**: `X-Api-Key: <api_key>`
- **Respuesta Exitosa (`200 OK`)**:
```json
{
  "camera_id": "cam_01",
  "name": "Acceso Principal",
  "status": "online",
  "running": true,
  "enabled": true,
  "stream_url": "/api/v1/cameras/cam_01/stream",
  "fps": 24.5,
  "message": "Cámara iniciada correctamente."
}
```

### Detener Cámara
- **Método**: `POST`
- **Ruta**: `/api/v1/cameras/{id}/stop`
- **Headers**: `X-Api-Key: <api_key>`
- **Respuesta Exitosa (`200 OK`)**:
```json
{
  "camera_id": "cam_01",
  "name": "Acceso Principal",
  "status": "stopped",
  "running": false,
  "enabled": false,
  "stream_url": null,
  "fps": 0.0,
  "message": "Cámara detenida."
}
```

### Consultar Estado de Cámara
- **Método**: `GET`
- **Ruta**: `/api/v1/cameras/{id}`
- **Headers**: `X-Api-Key: <api_key>`
- **Respuesta Exitosa (`200 OK`)**:
```json
{
  "id": "cam_01",
  "name": "Acceso Principal",
  "source_type": "RTSP",
  "source": "rtsp://192.168.1.100:554/stream1",
  "enabled": true,
  "running": true,
  "fps": 24.5,
  "resolution": "1920x1080",
  "active_analytics_count": 1,
  "stream_url": "/api/v1/cameras/cam_01/stream"
}
```

---

## 3. Comportamiento Garantizado en React y Edge

1. **Al pulsar "Detener" en React**:
   - React envía `POST /api/v1/cameras/{id}/stop`.
   - VisionControl Edge detiene el hardware de captura, suspende inferencia y cierra el stream MJPEG.
   - La UI de WPF cambia inmediatamente a estado `Detenida` y el botón muestra `Volver a activar`.
   - Si había un navegador o elemento `<img src="/api/v1/cameras/{id}/stream" />` en React, la conexión HTTP finaliza limpiamente.

2. **Al pulsar "Volver a activar" en React**:
   - React envía `POST /api/v1/cameras/{id}/start`.
   - Edge inicializa el pipeline, captura el primer fotograma y lo entrega de inmediato al suscriptor.
   - El endpoint `/api/v1/cameras/{id}/stream` comienza a emitir fotogramas en vivo a los clientes conectados sin congelamiento.
   - La UI de WPF cambia inmediatamente a estado `En línea` y el botón muestra `Detener`.
