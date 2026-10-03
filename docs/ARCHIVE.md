# Archivo histórico. No leer salvo que una tarea lo requiera.

# Hand Raise Detection — aplicación Windows en C#

> Este es el único documento vivo del proyecto. Contiene arquitectura, requisitos, decisiones y avance. Debe actualizarse al terminar cada bloque.

## Estado de esta decisión

Este documento reemplaza a Python como dirección principal del producto. El código Python existente queda únicamente como prototipo ejecutable, referencia de comportamiento y fuente de casos de prueba. No se continuará agregando funcionalidad de producción en Python salvo que sea necesaria para exportar o validar un modelo.

La meta es entregar una aplicación de escritorio para Windows que:

- se ejecute sin instalar Python;
- detecte CPU y adaptadores gráficos disponibles;
- permita seleccionar el dispositivo de inferencia;
- procese archivo, webcam o RTSP;
- detecte personas con la mano levantada;
- exponga la API local existente para una UI o integraciones;
- pueda publicarse como aplicación autocontenida y ejecutarse en el escritorio personal.

## Decisiones principales

### Plataforma

- Lenguaje: C#.
- Runtime: .NET 10 LTS.
- Sistema recomendado para la primera entrega: Windows 11 24H2 x64.
- UI futura: WPF sobre .NET. Se prioriza madurez y despliegue simple.
- API local: ASP.NET Core/Kestrel dentro del mismo proceso.
- Inferencia de producción: ONNX Runtime mediante Windows ML autocontenido (`Microsoft.Windows.AI.MachineLearning` 2.3.42).
- Modelo inicial: `yolo26n-pose.onnx`; `yolo26s-pose.onnx` queda como alternativa configurable para comparar precisión.
- Aceleración inicial: DirectML para GPU NVIDIA, AMD e Intel; CPU como fallback obligatorio.
- Persistencia: SQLite mediante Entity Framework Core.
- Captura y dibujo: OpenCvSharp.
- Configuración general: `appsettings.json` más un archivo de usuario en `%LOCALAPPDATA%\HandRaiseDetection\settings.json`.

.NET 10 es LTS y permanece soportado hasta noviembre de 2028. Windows ML permite enumerar dispositivos de ejecución y seleccionar explícitamente el proveedor y hardware. Referencias:

- https://dotnet.microsoft.com/platform/support/policy
- https://learn.microsoft.com/windows/ai/new-windows-ml/select-execution-providers
- https://learn.microsoft.com/windows/ai/new-windows-ml/distributing-your-app

### Qué significa “un ejecutable”

La aplicación tendrá un `.exe` principal y no requerirá instalar .NET ni Python cuando se publique como `self-contained`.

No se promete que todos los componentes estén físicamente dentro de un único archivo. ONNX Runtime, DirectML, OpenCV y el modelo ONNX contienen archivos nativos que pueden necesitar acompañar al ejecutable o extraerse al iniciar. Las opciones de entrega serán:

1. Primera entrega: carpeta autocontenida con un `.exe`, DLL nativas y modelo.
2. Entrega local opcional: paquete MSIX o instalador sencillo con acceso directo y desinstalación.
3. `PublishSingleFile` solo si las pruebas confirman que los componentes nativos funcionan correctamente en todos los equipos objetivo.

## Alcance personal y licencia

Esta aplicación es para uso personal y local en el escritorio del desarrollador. No es un producto comercial, un servicio para clientes ni una aplicación que se distribuirá a terceros.

Con este alcance se puede continuar usando Ultralytics Pose bajo su opción AGPL-3.0. La migración a C# utilizará YOLO26 Pose exportado a ONNX y no requiere sustituirlo por RTMPose.

YOLO26 es la versión publicada más reciente con modelos Pose. YOLO27 continúa en vista previa y todavía no tiene pesos disponibles, por lo que no se utilizará hasta que exista una versión publicada y validada.

Si en el futuro la aplicación se vende, se integra en un producto cerrado o se distribuye a terceros, se revisará nuevamente la licencia antes de publicar esa versión. Ese escenario futuro no bloquea el desarrollo personal actual.

Referencia de licencia de Ultralytics:

- https://www.ultralytics.com/license

La licencia y el origen del modelo se conservarán en el repositorio como información técnica.

## Arquitectura propuesta

```text
HandRaiseDetection.sln
  src/
    HandRaise.Domain/
      Detection/          # reglas de mano levantada y estados
      Events/             # contratos de eventos
      Zones/              # geometría pura
    HandRaise.Application/
      Pipelines/          # orquestación de captura, inferencia y tracking
      Services/           # eventos, snapshots, estadísticas
    HandRaise.Infrastructure.Windows/
      Capture/            # OpenCvSharp, archivo/webcam/RTSP
      Hardware/           # Windows ML + inventario DXGI
      Inference/          # sesiones ONNX y postproceso de pose
      Tracking/           # ByteTrack o implementación aprobada
      Storage/            # EF Core + SQLite
      Settings/           # configuración global y de usuario
    HandRaise.Api/
      Endpoints/          # REST, WebSocket y MJPEG
    HandRaise.Desktop/
      Views/              # WPF, se implementa después del backend
      ViewModels/
    HandRaise.Host/
      Program.cs          # composición, logging y ciclo de vida
  tests/
    HandRaise.Domain.Tests/
    HandRaise.Application.Tests/
    HandRaise.Infrastructure.Tests/
    HandRaise.Api.Tests/
  models/
    pose.onnx
    model.manifest.json
```

Las dependencias apuntan hacia adentro: infraestructura y API pueden depender de Application/Domain; Domain no conoce OpenCV, ONNX, WPF, SQLite ni ASP.NET Core.

## Contratos centrales

### Hardware

```csharp
public sealed record DeviceInfo(
    string Id,
    string Name,
    HardwareVendor Vendor,
    InferenceBackend Backend,
    ulong? VramBytes,
    string? DriverVersion,
    bool RuntimeAvailable,
    string? UnavailableReason);
```

Requisitos:

- enumerar los `EpDevice` de Windows ML/ONNX Runtime;
- complementar nombre, VRAM, LUID, vendor ID y device ID mediante DXGI cuando sea necesario;
- construir un ID estable a partir del proveedor, LUID o identificador Plug and Play, nunca solo del índice;
- incluir siempre CPU;
- mostrar dispositivos cuyo runtime falte, pero marcarlos como no utilizables;
- recomendar: proveedor NVIDIA específico aprobado, GPU DirectML, CPU;
- si el dispositivo guardado desaparece, seleccionar CPU, persistir el fallback y registrar una advertencia;
- no descargar providers o runtimes silenciosamente. Cualquier instalación debe ser una acción explícita y auditable.

### Inferencia

```csharp
public interface IInferenceBackend : IAsyncDisposable
{
    DeviceInfo Device { get; }
    InferenceExecutionInfo ExecutionInfo { get; }
    ValueTask LoadAsync(ModelDescriptor model, CancellationToken cancellationToken);
    ValueTask<PoseBatch> InferAsync(ImageFrame frame, CancellationToken cancellationToken);
}
```

Implementaciones iniciales:

- `WindowsMlDirectMlBackend` para el adaptador elegido;
- `WindowsMlCpuBackend` como fallback;
- proveedores específicos NVIDIA/AMD/Intel quedan como optimización posterior, sujetos a compatibilidad.

El cambio en caliente debe crear y validar la nueva sesión primero, detener brevemente nuevas inferencias, intercambiar la sesión de forma atómica y liberar la anterior. Si falla, se conserva el backend activo y se devuelve un error explicativo.

### Captura

- Una tarea por cámara.
- `Channel<VideoFrame>` acotado a capacidad uno para RTSP/webcam: siempre conservar el frame más reciente.
- En archivos, no descartar frames y respetar el FPS original.
- Reconexión RTSP con backoff cancelable.
- Uso obligatorio de `CancellationToken` y `IAsyncDisposable`.
- Liberar cada `Mat`, tensor y buffer nativo de forma determinista.
- Separar captura, inferencia y render para que una ventana lenta no bloquee la cámara.

### Reglas de negocio

La lógica Python de `rules.py` y `zones.py` se portará de forma literal a Domain:

- keypoints COCO 0, 1, 2, 5, 6, 9 y 10;
- margen de hombro proporcional o en píxeles;
- modo estricto por ojos/nariz;
- confianza mínima configurable;
- estados `Idle -> Raising -> Raised -> Lowering -> Idle`;
- estabilización por frames o milisegundos;
- cooldown y limpieza de tracks;
- mano izquierda, derecha o ambas;
- polígonos con borde incluido;
- ninguna dependencia de ONNX u OpenCV en estas reglas.

Los casos de pytest actuales deben convertirse a xUnit y conservar los mismos datos sintéticos.

### Tracking

El tracking debe permanecer separado del modelo de pose. Se requiere ByteTrack o una implementación compatible que produzca IDs estables.

Antes de incorporar un port de terceros se debe verificar su licencia. Si se porta el algoritmo internamente, se documentará el origen y se crearán pruebas de asociación, pérdida temporal y recuperación de tracks.

### Persistencia y eventos

- EF Core con SQLite.
- Migraciones versionadas.
- Repositorios fuera de los endpoints.
- Snapshots JPEG en disco con ruta relativa en base de datos.
- Bus interno con `Channel<HandEvent>`.
- WebSocket para eventos en vivo.
- Timestamps UTC mediante `DateTimeOffset`.

## Contrato API que debe conservarse

- `GET /health`
- `GET /cameras`
- `GET /cameras/{id}/stream`
- `WS /ws/events`
- `GET /events`
- `GET /events/{id}/snapshot`
- `GET /stats`
- `GET/PUT /config/zones/{camera_id}`
- `GET /system/devices`
- `PUT /system/device` con `{ "device_id": "..." }`
- `GET /system/runtime`

La UI WPF puede consumir servicios en proceso, pero la API debe seguir disponible para Angular, Next.js u otras integraciones.

## Modelo y artefactos

El equipo donde se ejecute la aplicación no debe necesitar Python. La conversión a ONNX se realiza una vez durante desarrollo.

Cada modelo distribuido tendrá un `model.manifest.json` con:

- nombre y versión;
- URL/origen verificable;
- licencia;
- hash SHA-256;
- tamaño de entrada;
- nombres y formas de entradas/salidas;
- orden de keypoints;
- normalización y letterbox;
- umbrales recomendados;
- opset ONNX;
- fecha y herramienta de exportación.

El postproceso C# debe validarse contra resultados de referencia antes de aceptar un modelo nuevo.

## Configuración

`appsettings.json` contendrá valores administrables del producto:

- cámaras y zonas;
- modelo e `image_size`;
- confianza, IoU y margen de mano;
- frames/milisegundos de estabilización;
- cooldown;
- reconexión RTSP;
- CORS, host y puerto local;
- rutas de SQLite y snapshots.

El archivo de usuario contendrá únicamente preferencias de esa máquina:

- ID del dispositivo elegido;
- backend;
- cámara seleccionada en la UI;
- preferencias visuales futuras.

Las credenciales RTSP se guardarán con Windows Credential Manager o DPAPI, no en texto plano dentro de `appsettings.json`.

## Logging y diagnóstico

- `Microsoft.Extensions.Logging` como abstracción.
- Serilog es opcional para archivos rotativos, sujeto a aprobación de dependencia.
- Nunca registrar contraseñas RTSP ni API keys.
- Registrar dispositivo, provider, modelo/hash, FPS, latencia y reconexiones.
- Crear una pantalla o archivo de diagnóstico exportable en una fase posterior.

## Pruebas obligatorias

### Unitarias

- reglas de mano levantada y máquina de estados;
- zonas convexas/cóncavas y bordes;
- recomendación de dispositivo;
- dispositivo guardado ausente;
- parsing y postproceso ONNX;
- asociación de tracking;
- configuración inválida.

### Integración

- inferencia CPU y DirectML sobre la misma imagen dentro de tolerancia;
- cambio de dispositivo sin reiniciar proceso;
- cero personas sin excepción;
- video local hasta EOF;
- cancelación y cierre sin tareas colgadas;
- pérdida y recuperación simulada de RTSP;
- SQLite y snapshots;
- endpoints y WebSocket.

### Equipo real

- PC solo CPU;
- Intel integrada;
- AMD integrada/dedicada;
- NVIDIA dedicada;
- portátil con dos GPU;
- GPU deshabilitada o driver roto;
- desconexión de cámara y cierre forzado de la aplicación.

## Publicación

C9 publica `HandRaise.Desktop` como carpeta portable, self-contained, Release/win-x64 mediante `publish.cmd`; incluye nativos, configuración, modelo/manifiesto, datos iniciales, avisos y guía. Se mantiene `PublishSingleFile=false` porque ONNX Runtime, DirectML, OpenCV y FFmpeg requieren artefactos nativos acompañantes. Al iniciar se valida el SHA-256 del modelo; los errores globales se rotan en `%LOCALAPPDATA%\HandRaiseDetection\logs` sin credenciales RTSP.

```bat
set DOTNET_CLI_HOME=%CD%\.dotnet_cli&& set DOTNET_CLI_TELEMETRY_OPTOUT=1&& "C:\Program Files\dotnet\dotnet.exe" test HandRaiseDetection.slnx --configuration Release --no-restore
publish.cmd
start "" dist\HandRaise\HandRaise.Desktop.exe
```

## Desarrollo y pruebas actuales

Requisitos instalados:

- SDK .NET 10 LTS x64.
- Windows x64.

Desde `cmd.exe`:

```bat
set DOTNET_CLI_HOME=%CD%\.dotnet_cli
set DOTNET_CLI_TELEMETRY_OPTOUT=1
"C:\Program Files\dotnet\dotnet.exe" restore HandRaiseDetection.slnx --configfile NuGet.Config
"C:\Program Files\dotnet\dotnet.exe" test HandRaiseDetection.slnx --configuration Release --no-restore
```

La solución actual contiene los proyectos de dominio, aplicación, infraestructura Windows, sondas de hardware/inferencia y sus pruebas. El dominio continúa sin depender de cámara, ONNX, GPU, API ni interfaz gráfica.

Para inspeccionar el hardware real sin abrir una interfaz:

```bat
"C:\Program Files\dotnet\dotnet.exe" run --project src/HandRaise.DeviceProbe/HandRaise.DeviceProbe.csproj --configuration Release --no-restore
```

La sonda imprime JSON y guarda la preferencia en `%LOCALAPPDATA%\HandRaiseDetection\settings.json`. Para probar sin modificar esa preferencia se puede indicar otra ruta:

```bat
"C:\Program Files\dotnet\dotnet.exe" run --project src/HandRaise.DeviceProbe/HandRaise.DeviceProbe.csproj --configuration Release --no-restore -- --settings .local-test\settings.json
```

## Implementación actual de hardware

El bloque C2 ya implementa:

- contrato `DeviceInfo` con ID estable, nombre, fabricante, backend, índice, VRAM, driver y disponibilidad del runtime;
- CPU incluida siempre como fallback;
- enumeración de adaptadores físicos DXGI mediante `Vortice.DXGI` 3.8.3, omitiendo adaptadores de software;
- IDs DirectML construidos con vendor ID, device ID y LUID del adaptador;
- consulta opcional a `nvidia-smi`, con timeout, para UUID, índice CUDA, VRAM y versión del driver;
- ausencia o fallo de `nvidia-smi` convertido en advertencia, nunca en caída de la aplicación;
- inventario de componentes ONNX Runtime, DirectML y CUDA presentes junto al ejecutable;
- recomendación determinista: NVIDIA CUDA disponible, GPU DirectML disponible y CPU;
- preferencia JSON configurable y escritura atómica;
- fallback a CPU y actualización del archivo cuando el dispositivo guardado desaparece o deja de estar disponible;
- utilidad de consola `HandRaise.DeviceProbe` para validar una máquina real.

La enumeración real en el equipo de desarrollo encontró una NVIDIA GeForce RTX 2050 y una AMD Radeon integrada mediante DXGI, además del destino CUDA de NVIDIA y CPU. Desde C3, Windows ML 2.3.42 aporta ONNX Runtime y DirectML de forma autocontenida: CPU, NVIDIA DirectML y AMD DirectML se cargaron y ejecutaron correctamente. CUDA continúa inventariado como capacidad de hardware, pero no es un provider activo en el backend actual.

`Vortice.DXGI` usa licencia MIT. No se agregó software de pago ni telemetría del proyecto.

## Implementación actual de inferencia

El bloque C3 ya implementa:

- Windows ML 2.3.42 autocontenido, que incorpora ONNX Runtime y DirectML sin requerir una instalación separada en el PC final;
- interfaz `IInferenceBackend` independiente de ONNX, captura y UI;
- `WindowsMlOnnxBackend` con sesiones CPU y selección explícita del `EpDevice` DirectML correspondiente al vendor/device de DXGI;
- validación del contrato de entrada ONNX antes de aceptar una sesión;
- preprocesamiento letterbox con interpolación bilineal, padding configurable, BGR a RGB y tensor NCHW normalizado;
- postproceso para la salida clásica `[1,56,8400]` de YOLO26 Pose y para la variante end-to-end `[1,N,57]`;
- restauración de cajas/keypoints a las coordenadas originales, filtro de confianza, NMS e imposición del máximo de detecciones;
- cierre determinista de sesiones, tensores, resultados y opciones nativas;
- serialización de inferencia por backend para evitar acceso concurrente inseguro al cambiar de fase;
- sonda `HandRaise.InferenceProbe` para ejecutar el mismo frame en CPU/DirectML y verificar tolerancia numérica;
- manifiesto versionado del modelo en `models/model.manifest.json` con origen, licencia, hash, entrada, salida y keypoints.

El modelo local se genera durante desarrollo y no se confirma en Git:

```powershell
& .venv-export\Scripts\python.exe scripts\export_onnx.py `
  --model yolo26n-pose.pt `
  --imgsz 640 `
  --output models\yolo26n-pose.onnx
```

Contrato comprobado del modelo oficial exportado:

- entrada `images`: `[1,3,640,640]`, RGB/NCHW;
- salida `output0`: `[1,56,8400]`, cajas `xywh`, una clase y 17 keypoints `(x,y,confidence)`;
- opset 18;
- SHA-256 `c56815ab17dd8ed15f507f12800650c28c97e46ab9b33b23fc2c57a57f9035b1`;
- licencia declarada por el artefacto: AGPL-3.0.

Validación real realizada con la imagen oficial `bus.jpg` de Ultralytics:

- CPU: 4 personas;
- NVIDIA RTX 2050 mediante DirectML: 4 personas;
- diferencia máxima de keypoints: `0.0001220703125` píxeles con tolerancia configurada de 3 píxeles;
- resultado: equivalencia CPU/DirectML aceptada.

Comando de comparación para un frame BGR crudo:

```powershell
& "C:\Program Files\dotnet\dotnet.exe" run `
  --project src/HandRaise.InferenceProbe/HandRaise.InferenceProbe.csproj `
  --configuration Release `
  --no-restore -- `
  --model models/yolo26n-pose.onnx `
  --bgr .local-test/bus.bgr `
  --width 810 `
  --height 1080 `
  --confidence 0.25 `
  --iou 0.7 `
  --tolerance 3
```

La sonda de C3 recibe BGR crudo para mantener aislada la validación del backend. Desde C4, la aplicación de depuración sí lee archivos, webcam y RTSP mediante OpenCvSharp.

La diferencia de `0.0001220703125` píxeles entre CPU y DirectML es esperable. Ambos ejecutan el mismo grafo ONNX en `float32`; el orden de las operaciones y los kernels optimizados del provider pueden producir variaciones mínimas de redondeo. El valor está varios órdenes de magnitud por debajo de la tolerancia funcional de 3 píxeles y no indica una diferencia de pose observable.

## Implementación actual de captura, tracking y eventos

El bloque C4 implementa, sin tracking, eventos, persistencia, API ni WPF:

- `IVideoSource` y fuentes OpenCvSharp separadas para archivo, webcam y RTSP;
- archivo temporizado con su FPS original, sin descarte, EOF limpio y `loop` configurable;
- webcam/RTSP con canal acotado a un frame, política `DropOldest`, conteo de descartes y reconexión con backoff cancelable;
- timestamp de archivo calculado desde el índice global y timestamp vivo mediante `Stopwatch`, siempre monotónicos;
- `CameraPipeline` independiente por cámara: captura, inferencia compartida serializada, evaluación pura de manos, zona y overlay;
- métricas de edad de frame, preproceso, inferencia, postproceso, overlay, total, FPS y descartes;
- overlay reutilizable con esqueleto COCO, cajas, score, zonas, `MANO I/D/AMBAS`, FPS, latencia, provider, dispositivo y modelo;
- `HandRaise.DebugApp` con ventana, salida por `q`, cambio de dispositivo por `d`, grabación anotada, grabación cruda, JSON de métricas y benchmark;
- intercambio atómico del backend: la nueva sesión se carga antes del cambio y un fallo conserva la sesión activa;
- verificación de ejecución con nombre de provider y, para DirectML, vendor/device del `EpDevice` asignado explícitamente.

Propiedad de memoria: cada `VideoFrame` es dueño exclusivo de su arreglo BGR y quien lo recibe debe liberarlo con `Dispose`. OpenCvSharp conserva la propiedad de cada `Mat` temporal de captura, dibujo, ventana o escritura y lo libera en el mismo alcance con `using`. La captura copia fuera del `Mat` antes de entregarlo; el overlay produce otro `VideoFrame` y no modifica el original. Las fuentes y destinos implementan `IAsyncDisposable` y aceptan `CancellationToken`.

La URL RTSP solo se entrega a OpenCV. Los nombres y mensajes de diagnóstico eliminan usuario y contraseña antes de registrar la dirección.

Dependencias nuevas de C4:

- `OpenCvSharp5` 5.0.0.20260905: Apache-2.0;
- `OpenCvSharp5.runtime.win` 5.0.0.20260905: Apache-2.0;
- OpenCV nativo incluido por el runtime: Apache-2.0;
- `opencv_videoio_ffmpeg500_64.dll`, cargado dinámicamente para video: FFmpeg bajo LGPL-2.1 o posterior según la documentación del paquete/runtime.

No se incorporaron componentes de pago ni telemetría. Referencias de licencia y selección de paquetes:

- https://github.com/shimat/opencvsharp/blob/main/docs/docfx/articles/getting-started/package-selection.md
- https://github.com/shimat/opencvsharp
- https://github.com/opencv/opencv-python/blob/4.x/LICENSE-3RD-PARTY.txt

### Uso de la aplicación de depuración

Desde `cmd.exe`:

```bat
set DOTNET_CLI_HOME=%CD%\.dotnet_cli
set DOTNET_CLI_TELEMETRY_OPTOUT=1
"C:\Program Files\dotnet\dotnet.exe" run --project src\HandRaise.DebugApp\HandRaise.DebugApp.csproj --configuration Release --no-restore -- --source 0 --device cpu
"C:\Program Files\dotnet\dotnet.exe" run --project src\HandRaise.DebugApp\HandRaise.DebugApp.csproj --configuration Release --no-restore -- --source video.mp4 --no-window --record salida.mp4 --record-raw .local-test\raw --metrics-json .local-test\video.json
"C:\Program Files\dotnet\dotnet.exe" run --project src\HandRaise.DebugApp\HandRaise.DebugApp.csproj --configuration Release --no-restore -- --source video.mp4 --no-window --benchmark --max-seconds 10 --metrics-json .local-test\benchmark.json
```

Para RTSP, `--source` acepta directamente `rtsp://...`. `--record-raw` recibe una carpeta y crea `session-raw.mp4`. En la ventana, `q` cierra y `d` recorre CPU y adaptadores DirectML disponibles sin reiniciar el proceso.

### Validación C4 en este equipo

La prueba de archivo procesó sus 30 frames hasta EOF a 9.91 FPS, descartó cero frames y produjo videos crudo y anotado. La webcam 0 abrió, procesó cuatro frames durante dos segundos y cerró sin dejar la tarea activa; al terminar la fuente reportó su pérdida e inició el camino cancelable de reconexión antes del cierre.

Benchmark de 20 frames por destino sobre el mismo archivo de 10 FPS:

- CPU / `CPUExecutionProvider`: inferencia media 56.51 ms, p95 62.77 ms; total medio 75.17 ms, p95 82.16 ms.
- NVIDIA GeForce RTX 2050 / `DmlExecutionProvider`: inferencia media 19.68 ms, p95 87.56 ms; total medio 36.36 ms, p95 103.21 ms; `EpDevice` confirmado con vendor `0x10DE`, device `0x25AD`.
- AMD Radeon Graphics / `DmlExecutionProvider`: inferencia media 21.40 ms, p95 26.37 ms; total medio 35.19 ms, p95 42.61 ms; `EpDevice` confirmado con vendor `0x1002`, device `0x1681`.

El FPS global queda limitado aproximadamente a 10 por la temporización original del archivo; para comparar hardware deben observarse las latencias por etapa. DirectML fue claramente diferente de CPU, por lo que esta ejecución no activó la advertencia de posible fallback silencioso. El p95 elevado de NVIDIA incluye el calentamiento inicial de la sesión en una muestra corta.

### C5: tracking, estados y eventos

C5 incorpora un ByteTrack propio, sin ports ni dependencias nuevas: filtro Kalman de velocidad constante para centro y tamaño de caja, asociación IoU determinista en dos etapas para detecciones de confianza alta/baja y buffer configurable para oclusiones. Cada `CameraPipeline` posee su propia instancia y espacio de IDs. Los umbrales, ruido del filtro, FPS nominal y buffer están en `appsettings.json`.

El pipeline asocia pose e ID, evalúa las manos y alimenta `RaiseHandTracker` con el timestamp monotónico del frame. Los tracks conservados por el buffer sobreviven oclusiones cortas; al expirar se eliminan mediante `Prune`. Las detecciones sin ID se dibujan, pero no generan eventos. El tracker y sus estados se reinician cuando la fuente viva incrementa su generación de conexión o cuando cambia exitosamente el backend compartido.

`HandEventBus` publica a canales independientes para poder añadir SQLite y WebSocket sin modificar el pipeline. La DebugApp incluye el suscriptor de consola y `--events-jsonl <archivo>` con `snapshot_url: null`. El ID del evento y su tiempo de reproducción son deterministas para que una misma grabación produzca exactamente la misma secuencia. El overlay muestra `ID`, `RAISING` y `RAISED`.

Pruebas C5: parpadeo, levantada sostenida, bajada, detección sin ID, dos personas, asociación de baja confianza, oclusión, expiración sin eventos fantasma, cámaras aisladas, reinicio por reconexión/backend, bus con múltiples suscriptores y reproducción determinista. La suite acumulada contiene 74 pruebas: 25 Domain, 33 Application y 16 Infrastructure, todas superadas en Release.

La grabación cruda disponible se reprodujo dos veces: 30 frames por ejecución y JSONL idéntico. Produjo cero eventos porque la escena estática no contiene una levantada detectable; falta validar una levantada y bajada reales con un video de aceptación que contenga esa acción.

### C6: persistencia y snapshots

Se corrigió la fecha de eventos: cada fuente crea un `FrameTimeAnchor` con UTC real y su timestamp monotónico inicial. Las reglas continúan usando exclusivamente el tiempo monotónico; `HandEvent.Timestamp` se calcula con el ancla y queda en UTC del año actual. El reloj es inyectable, por lo que una ancla fija conserva JSONL idéntico en pruebas. `VideoFrame` exige ahora ambos tiempos para impedir regresiones a 1970.

C6 incorpora EF Core/SQLite, migración inicial versionada y aplicación automática al arrancar `--persist` o `--list-events`. La fecha se convierte a ticks UTC (`INTEGER`) para ordenar y filtrar correctamente en SQLite. Existen índices `(camera_id, timestamp_utc_ticks)` y `type`, repositorio con filtros/paginación/obtención por ID y estadísticas por cámara, zona y hora UTC.

El suscriptor de persistencia copia los eventos del bus a un canal propio acotado y escribe lotes. Si se llena, descarta el evento más nuevo y registra advertencia; nunca espera el pipeline. Fallos de SQLite o disco se registran y deshabilitan o degradan la persistencia sin detener inferencia. El cierre completa el bus, vacía los elementos aceptados y ejecuta limpieza final.

En `hand_raised`, el pipeline copia únicamente el recorte de la persona con margen configurable, lo comprime como JPEG y libera el frame normalmente. El archivo se guarda bajo año/mes/día con nombre SHA-256; SQLite conserva solo la ruta relativa. La retención elimina eventos antiguos, limita el tamaño total de snapshots empezando por los más viejos y elimina JPEG huérfanos.

DebugApp añade `--persist` y `--list-events [--camera <id>] [--from <ISO-8601>] [--to <ISO-8601>]`. Rutas, cola, lote, margen, calidad JPEG, retención y frecuencia de limpieza están en `appsettings.json`.

Dependencias nuevas: `Microsoft.EntityFrameworkCore.Sqlite` 10.0.12 y componentes Microsoft transitivos bajo MIT; `SQLitePCLRaw` 2.1.12 bajo Apache-2.0; SQLite nativo es dominio público. No se añadió pago ni telemetría. Referencias: https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Sqlite/10.0.12 y https://www.sqlite.org/copyright.html.

Validación C6: migración sobre memoria, filtros, rangos, paginación, orden UTC, estadísticas, fechas actuales ordenadas, JSONL determinista con ancla fija, JPEG recortado, ruta relativa, fallo tolerado, saturación no bloqueante, vaciado al cerrar y retención por días/tamaño/huérfanos. La suite acumulada contiene 84 pruebas: 25 Domain, 33 Application y 26 Infrastructure. En el equipo real, la webcam abrió con `--persist`, creó/migró la base y un segundo proceso ejecutó `--list-events`; la escena no generó una levantada, por lo que no creó snapshots reales.

### C8: escritorio WPF

`HandRaise.Desktop` implementa una interfaz WPF en proceso con MVVM: mosaico `UniformGrid` para las cámaras de `appsettings.json`, video con el overlay existente, estado, FPS y control independiente de inicio/parada. La entrega de imagen a `WriteableBitmap` usa un canal de capacidad uno y descarta el frame visual anterior para no bloquear el hilo de UI.

El selector global muestra CPU y GPU detectadas, explica los dispositivos no disponibles y permite cambiar el backend en caliente conservando el activo si falla. La selección se persiste con el servicio existente. El panel recibe eventos en vivo desde `HandEventBus`, consulta el historial SQLite por cámara y fecha y muestra la miniatura JPEG de cada `hand_raised`. El cierre detiene cámaras, suscripciones, persistencia, sesiones y tareas en orden. Incluye tema claro/oscuro; API e instalador permanecen fuera de C8.

El editor por cámara permite crear, nombrar, cerrar, arrastrar, deshacer y borrar polígonos validados. Persiste coordenadas normalizadas en `%LOCALAPPDATA%\HandRaiseDetection\zones.json`, con prioridad sobre `appsettings.json`, y las aplica atómicamente al overlay y a los eventos sin reiniciar el pipeline. Las zonas antiguas en píxeles siguen admitidas y se normalizan al conocerse el tamaño del frame.

Dependencia nueva: `CommunityToolkit.Mvvm` 8.4.2, licencia MIT, sin pago ni telemetría del proyecto. Referencias: https://www.nuget.org/packages/CommunityToolkit.Mvvm/8.4.2 y https://github.com/CommunityToolkit/dotnet/blob/main/License.md.

Tres comandos para probar desde `cmd.exe`, en la raíz del repositorio:

```bat
set DOTNET_CLI_HOME=%CD%\.dotnet_cli&& set DOTNET_CLI_TELEMETRY_OPTOUT=1&& "C:\Program Files\dotnet\dotnet.exe" restore HandRaiseDetection.slnx --configfile NuGet.Config
"C:\Program Files\dotnet\dotnet.exe" test HandRaiseDetection.slnx --configuration Release --no-restore
"C:\Program Files\dotnet\dotnet.exe" run --project src\HandRaise.Desktop\HandRaise.Desktop.csproj --configuration Release --no-restore
```

Antes de entregar:

- comprobar hashes y conservar el listado de dependencias y licencias;
- probar en una máquina limpia sin SDK ni Python;
- verificar que el modelo y DLL nativas estén incluidos;
- validar inicio, actualización, reparación y desinstalación;
- decidir si realmente se necesita MSIX/MSI o si basta una carpeta portable.

## Plan de migración por bloques

### C1. Solución y dominio

- Crear solución .NET y proyectos.
- Portar configuración, `rules.py`, `zones.py` y tests a xUnit.
- Sin ONNX ni OpenCV todavía.

### C2. Hardware

- Completado: inventario DXGI y NVIDIA, diagnóstico de runtimes, recomendación, persistencia y fallback a CPU.
- Completado: pruebas sin depender de una GPU y validación adicional con adaptadores reales.
- La enumeración de `EpDevice` se conectará al incorporar ONNX Runtime/Windows ML en C3; el contrato ya está preparado.

### C3. ONNX

- Completado: carga validada del modelo, preproceso, inferencia y postproceso.
- Completado: ejecución CPU y DirectML sobre el mismo frame con personas reales.
- Completado: equivalencia numérica dentro de tolerancia y manifiesto con hash del modelo oficial exportado.

### C4. Captura y overlay

- Completado: archivo, webcam y RTSP con ciclo de vida cancelable.
- Completado: canal de último frame para fuentes vivas y pipeline independiente por cámara.
- Completado: esqueleto, cajas, score, zonas, estado instantáneo de manos y métricas. Los IDs corresponden a C5 porque requieren tracking.
- Completado: aplicación de consola/ventana, grabaciones, cambio de dispositivo y benchmark verificable.

### C5. Tracking y eventos

- ByteTrack.
- Máquina de estados por track.
- Eventos en consola y pruebas contra videos controlados.

### C6. Persistencia

- EF Core, SQLite, migraciones y snapshots.

### C7. API local

- ASP.NET Core, MJPEG, WebSocket, historial, estadísticas y hardware.

### C8. Escritorio WPF

- Completado: mosaico de cámaras configuradas, selección global de GPU, video y eventos en vivo/históricos.
- Completado: render no bloqueante, snapshots, temas claro/oscuro y cierre ordenado.
- Completado: editor de zonas normalizadas con persistencia local y aplicación en caliente; API e instalador siguen fuera de alcance.

### C9. Publicación local

- Completado: publicación portable autocontenida win-x64, verificación del modelo, logs globales, versión y avisos de terceros.
- Fuera de alcance: MSI/MSIX, API y ARM64; queda pendiente probar la carpeta en una máquina limpia.

## Criterios de aceptación del producto C#

- No requiere Python para ejecutar la aplicación publicada.
- Funciona en CPU si no existe una GPU compatible.
- Enumera todas las opciones disponibles y explica por qué alguna no puede usarse.
- Cambia de dispositivo sin reiniciar la aplicación.
- Una GPU retirada o un driver roto no impiden iniciar.
- Genera exactamente un `hand_raised` por levantada real bajo los videos de aceptación.
- Mantiene el contrato API documentado.
- Cierra cámaras, sesiones ONNX, tareas y archivos sin recursos colgados.
- Se ejecuta correctamente desde una carpeta portable o instalación local.
- Todas las dependencias y el modelo tienen su origen y licencia documentados.

## Decisiones pendientes para bloques posteriores

1. Confirmar si el mínimo será Windows 11 24H2 o si se debe soportar Windows 10.
2. Confirmar arquitectura inicial `win-x64`; ARM64 quedará para una compilación separada.
3. Obtener uno o más videos de aceptación y resultados esperados.

Se continuará con YOLO26 Pose exportado a ONNX para esta aplicación personal.

## Registro de avance

- Bloques Python 1–3.5: prototipo funcional y referencia de comportamiento.
- Migración C#: iniciada.
- Modelo base decidido: `yolo26n-pose.onnx`. YOLO27 no se usará mientras siga sin pesos publicados.
- C1 completado: solución .NET 10, dominio puro, evaluación de manos, estados, zonas y 25 pruebas xUnit.
- Validación C1: 25 superadas, 0 fallidas, 0 omitidas en .NET 10 Release.
- C2 completado: inventario DXGI, `nvidia-smi`, disponibilidad de runtimes, recomendación, preferencias JSON atómicas y fallback persistente a CPU.
- Validación acumulada C1+C2: 43 superadas, 0 fallidas, 0 omitidas en .NET 10 Release.
- Validación de equipo real: CPU, NVIDIA DirectML/CUDA y AMD DirectML enumerados sin excepciones; CPU y DirectML quedaron activos mediante Windows ML en C3.
- C3 completado: Windows ML/ONNX Runtime autocontenido, backend CPU/DirectML, letterbox, contratos YOLO26 Pose, NMS y sonda de equivalencia.
- Validación acumulada C1–C3: 51 superadas, 0 fallidas, 0 omitidas en .NET 10 Release.
- Validación C3 real: 4 personas en CPU y DirectML; diferencia máxima de keypoints `0.0001220703125` píxeles.
- C4 completado: fuentes de archivo/webcam/RTSP, canal acotado, pipeline por cámara, overlay OpenCvSharp, grabación, cambio seguro de backend y benchmark.
- Validación acumulada C1–C4: 61 superadas, 0 fallidas, 0 omitidas en .NET 10 Release.
- Validación C4 real: archivo de 30 frames hasta EOF sin descartes; webcam abierta/cancelada limpiamente; CPU, NVIDIA DirectML y AMD DirectML ejecutados y verificados.
- C5 completado: ByteTrack propio por cámara, Kalman, asociación IoU en dos etapas, buffer de pérdida, estados, bus de eventos, consola/JSONL y overlay con ID/fase.
- Validación acumulada C1–C5: 74 superadas, 0 fallidas, 0 omitidas en .NET 10 Release.
- Validación C5 reproducible: dos ejecuciones del mismo archivo crudo generaron JSONL idéntico; el clip disponible no contenía una levantada detectable.
- Corrección temporal completada: reglas monotónicas y eventos UTC reales mediante ancla/reloj inyectable; fechas actuales, orden y determinismo cubiertos.
- C6 completado: EF Core/SQLite, migración, repositorio, persistencia asíncrona, snapshots JPEG, retención y comandos de diagnóstico.
- Validación acumulada C1–C6: 84 superadas, 0 fallidas, 0 omitidas en .NET 10 Release.
- Validación C6 real: `--persist` creó/migró la base con webcam y `--list-events` funcionó tras reiniciar; no hubo evento real en esa escena.
- C8 completado: WPF/MVVM, mosaico, dispositivo en caliente, eventos, temas y editor de zonas normalizadas persistentes.
- Validación acumulada tras el editor: 91 superadas, 0 fallidas, 0 omitidas; compilación Release sin advertencias.
- C9 completado: `publish.cmd`, paquete portable, integridad SHA-256, errores rotativos, versión y avisos/licencias.
- Validación acumulada C9: 94 superadas, 0 fallidas, 0 omitidas; publicación portable verificada con 446 archivos, nativos y hash correcto.
- D1 completado: host consola/Windows Service, pipelines headless y API `/api/v1` de solo lectura con autenticación, CORS y OpenAPI.
- Validación D1: 99 superadas, 0 fallidas, 0 omitidas; 5 pruebas TestServer y compilación aislada sin advertencias.
- D2 completado: cámaras persistentes, credenciales DPAPI, CRUD/operación en caliente, prueba, snapshot, zonas y cambio de dispositivo por API.
- Validación D2: 103 superadas, 0 fallidas, 0 omitidas; compilación Release sin advertencias.

## Fase 2 — Producto para cliente
Objetivo: motor headless instalable en PC/servidor, administrado desde un front React (fuera de alcance) vía API; soporte de modelos entrenados a medida (p. ej. paquetes en banda).
1. Motor como servicio: HandRaise.Host headless (UseWindowsService, también consola) + API Kestrel en proceso. WPF = consola local opcional. A validar: Windows ML/DirectML bajo servicio; webcams no disponibles en servicio (solo RTSP/archivo); credenciales RTSP con cuenta de servicio.
2. API /api/v1 = contrato con el front: REST + WebSocket (eventos/métricas) + MJPEG; OpenAPI; API key, CORS configurable, HTTPS opcional. Recursos: health, metrics, devices, cameras (CRUD, test, snapshot), zones/lines, models, modules, events, stats, config, logs.
3. Métricas: FPS por cámara, latencia p50/p95 por etapa, descartes, reconexiones, cola, dispositivo/modelo activos, uptime, tamaño BD, eventos/hora.
4. Módulos de analítica: el motor entrega tracks; cada módulo emite eventos+métricas. HandRaise existe; nuevos: PackageCounting (cruce de línea y sentido), ConveyorFlow (throughput, parada/atasco). Config por cámara; JSON Schema expuesto por la API.
5. Modelos: manifest v2 (task pose|detect|segment, labels, input, umbrales, hash, licencia, métricas); registro versionado, importar/activar/revertir por API, cambio en caliente por cámara. Detector candidato: RF-DETR (Apache-2.0), pendiente de medir en ONNX con CPU/DirectML.
6. Entrenamiento: herramienta Python de desarrollo (training/, no se distribuye): captura de dataset, anotación (CVAT/Label Studio, verificar licencia), fine-tuning, export ONNX, validación, empaquetado.
7. Licencias: lo entregado al cliente no debe depender de AGPL sin resolver. YOLO (Ultralytics) = AGPL-3.0: licencia comercial o sustitución. La pose actual (YOLO26-pose) es la pieza pendiente.
Bloques: D1 host+API lectura · D2 gestión y tiempo real · D3 modelos v2 + detector · D4 módulos de conteo · D5 herramienta de entrenamiento · D6 servicio instalable.

### D1: host headless y API de lectura
`HandRaise.Host` usa Generic Host/`UseWindowsService`, también funciona como consola, inicia las cámaras habilitadas y expone Kestrel configurable. `/api/v1` incluye `health`, `metrics`, `devices`, `cameras`, `events`, snapshot, `stats`, `system/runtime` y `system/capacity`; Swagger UI solo aparece en Development. API key, restricción localhost sin clave, CORS y ProblemDetails se configuran en `appsettings.json`. El registro singleton recibe FPS, latencia, procesados, descartados y medias de decodificación/inferencia del pipeline. Fuera de alcance: WebSocket, MJPEG, CRUD, modelos y módulos.
`nodeId`, `siteId` y el FPS objetivo de capacidad se configuran en `appsettings.json`. La identidad se incluye en eventos JSON/SQLite, métricas y health; la migración `202610010001_NodeAndSite` conserva eventos anteriores como `unknown`. La capacidad estima cámaras máximas con `floor(1000 / (FPS objetivo × (decode medio + inferencia media)))` y devuelve `null` hasta disponer de muestras.
Dependencias D1: `Microsoft.Extensions.Hosting.WindowsServices`/`Microsoft.AspNetCore.TestHost` 10.0.12 y `Swashbuckle.AspNetCore` 10.2.3, licencia MIT; https://www.nuget.org/packages/Microsoft.Extensions.Hosting.WindowsServices/10.0.12 y https://www.nuget.org/packages/Swashbuckle.AspNetCore/10.2.3.
```bat
set DOTNET_CLI_HOME=%CD%\.dotnet_cli&& set DOTNET_CLI_TELEMETRY_OPTOUT=1&& "C:\Program Files\dotnet\dotnet.exe" test HandRaiseDetection.slnx --configuration Release --no-restore
"C:\Program Files\dotnet\dotnet.exe" run --project src\HandRaise.Host\HandRaise.Host.csproj --configuration Release --no-restore
curl http://127.0.0.1:5080/api/v1/health
```

### D2: gestión de cámaras por API
CRUD, habilitar/iniciar/detener, prueba de fuente, último frame JPEG, zonas normalizadas en caliente y cambio seguro de dispositivo están disponibles en `/api/v1`; las escrituras exigen API key. Cámaras en JSON atómico y credenciales RTSP separadas con DPAPI de máquina; nunca se devuelven ni registran. Sin dependencias nuevas. Validación acumulada: 103 pruebas superadas.
```bat
set DOTNET_CLI_HOME=%CD%\.dotnet_cli&& set DOTNET_CLI_TELEMETRY_OPTOUT=1
"C:\Program Files\dotnet\dotnet.exe" run --project src\HandRaise.Host\HandRaise.Host.csproj --configuration Release --no-restore
curl -H "X-Api-Key: CAMBIAR" http://127.0.0.1:5080/api/v1/cameras
```

8. Alcance: el Nodo es un software appliance (PC/servidor) que analiza cámaras IP estándar (RTSP/ONVIF) y emite eventos/métricas. NO es NVR: no graba video continuo. Analizar el substream de baja resolución si existe.
9. Flota: todo evento y métrica lleva node_id y site_id (appsettings). Futuro Hub central recibe de los nodos por conexión saliente (registro por token, sin puertos entrantes en el cliente).
10. Salidas: webhook, MQTT y WebSocket; clip corto pre/post evento además del snapshot. Plantillas de escenario (módulo+zonas+umbrales). Medidor de capacidad: ms de decodificación e inferencia medidos -> cámaras máximas por dispositivo a un FPS objetivo.
Licencias (actualizado): por ahora todo local; revisar licencias antes de la primera entrega a terceros (YOLO/AGPL, FFmpeg/LGPL, modelos).
Bloques: D1 host+API lectura · D2 gestión y tiempo real · D3 modelos v2 + detector · D4 módulos de conteo · D5 salidas de eventos + clips · D6 ONVIF + plantillas + capacidad · D7 entrenamiento · D8 servicio instalable · D9 Hub.
