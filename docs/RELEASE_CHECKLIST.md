# VisionControl Edge — Release Checklist (v0.9.0-rc.1)

## 1. Compilación y Pruebas Automatizadas
- [x] Compilación Release limpia sin errores ni advertencias (`dotnet build HandRaiseDetection.slnx -c Release --no-restore`).
- [x] Suite completa de tests automatizados xUnit superada (362/362 pruebas pasando).
- [x] Verificación de archivos fuente sin ficheros vacíos de 0 bytes.

## 2. Publicación y Empaquetado
- [x] Script `publish.cmd` genera `dist\HandRaise\VisionControl.Edge.exe`.
- [x] Inclusión de dependencias nativas (OpenCvSharpExtern, OnnxRuntime DirectML/CPU).
- [x] Modelos y manifiestos presentes en `dist\HandRaise\models\`.
- [x] Exclusión de código fuente, artefactos temporales y secretos en `dist\`.

## 3. Seguridad y Configuración
- [x] Protección de secretos con Machine DPAPI para `node-credentials.json`.
- [x] Regeneración de API Key en caliente sin reiniciar el Host.
- [x] Autenticación obligatoria vía header `X-Api-Key` o `?api_key=` para accesos no-localhost.
- [x] CORS configurado para orígenes de desarrollo local (`http://localhost:3000`, `http://127.0.0.1:3000`) y AI Studio.
- [x] Sanitización de credenciales RTSP, URLs y errores en logs y ProblemDetails.

## 4. Analíticas, Reglas y Notificaciones
- [x] Coexistencia de 5 analíticas (`hand_raise`, `person_presence`, `zone_intrusion`, `line_crossing`, `person_counting`).
- [x] Inferencia compartida y tracking unificado sin duplicación de modelos.
- [x] Reglas operacionales evaluadas asíncronamente en `RuleEngine`.
- [x] Despacho de notificaciones Webhook y MQTT aislado del loop crítico de inferencia.

## 5. Streaming y Comunicación en Tiempo Real
- [x] Servidor Kestrel embebido en `127.0.0.1:5080`.
- [x] Endpoints WebSocket para eventos `/api/v1/ws/events` y métricas `/api/v1/ws/metrics`.
- [x] Servidor MJPEG `/api/v1/cameras/{id}/stream` sin bloqueo de frames.

## 6. Validación Física y Hardware (Manual)
- [ ] Cámara USB física (iniciar, detener, desconectar, reconectar en vivo).
- [ ] Stream RTSP físico (verificación de conectividad y reconexión ante cortes).
- [ ] Multi-cámara concurrente en hardware real con aceleración GPU (DirectML).
- [ ] Soak test prolongado (30–60 min) en máquina destino.
