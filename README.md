# HandRaiseDetection

## Qué es
Nodo de analítica de video en el borde para Windows 11 x64 y nicho inicial de logística.
Procesa archivo, webcam o RTSP, detecta personas y emite eventos de mano levantada con zona y snapshot.
Puede operar como escritorio WPF o como Host headless administrado por REST; no graba video continuo y no es un NVR.
CPU es el fallback; GPU NVIDIA/AMD/Intel se usa mediante DirectML cuando está disponible.
Producto de referencia actual: detección de manos con YOLO26 Pose ONNX, ByteTrack, OpenCvSharp y persistencia SQLite.
El código de producción es C#/.NET 10; `app/`, `tests/`, `config.yaml` y scripts Python son prototipo/referencia.

## Estado y roadmap

Pruebas actuales: **255** (51 Domain, 111 Application, 33 Infrastructure.Windows, 60 Host), todas superadas en Release.

| Bloque | Estado |
|---|---|
| C1–C9, D1–D2 | Base funcional de video, inferencia ONNX, tracking, persistencia SQLite, host headless y WPF. |
| E0–E1.5 | Evolución camera-centric, fundación genérica multi-analítica y freeze de contrato v1. |
| E2–E5 | Catálogo real (5 analíticas: `hand_raise`, `person_presence`, `zone_intrusion`, `line_crossing`, `person_counting`), líneas virtuales y coexistencia por cámara. |
| D2b pendiente | WebSocket de eventos/métricas y MJPEG; aceptar con reconexión y clientes simultáneos sin bloquear inferencia. |
| D3 pendiente | Manifest/modelos v2 e importación/activar/revertir; aceptar cambio seguro y validación de hash/licencia. |
| D4 pendiente | Módulos PackageCounting/ConveyorFlow; aceptar conteos deterministas con secuencias sintéticas. |
| D5 pendiente | Webhook, MQTT y clips pre/post evento; aceptar reintentos acotados sin bloquear pipelines. |
| D6 pendiente | ONVIF, plantillas y capacidad calibrada; aceptar descubrimiento/configuración y estimación contrastada. |
| D7 pendiente | Herramienta Python de dataset/entrenamiento/exportación; aceptar paquete ONNX reproducible validado. |
| D8 pendiente | Servicio instalable; aceptar instalar/iniciar/actualizar/desinstalar en Windows limpio. |
| D9 pendiente | Hub de flota por conexión saliente; aceptar registro seguro y recepción multi-nodo. |

## Arquitectura

| Proyecto/ruta | Responsabilidad |
|---|---|
| `src/HandRaise.Domain` | Reglas puras de manos, máquina de estados, geometría de zonas y entidades analíticas. |
| `src/HandRaise.Application` | Contratos, catálogo/instancias analíticas, inferencia abstracta, tracking, pipelines y eventos. |
| `src/HandRaise.Infrastructure.Windows` | OpenCV, Windows ML/ONNX, DXGI, SQLite, settings, almacén JSON de analíticas y logs. |
| `src/HandRaise.Desktop` | Aplicación WPF/MVVM y editor de zonas en proceso. |
| `src/HandRaise.Host` | Generic Host/Kestrel, composición headless, gestión de analíticas y API REST. |
| `src/HandRaise.DebugApp` | Consola de diagnóstico, grabación y benchmark. |
| `src/HandRaise.DeviceProbe` | Inventario de hardware/runtimes. |
| `src/HandRaise.InferenceProbe` | Comparación de inferencia CPU/DirectML. |
| `tests-csharp/*` | xUnit para Domain, Application, Infrastructure.Windows y Host/TestServer. |

Dependencias: `Domain <- Application <- Infrastructure.Windows`; Desktop, Host y utilidades componen esas capas. Domain no depende de OpenCV, ONNX, WPF, SQLite ni ASP.NET.

## Contratos

Auth: lecturas requieren API key si está configurada; sin clave solo aceptan localhost. Toda escritura exige `X-Api-Key` configurada.

| Método | Ruta `/api/v1` | Auth | Descripción |
|---|---|---|---|
| GET | `/health`, `/metrics` | lectura | Salud/identidad y métricas. |
| GET | `/devices`, `/system/runtime`, `/system/capacity` | lectura | Hardware, runtimes y capacidad estimada. |
| GET | `/analytics/catalog` | lectura | Catálogo de analíticas disponibles en el nodo. |
| GET | `/cameras`, `/cameras/{id}` | lectura | Cámaras persistentes y estado. |
| POST | `/cameras` | escritura | Crear cámara. |
| PUT/DELETE | `/cameras/{id}` | escritura | Modificar/eliminar cámara. |
| GET/POST | `/cameras/{id}/analytics` | lectura/escritura | Listar e instalar instancias analíticas en la cámara. |
| GET/PUT/DELETE | `/cameras/{id}/analytics/{instanceId}` | lectura/escritura | Consultar, actualizar configuración o eliminar analítica. |
| GET/POST/PUT | `/cameras/{id}/lines` | lectura/escritura | Listar, crear o actualizar líneas virtuales por cámara. |
| DELETE | `/cameras/{id}/lines/{lineId}` | escritura | Eliminar línea virtual. |
| POST | `/cameras/{id}/start`, `/cameras/{id}/stop` | escritura | Habilitar e iniciar/detener. |
| POST | `/cameras/test` | escritura | Probar fuente con timeout. |
| GET | `/cameras/{id}/snapshot` | lectura | Último frame JPEG. |
| GET/PUT | `/cameras/{id}/zones` | lectura/escritura | Zonas normalizadas y aplicación en caliente. |
| PUT | `/system/device` | escritura | Cambio seguro del backend activo. |
| GET | `/events` | lectura | Historial filtrado/paginado. |
| GET | `/events/{id}/snapshot` | lectura | Snapshot del evento. |
| GET | `/stats` | lectura | Agregados por cámara, zona y hora. |

Evento JSON: `{id,type:"hand_raised|hand_lowered",camera_id,track_id,hand:"left|right|both",zone,confidence,timestamp,snapshot_url,node_id,site_id}`.

Configuración principal: `src/HandRaise.Host/appsettings.json` (`nodeId`, `siteId`, `api`, `capacity`, `model`, `capture`, `hands`, `tracker`, `storage`, `cameras`) y equivalentes `snake_case` en Desktop/DebugApp. Preferencia de dispositivo: `%LOCALAPPDATA%\HandRaiseDetection\settings.json`.

| Datos | Escritorio | Host/servicio |
|---|---|---|
| DB/snapshots | `%LOCALAPPDATA%\HandRaiseDetection\events.db` y `snapshots\` | Igual, bajo el perfil de la cuenta que ejecuta el proceso. |
| Zonas / Líneas | `zones.json` | `cameras.json` (zonas), `lines.json` (líneas virtuales). |
| Cámaras/credenciales | `node-credentials.json` (DPAPI máquina) y config WPF | `cameras.json`, `camera-credentials.json` y `node-credentials.json` (DPAPI máquina). |
| Logs | `%LOCALAPPDATA%\HandRaiseDetection\logs\` | Igual, bajo el perfil de la cuenta de servicio. |

## Comandos (`cmd.exe`)

```bat
set DOTNET_CLI_HOME=%CD%\.dotnet_cli
set DOTNET_CLI_TELEMETRY_OPTOUT=1
"C:\Program Files\dotnet\dotnet.exe" restore HandRaiseDetection.slnx --configfile NuGet.Config
"C:\Program Files\dotnet\dotnet.exe" build HandRaiseDetection.slnx -c Release --no-restore
"C:\Program Files\dotnet\dotnet.exe" test HandRaiseDetection.slnx -c Release --no-restore
"C:\Program Files\dotnet\dotnet.exe" run --project src\HandRaise.Host\HandRaise.Host.csproj -c Release --no-restore
"C:\Program Files\dotnet\dotnet.exe" run --project src\HandRaise.Desktop\HandRaise.Desktop.csproj -c Release --no-restore
"C:\Program Files\dotnet\dotnet.exe" run --project src\HandRaise.DebugApp\HandRaise.DebugApp.csproj -c Release --no-restore -- --source 0
publish.cmd
```

## Validación

| Validado por el dueño | Sin validar |
|---|---|
| Detección de manos con webcam; cambio de dispositivo con `d`; eventos con persona real; portable en otra PC; Host responde `health` y `capacity`. | RTSP real; Windows Service; GPU/Windows ML y DPAPI bajo cuenta de servicio; varias cámaras reales; carga sostenida; WebSocket/MJPEG y todo D2b–D9. |

## Riesgos y decisiones abiertas

- Antes de entregar a terceros resolver YOLO26 Pose/Ultralytics AGPL-3.0 y revisar FFmpeg LGPL y avisos restantes.
- En modo servicio las webcams pueden no estar disponibles; priorizar RTSP/archivo.
- Confirmar cuenta del servicio y ubicación/ACL definitiva de datos, logs y secretos.
- Calibrar la fórmula de capacidad con varias cámaras, resoluciones, FPS objetivo y hardware reales.

## Plantilla de prompt de bloque

`Lee README.md y AGENTS.md. Implementa SOLO X. Alcance: [...]. Fuera de alcance: [...]. README: actualiza 3 líneas.`
