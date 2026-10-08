# Training Lab V1 — entrenamiento Desktop y detección personalizada

## Alcance

Detección de objetos de una o varias clases, JPG/PNG y muestreo de archivos de video. Proyectos y anotaciones persistentes, editor WPF de cajas, snapshots YOLO, jobs de fondo, worker externo, validación, ONNX, publicación versionada, prueba de imágenes y selección/ejecución custom por cámara. Sin endpoints nuevos ni cambios en React. No incluye segmentación, pose personalizada, clasificación, cloud, AutoML o entrenamiento distribuido. La captura de ejemplos desde cámara queda pendiente; el core admite frames suministrados por un adaptador futuro.

## Arquitectura encontrada

- Los proyectos reales usan .NET 10; Infrastructure/Desktop/Host apuntan a Windows 10.0.18362.0. Las menciones anteriores a .NET 8/9 son históricas.
- `StandardModelRegistry` implementa `IModelRegistry` con diccionarios de descriptores/proveedores. Antes de esta fase no persistía modelos.
- `Domain.Models.ModelDescriptor` representa capabilities, dimensiones, conteos de clases/keypoints, SHA-256 y ruta. El nombre en Application es un alias, no otro contrato.
- El artefacto actual es `models/yolo26n-pose.onnx`: su manifiesto declara Ultralytics 8.4.170, torch 2.14.1, opset 18 y salida raw `[1,56,8400]`. No se asumió YOLOv8.
- `WindowsMlOnnxBackend` utiliza ONNX Runtime de Windows ML 2.3.42, CPU/DirectML y pre/postprocesamiento propio. `YoloPosePostprocessor` admite clases variables y cero keypoints; no contiene nombres COCO de objetos hardcodeados.
- `HeadlessEngineService` conserva el backend builtin compartido desde `options.Model`. Además compone un `CustomObjectRuntime` por pipeline para los modelos seleccionados en analíticas custom. `CapabilityPlanner` separa esa selección explícita de la cobertura builtin; publicar un descriptor no registra proveedores ficticios.
- Las analíticas de personas, `HandEvaluator` y COCO-17 asumen personas/pose. No deben recibir automáticamente objetos personalizados.
- La persistencia combina JSON de configuración y SQLite de eventos. Se reutiliza JSON atómico, sin otra DB. El entrenamiento usa el runtime Python privado administrado bajo LocalAppData; `scripts/export_onnx.py` sigue siendo una herramienta de desarrollo.

## Capas y composición final

`Desktop → TrainingApplicationService → dataset snapshot → ITrainingWorker → Python/YOLO → validación ONNX → TrainingModelPublisher → IModelRegistry existente`.

- **Domain/Training**: records/enums puros, validación y transiciones; sin Process, Python o filesystem.
- **Application/Training**: interfaces, perfiles, split y `TrainingApplicationService`, que incluye el administrador simple de jobs con cola serial. No se agrega un scheduler separado.
- **Infrastructure.Windows/Training**: JSON, importación OpenCV, exporter, procesos, runtime configurado, validación, publicación y prueba de imágenes.
- **Host**: configura persistencia custom en su registro existente. No inicia TrainingCore ni expone endpoints nuevos.
- **Desktop**: `NodeHostController.GetTrainingAsync` compone una sola instancia al primer acceso con `TrainingCore.CreateAsync` y el MISMO registro del host embebido, persistente en `TrainingPaths.CustomModels`. Conserva registro/core durante Stop/Restart del Host y reinserta ese registro en DI al reconstruirlo. Navegar no dispone el core. El cierre espera operaciones de la UI y después la cancelación/disposición del core antes de detener el Host, incluso si falla el guardado de un borrador.

La factory adquiere `training/owner.lock` exclusivo entre procesos y recupera jobs sin iniciar Python. Debe existir una instancia por aplicación; el cierre espera `DisposeAsync`, que cancela/espera workers antes de liberar propiedad. Una segunda instancia no puede recuperar jobs de un propietario activo.

## Entidades y annotations

`TrainingProject` contiene GUID, nombre, descripción, clases, imágenes, estado, creación UTC, revisión y `updated_at_utc` opcional para proyectos anteriores. `TrainingClass` usa GUID estable; el orden del snapshot define el índice de salida, no la identidad de clase.

`TrainingImage` es el asset V1 (sin duplicar una entidad asset vacía): GUID propio, project ID, nombre original informativo, ruta relativa controlada, dimensiones, SHA-256, fecha, origen, `source_id`, tiempo opcional y annotations. `Reviewed` distingue imágenes sin revisar de ejemplos negativos confirmados sin cajas.

`BoundingBoxAnnotation` guarda `class_id`, `x_center`, `y_center`, `width`, `height`. Las coordenadas son normalizadas, finitas, con tamaño positivo y los cuatro bordes dentro de `[0,1]`. El estado primario es JSON propio; los TXT YOLO son derivados.

`TrainingDataset` describe hash del snapshot, YAML, IDs de train/validation y advertencias. `TrainingJob`, `TrainingProgress`, `TrainingConfiguration`, `TrainingResult`, `TrainingError` y `CustomModelMetadata` conservan ejecución y auditoría.

El servicio ofrece crear/editar/cargar/listar proyectos, importar y reemplazar annotations. No se pueden eliminar clases que aún tengan cajas referenciadas. Un proyecto con job activo no admite edición/eliminación; cada job conserva además el snapshot persistido. Estados del proyecto: Draft, Preparing, Ready, Training, Completed, Failed. Al cancelar vuelve a Ready; iniciar siempre vuelve a validar el dataset.

## Importación y seguridad de assets

`TrainingAssetService` acepta JPG/JPEG/PNG, verifica extensión/firma y decodifica con OpenCV. Límites: 40 MB y 40 megapíxeles. Copia a archivos GUID y detecta duplicados por SHA-256; borrar el original externo no afecta el proyecto. El nombre original no construye destinos.

`ImportFolderAsync` es no recursivo y devuelve errores por archivo. Una imagen corrupta no invalida importaciones anteriores. `ImportFrameAsync` recibe `ImageFrame` y guarda PNG; VideoFrame/CameraCapture exige `source_id` y conserva el tiempo disponible.

El servicio de assets no abre VideoCapture ni adquiere cámaras. `TrainingDesktopServices.ImportVideoAsync` abre únicamente archivos de video seleccionados (MP4/AVI/MOV/MKV), toma hasta 30 frames distribuidos y usa el hash del archivo como `source_id`. Cada archivo se mantiene en una partición del split. No abre índices de cámara ni URL RTSP. El stream compartido actual entrega imágenes con overlays; por eso no se ofrece captura de cámara para datasets en V1 y no se creó un segundo capture.

## Split y exporter

`DatasetSplitService` ordena grupos por SHA-256 de seed + origen (o ID de imagen). Usa aproximadamente 80/20 por grupos, configurable internamente y estable ante reordenamiento. Mantiene un video/burst completo en una partición: protección conservadora de leakage, sin hashing perceptual.

Requiere dos grupos independientes, imágenes revisadas y ejemplos de cada clase en entrenamiento. Menos de 20 imágenes y clases ausentes en validación generan advertencias. Un único video no se divide fingiendo independencia. Grupos desiguales pueden producir una proporción de imágenes diferente; no hay estratificación avanzada.

`YoloDatasetExporter` valida snapshot, existencia y SHA de assets; copia imágenes y genera labels con cultura invariante y mapeo GUID→índice. Negativos confirmados tienen labels vacíos válidos. YAML usa escalares/arrays JSON escapados, válidos en YAML, sin instrucciones de descarga.

```text
dataset-export/
  images/train/       images/val/
  labels/train/       labels/val/
  dataset.yaml
  snapshot.json
```

El hash cubre manifiesto, hashes de imágenes, cajas, clases, seed y split final. Reexportar sobre el mismo job se rechaza; reentrenar crea un job nuevo.

## Jobs, perfiles y recursos

```text
Queued → PreparingDataset → Training → Validating → Exporting → Registering → Completed
    cualquier estado no terminal → Failed / Cancelled
```

No hay saltos ni transiciones desde estados terminales. El servicio persiste tiempos/progreso/estado/dataset/resultado/error; serializa jobs con un semáforo. `StartAsync` retorna el job encolado y ejecuta trabajo en Task.Run. El token de la llamada no gobierna la vida del job: `CancelAsync` es explícito. `WaitAsync` permite pruebas/cierre coordinado.

Perfiles: Quick 20 epochs, Balanced 60, HighAccuracy 120; 640 px, seed 42, patience 15. Configuración interna: batch 4 en PreferSystemAvailability y 8 en los otros; workers 0. **El usuario elige CPU o GPU para cada entrenamiento**, independientemente del perfil. No hay selección automática. `TrainingDevice` conserva tipo, índice y nombre; el job guarda `selected_device`, configuración efectiva `cpu`/`cuda:N`, versión y SHA-256 del manifest de runtime. Hilos CPU: 2 / mitad de procesadores / procesadores disponibles según política.

`TrainingResourcePolicy` e `ITrainingResourceMonitor` aceptan una función de cámaras activas y producen `TrainingResourceWarning`. No hay pausa automática de cámaras, reserva garantizada de GPU/VRAM, throttling adaptativo ni monitor continuo.

## Runtime y dependencias

`ITrainingWorker` mantiene Application independiente de Python. `TrainingRuntimeOptions` exige rutas absolutas y archivos existentes. Defaults:

```text
%LOCALAPPDATA%/HandRaiseDetection/training/runtime/2.0.0-win-x64-cu130/python/python.exe
%LOCALAPPDATA%/HandRaiseDetection/training/runtime/2.0.0-win-x64-cu130/yolo26n.pt
%LOCALAPPDATA%/HandRaiseDetection/training/runtime/2.0.0-win-x64-cu130/worker.py
```

Estrategia B: bootstrap administrado por VisionControl, iniciado únicamente al pulsar **Preparar entorno**. Descarga el intérprete privado, componentes de procesamiento y modelo inicial, muestra progreso/tamaño cuando se conoce y permite cancelar/reintentar. No necesita Python, pip, venv, CUDA toolkit ni configuración de PATH del usuario. El ejecutable usa exclusivamente el runtime privado con `-I -B -u`, ArgumentList y Job Object. El controlador NVIDIA compatible, si existe, se detecta; Edge no instala drivers. CPU sigue disponible con o sin GPU.

`TrainingRuntimeProvisioner` gestiona NotInstalled/Checking/Preparing/Ready/Error. Prepara en `stage-<guid>`, verifica SHA-256 antes de extraer cada componente, valida mediante el worker y escribe `installed.json` con hashes de archivos antes del commit por renombrado. Un fallo conserva el runtime anterior; cancelación limpia únicamente el staging. Un lease impide reemplazar componentes usados por jobs. Los artefactos completos verificados quedan en una caché por SHA-256 y cada descarga activa conserva `.partial` más URL, hash, tamaño, ETag/Last-Modified. Al reabrir, **Continuar preparación** solicita HTTP Range desde el byte persistido; si el origen no admite Range o cambió el validador, reinicia solo ese artefacto. Hay tres intentos acotados por interrupción. Los parciales no se borran al cancelar/cerrar y nunca se activan como runtime.

Versiones fijadas: runtime/dependency set `2.0.0-win-x64-cu130`, worker `2.0.0`, Python `3.11.9`, Ultralytics `8.4.170`, PyTorch `2.14.1+cu130`, torchvision `0.29.1+cu130`, ONNX `1.23.1`; las demás dependencias exactas están en `requirements.txt`. El modelo `yolo26n.pt` pertenece al release `v8.4.0` de assets. No se resuelve `latest` ni se compila código fuente. Se extraen wheels compatibles en site-packages privados sin usar pip global.

La raíz de confianza inicial son los índices oficiales HTTPS: SHA de Python fijado en código desde su [manifest oficial](https://www.python.org/ftp/python/3.11.9/windows-3.11.9.json), hashes de wheels de PyPI/[PyTorch cu130](https://download.pytorch.org/whl/cu130/torch/) y digest del asset del [release del modelo](https://github.com/ultralytics/assets/releases/tag/v8.4.0). Se materializa `download-manifest.json` con URLs, versiones, tamaños y hashes ANTES de descargar binarios; no se acepta un componente sin hash. No es un manifest firmado propio de VisionControl. El receipt local también vincula worker/requirements al código distribuido. Cada job fija el hash del receipt; un cambio de entorno entre cola y ejecución falla explícitamente.

La primera instalación y la **Verificación profunda** requieren probe real: versiones y dependencias, imports, operación CPU/backprop mínima, operación de detección compilada y carga del modelo inicial, sin entrenamiento. Cada GPU se verifica con tensor, backprop y sincronización; se reportan nombre, índice y memoria libre en ese instante. El inicio normal usa el receipt ya validado, comprueba versión/recipe, estado completado, estructura y hashes de los archivos esenciales; no recorre miles de archivos, no importa PyTorch/Ultralytics, no inicializa CUDA y no consulta la red. Los dispositivos persistidos se identifican como la última detección conocida. Si un receipt legado no contiene dispositivos, la UI inicia en segundo plano un probe limitado a PyTorch/CUDA: no importa Ultralytics/ONNX, no carga el modelo base y no calcula los hashes integrales. El resultado válido se persiste; un fallo no borra la última GPU conocida y la deshabilita durante esa sesión. Antes de cada job, el worker privado vuelve a comprobar realmente el dispositivo elegido, sin fallback. No se usa DirectML como evidencia de capacidad de entrenamiento. Packaging conserva la copia de worker/requirements existente; los binarios pesados se preparan fuera de bin/obj/repo.

Se desactivan autoinstall, modo online, sync e integraciones cloud; se omite el chequeo de fuentes que intenta descargarlas al validar datasets sin plots. `amp=False` evita el probe AMP que puede descargar pesos auxiliares. No es un sandbox para pesos/código Python no confiables.

Fuentes: [exportación oficial](https://docs.ultralytics.com/modes/export/), [entrenamiento oficial](https://docs.ultralytics.com/modes/train/), [release 8.4.170](https://pypi.org/project/ultralytics/8.4.170/). También se inspeccionó código local del exporter/trainer; el worker queda fijado a esa versión.

## Protocolo .NET ↔ worker

JSON Lines UTF-8 versión 1 en stdout: capabilities, progress, completed, error. Ejemplos:

```json
{"protocol_version":1,"type":"progress","stage":"training","percent":25,"current_epoch":12,"total_epochs":60}
{"protocol_version":1,"type":"completed","model_path":"<job>/output/model.onnx","worker_version":"2.0.0","device":"cpu","metrics":{"map50":0.81}}
```

stdin comienza con `{"type":"start","protocol_version":1}` después de asociar el proceso al Job Object. Cancelación: `{"type":"cancel","protocol_version":1}`. `request.json` persiste dataset, base/hash, configuración, clases y versión.

El parser rechaza versiones/tipos/estados/epochs/porcentajes inválidos y resultados incompletos; límite 64 Ki caracteres por línea. No interpreta consola YOLO. El worker reserva un descriptor de stdout y redirige incluso stdout nativo de librerías al log stderr. .NET limita `worker.log` a aproximadamente 8 Mi caracteres y continúa drenando pipes. El propietario .NET publica actividad indeterminada inmediatamente después de iniciar el proceso, antes de los imports lentos. Al recibir epochs transforma `current_epoch/total_epochs` en porcentaje real de la fase; validación, exportación y publicación permanecen indeterminadas porque el worker no ofrece una fracción fiable.

El dispositivo real se guarda en la configuración del job. `output/environment.json` conserva versiones de paquetes/Python, configuración efectiva y hashes del script/peso. `job.json` incluye snapshot, perfil, seed, clases y resultado.

## GPU, cancelación y crash

El selector muestra CPU y las GPU detectadas; la GPU no disponible queda deshabilitada con explicación humana. No preselecciona un recurso. Antes de crear el job y al ejecutarlo se revalida la elección: si la GPU desapareció, se muestra «La GPU seleccionada ya no está disponible», con **Usar CPU** o **Volver a comprobar**. No hay fallback automático. Un error/OOM durante entrenamiento falla el job, sin reinicio en CPU. Se admite elegir una GPU entre varias, no entrenamiento multi-GPU. La visibilidad CUDA se fija antes de importar torch, para que la GPU elegida conserve su identidad aunque Ultralytics use índice local 0. Entrenamiento CPU/GPU e inferencia DirectML/CPU son configuraciones independientes.

Callbacks comprueban cancelación entre batches/epochs y validación. Gracia predeterminada 8 s, después se termina el árbol. No hay timeout predeterminado de duración total; una inicialización o entrenamiento válido no se aborta por ser lento. Se admite un timeout explícito solo en configuración/pruebas. Un Windows Job Object con KILL_ON_JOB_CLOSE contiene descendientes ante cancelación, protocolo inválido, crash del worker y cierre abrupto de Edge. Handshake antes de importar torch/crear hijos; si no puede establecerse la contención, se termina el proceso y falla el arranque.

El proyecto/dataset se conserva. TrainingError separa código, mensaje amigable, detalle y logPath. Al reabrir, jobs no terminales pasan a `edge_interrupted` sin reentrenar. Si el commit del modelo ocurrió antes del crash, se reconcilia por training_job_id y el job queda Completed. Cancelar antes del commit evita publicación; después del commit se finaliza Completed.

## ONNX, Model Registry y prueba

Se entrena YOLO26 Nano detect, valida best.pt, guarda mAP50/mAP50-95/precision/recall y exporta float32, batch 1, shape fija, opset 18, end2end=False, nms=False, simplify=False. Añade `visioncontrol_classes` JSON y task=detect; ejecuta ONNX checker.

`OnnxTrainingArtifactValidator` usa una sesión CPU del runtime existente: exige `[1,3,S,S] → [1,4+clases,N]`, float32, metadata exacta y una inferencia de prueba finita. No ignora errores de warmup. Se valida también la copia destinada al registro.

`TrainingModelPublisher` copia a `models/custom/custom-<project>-vN/model.onnx`. `StandardModelRegistry` persiste model.json como marcador final; recarga modelos comprometidos y verifica SHA-256. Los artefactos/manifiestos inválidos se omiten con CustomModelLoadErrors.

La única extensión de ModelDescriptor es `custom_training`, nullable/omitida en JSON builtin: distingue custom de builtin y conserva clases con GUID, proyecto/job, descripción, fecha, base, snapshot, worker y métricas. ID/nombre/versión/path/tamaño/capabilities/hash se reutilizan. Solo ObjectDetection, cero keypoints.

Un contador persistente y lease de publicación protegen versiones, incluso después de desregistrarlas. Un fallo puede dejar huecos/directorios sin model.json; no se anuncian como disponibles. Publicar el mismo job comprometido es idempotente. No hay CustomModelRegistry paralelo, providers ficticios ni activación automática.

`CustomModelTestService` recibe ImageFrame, usa backend CPU y devuelve GUID/nombre real, confianza y bbox en píxeles originales. Las annotations siguen normalizadas; son contratos diferentes. No inicia cámaras.

## Storage y lifecycle

```text
%LOCALAPPDATA%/HandRaiseDetection/
  training/
    owner.lock
    runtime/runtime.lock
    runtime/preparation.json    # manifest de una preparación recuperable
    runtime/cache/<sha256>/     # artefactos verificados y descargas .partial persistentes
    runtime/2.0.0-win-x64-cu130/ # intérprete, dependencias, peso, worker y manifests
    runtime/stage-<guid>/       # preparación no activa
    runtime/rollback-<guid>/    # sustitución protegida del runtime anterior
    projects/<guid>/project.json
    projects/<guid>/assets/<guid>.png|jpg
    jobs/<guid>/job.json
    jobs/<guid>/dataset-export/...
    jobs/<guid>/request.json
    jobs/<guid>/yolo26n.pt
    jobs/<guid>/worker.log
    jobs/<guid>/output/...      # best.pt, ONNX, métricas, environment.json
    deleted/<guid>-<guid>/...   # proyecto eliminado recuperable
  models/custom/
    publication.lock
    <project-guid>.version.json
    custom-<project-guid>-vN/model.onnx
    custom-<project-guid>-vN/model.json
```

No hay datos runtime en src/docs/bin/obj. El worker distribuido en bin es código/requirements. Los tests usan directorios temporales aislados.

Rutas GUID, contención, rechazo de traversal, caracteres inválidos/ADS y reparse points existentes. No se aceptan outputs fuera del job. No es una barrera frente a procesos maliciosos del mismo usuario que cambien junctions concurrentemente.

Eliminar explícitamente un proyecto lo mueve a training/deleted; modelos y snapshots de jobs tienen lifecycle independiente. Los snapshots conservan imágenes para auditoría: falta una política posterior explícita de retención/purga, no hay borrado silencioso.

## Pruebas y límites reales

`TrainingCoreTests` cubre creación/multiclase/reload, cajas, importación/frames/duplicados/corrupción, rutas, split, export/hash, estados/progreso/cancelación/fallo/completion, protocolo, contrato ONNX, clases sin keypoints, registro/versiones/recarga/no overwrite y proceso stub con crash/cancelación/descendientes. No carga pesos YOLO ni descarga dependencias; `TrainingWorkerStub.ps1` ejercita el transporte real sin Python.

No se ejecutaron YOLO real, GPU/DirectML, cámaras, servidores, Desktop ni publish durante este hotfix. Los tests de ONNX verifican la abstracción/contrato; no califican una exportación real ni calidad/rendimiento. La inspección del primer job físico confirmó que el runtime privado validó CUDA y escribió `environment.json`; los venv del repositorio no participan.

Hay bootstrap integrado bajo demanda y la preparación física ya llegó a Ready en el equipo del usuario. No hay reanudación de epochs, scheduler adaptativo, retención automática o sesión persistente de preview. La finalización de un entrenamiento/exportación real sigue pendiente de validación manual; los tests usan fixtures sin descargas pesadas ni Python global.

**Puente live implementado:** la configuración `custom_object_detection` persiste `configuration.model_id` (ID de versión inmutable) y `confidence_threshold` en el store de analíticas existente. `AnalyticManagementService` rechaza selecciones inexistentes/builtin al habilitar; permite deshabilitar o eliminar una selección cuyo modelo desapareció. No hay activación por publicar.

`CameraPipeline` pasa el mismo frame capturado a `CustomObjectRuntime`; agrupa analíticas por modelo y ejecuta una inferencia por modelo/frame. Reutiliza la sesión entre frames; la libera al retirar la selección o cerrar el pipeline. Los modelos custom V1 ejecutan en CPU mediante `WindowsMlOnnxBackend`; la selección GPU/CPU builtin sigue intacta. Un fallo de modelo queda aislado del builtin, otros modelos y otras cámaras, con error visible en la cámara. Para reintentar una versión fallida se puede detener/iniciar la cámara. El arranque del motor aún requiere su modelo builtin configurado.

Antes de cargar se valida el contrato/metadata ONNX y SHA-256. `ObjectDetectionPostprocessor` procesa solo `[1,4+C,N]` raw: comprueba forma, valores finitos, confianza, cajas, índice de clase, NMS por clase y transformación letterbox al frame original. Utiliza el contenedor de detecciones existente con cero keypoints, pero los objetos NO pasan por `HandEvaluator`, COCO-17 ni el tracker de personas. Los resultados `ProcessedCameraFrame.Objects` conservan modelo/versión, GUID/nombre/índice de clase, confianza y bbox; el overlay custom dibuja cajas y etiquetas.

Se emite `custom_object_detected` por objeto en lotes separados al menos un segundo por instancia, sin inventar identidad de tracking ni semántica de presencia/entrada/salida. La metadata guarda `model_id`, `model_name`, `model_version`, `class_id`, `class_name`, `class_index`, `bbox` en píxeles originales y `emit_snapshot=false`. Usa el bus/adaptador/persistencia existentes. No crea reglas, alertas ni notificaciones automáticamente. El Rule Engine ya puede resolver `metadata.class_id`/`metadata.class_name`; la UX actual de reglas aún no ofrece un selector de clase custom. Las reglas por tipo de evento/instancia conservan el flujo habitual.

## Prompt 2: UX y uso por cámara

Resultado de validación local de Fase 1: solución Release compilada sin restore, 0 errores y 16 advertencias CS0067 en archivos previos ajenos al training core; 39 pruebas focalizadas correctas; integridad de fuentes y `git diff --check` correctos. No se ejecutó la suite global.

La sección **Entrenamiento de Modelos** usa el tema/navegación WPF existente. Lista proyectos, objetos, estado humano, modificación y última versión. Permite crear/editar nombres y clases, importar archivos/carpetas/video y etiquetar con rectángulos. El editor respeta el letterbox: crear, seleccionar, mover, redimensionar por esquina inferior derecha, reclasificar y eliminar. Los borradores se guardan sin marcar revisión al cambiar imagen/proyecto o cerrar. «Marcar como revisada» confirma cajas o un negativo vacío. Las clases existentes conservan su GUID al reordenarlas; eliminar clases con cajas sigue rechazándose.

La preparación comprueba revisiones, cajas, split y existencia de assets; el exporter vuelve a verificar hashes antes del worker. Los perfiles visibles son Rápido, Equilibrado y Mayor precisión. La UI muestra actividad indeterminada durante preparación/imports/carga/validación/exportación/publicación, y época/total con porcentaje real durante entrenamiento. **Detalles técnicos** muestra última actividad, dispositivo efectivo, época, mensajes recientes, estado/error y permite abrir `worker.log`. El resultado publica automáticamente tras validación mediante el publisher de V1; las métricas pertenecen a la versión seleccionada. «Probar modelo» usa `CustomModelTestService` con una imagen y muestra cajas/clases/confianzas; no inicia streams. «Usar en VisionControl» lleva a Cámaras para elegir la cámara y agregar la solución custom con modelo/versión antes de guardar.

La carga de proyectos/historial termina antes de iniciar la comprobación del entorno y permanece disponible si el runtime tarda o falla. La comprobación rápida se ejecuta en segundo plano y reutiliza el receipt comprometido atómicamente. Un receipt nuevo guarda runtime/worker, recipe, instalación completada, fecha de última validación profunda, identidad de componentes y dispositivos detectados; los receipts legados continúan siendo válidos porque solo se escribían después de verificar y probar el staging. Receipt ausente/incompatible, estructura esencial incompleta o corrupción esencial impiden anunciar Ready y habilitan reparación/diagnóstico sin ocultar proyectos. **Verificación profunda** es una acción explícita para recorrer todos los hashes y ejecutar el probe; instalación/actualización también la ejecutan. Ninguna comprobación descarga componentes.

Archivos principales nuevos de Prompt 2: `Desktop/ViewModels/TrainingLabViewModel.cs`, `Desktop/Views/TrainingLabView.xaml(.cs)`, `Desktop/Controls/TrainingBoxEditor.cs`, `Infrastructure.Windows/Training/TrainingDesktopServices.cs`, `Application/Analytics/CustomObjectDetection.cs`, `Application/Inference/ObjectDetectionPostprocessor.cs` e `Infrastructure.Windows/Inference/CustomObjectRuntime.cs`. Integración incremental en navegación, `NodeHostController`, configuración de analíticas, planner, pipeline, backend y overlay existentes.

Pruebas de Prompt 2: `TrainingLabDesktopTests`, `CustomObjectLiveTests` y `CustomModelSelectionTests`, además de `TrainingCoreTests`. Cubren composición/propiedad, persistencia, GUID y coordenadas, negativos, selección/reload, inferencia agrupada con fakes, aislamiento con builtin y entre cámaras, ausencia/incompatibilidad, postproceso y liberación al desactivar. El recorrido publicado → cámara → detección usa un backend fake y artefactos fixture; no acredita entrenamiento ni calidad de un modelo real. La prueba E2E existente tenía claves incorrectas de rutas de notificaciones; se corrigieron solo las claves del fixture para mantener sus escrituras en el directorio temporal.

Validación final de Prompt 2: build Release correcto (0 errores, 16 advertencias CS0067); 57 pruebas focalizadas aprobadas y suite amplia con 471 aprobadas. La suite final excluye `PhysicalWebcam`, que enumera hardware. Se ajustaron dos pruebas existentes: el estado esperado sin recibir un frame es «Conectando», y el stub de cancelación cooperativa dispone del mismo margen de 8 segundos que producción; no se cambiaron esos comportamientos productivos. No se ejecutaron entrenamiento real, captura de cámaras, Desktop interactivo ni publish.

Comprobación final de Prompt 2: `scripts/verify-source-integrity.ps1` correcto, 0 fuentes vacías; `git diff --check` correcto (solo advertencias de conversión LF/CRLF).

La fase de runtime administrado agrega `TrainingRuntimeTests`: ausencia, instalación/verificación, hash/manifest inválidos, conservación del runtime anterior, cancelación/reintento, exclusión con worker activo, CPU/GPU y varias GPU, selección persistida, request correcto y fallo sin fallback. `TrainingLabDesktopTests` comprueba selección explícita y CPU con GPU presente; el test de MainWindow verifica también los bindings de preparación de solo lectura. No se ejecutaron descargas de componentes, entrenamiento, GPU física, cámaras, publish ni Desktop interactivo.

Hotfix de progreso/recuperación: la evidencia física mostró 34:44 minutos entre el inicio del job y la creación de `environment.json`; el primer mensaje del worker se emitía después de imports/probe CUDA. El transporte ahora publica actividad inmediata, conserva mensajes/estado/log en `TrainingProgress` y calcula la fase de entrenamiento desde epoch/total. `TrainingRuntimeTests` añade Range con validador, fallback sin Range, parcial persistente tras cierre y reutilización de caché verificada. Validación actual: build Release correcto (0 errores, 6 advertencias CS0067), 71 pruebas focalizadas Infrastructure y 9 Desktop aprobadas; integridad correcta. No se ejecutaron entrenamiento, descargas pesadas, cámaras, publish ni Desktop interactivo.

Hotfix de persistencia: el proyecto y sus assets nunca cambiaron de ruta; el bloqueo era una dependencia secuencial de UI. `TrainingLabViewModel` ahora carga proyectos primero y comprueba el runtime en segundo plano. El panel de preparación permanece oculto durante una comprobación normal y la edición no depende de Python/PyTorch/CUDA. Un historial ilegible no impide listar proyectos. La verificación del runtime instalado no consulta fuentes ni descarga componentes. Validación: build Release sin errores, 73 pruebas focalizadas Infrastructure y 11 Desktop aprobadas.

Hotfix de inicio rápido: `CheckAsync` ya no llama a la verificación integral ni al probe Python/CUDA. Reconoce en segundos un runtime validado mediante el receipt y cinco archivos esenciales; conserva SHA-256 integral en instalación y en **Verificación profunda**. El chequeo previo al job también evita recorrer 3,28 GiB, pero sí ejecuta el probe real para confirmar CPU/GPU. Los tests prueban que el inicio rápido no resuelve fuentes, no descarga, no invoca el probe y no modifica/elimina el runtime; el diagnóstico profundo detecta cambios en archivos no esenciales. Validación: build Release correcto (0 errores, 8 advertencias CS0067), 35 pruebas focalizadas de runtime y 12 de Desktop aprobadas; integridad correcta.

Hotfix de detección GPU: los receipts creados antes del inicio rápido no tenían `last_known_devices`; el fallback CPU se interpretaba como una incompatibilidad GPU confirmada. Ahora ese caso muestra **Comprobando GPU**, ejecuta solamente el probe PyTorch/CUDA privado en segundo plano y actualiza el selector al terminar. **Comprobar dispositivos** permite reintentar sin verificación profunda. Se distinguen datos históricos, disponibilidad confirmada y error de probe; un error actual deshabilita la GPU en sesión, conserva el receipt previo y nunca selecciona CPU automáticamente. Validación: build Release correcto (0 errores, 8 advertencias CS0067), 39 pruebas focalizadas de runtime y 13 de Desktop aprobadas; integridad correcta.

Validación de esta fase: `dotnet build HandRaiseDetection.slnx -c Release` correcto, 0 errores y 23 advertencias (CS0067 y NU1900 por consulta de vulnerabilidades NuGet no disponible). Pasaron 66 pruebas focalizadas Infrastructure (TrainingRuntime/TrainingCore/CustomObjectLive) y 10 Desktop (TrainingLab/MainWindow), 76 en total. Integridad correcta, 0 fuentes vacías; `git diff --check` sin errores. No se repitió la suite global.

### Prueba manual

1. Abrir Desktop y **Entrenamiento de Modelos** en un equipo sin Python global. Si falta el entorno, pulsar **Preparar entorno**, esperar avance, cancelar/cerrar durante una descarga y reabrir. Debe ofrecer **Continuar preparación**, reanudar el artefacto parcial y reutilizar los ya verificados. Un runtime existente válido debe seguir listo sin descarga.
2. Crear «Detector de aves» con `Pollo` y `Gallo`, guardar, agregar imágenes distintas de varios orígenes. Si usa video, agregar al menos otro origen independiente.
3. Dibujar/reclasificar/mover cajas, revisar cada imagen y confirmar algún negativo vacío. Cambiar imagen y reabrir el proyecto para comprobar persistencia.
4. Revisar preparación del dataset. Elegir explícitamente CPU o una GPU disponible y el perfil, e iniciar. Debe aparecer actividad indeterminada de inmediato mientras carga el entorno/modelo; después, época actual/total y porcentaje. Abrir **Detalles técnicos** y comprobar dispositivo, última actividad, mensajes y acceso al log. Verificar CPU seleccionable aun con GPU, elección persistida y ausencia de fallback silencioso. Cancelar y comprobar estado terminal; repetir hasta completar para validar publicación.
5. Al aparecer «Modelo listo», seleccionar versión y **Probar modelo** con una imagen independiente; comprobar clases, confianza y cajas.
6. **Usar en VisionControl** → elegir cámara → **Agregar Solución IA** → **Detección de objetos personalizada** → seleccionar modelo/versión y confianza → guardar/activar la solución. Iniciar la cámara explícitamente si estaba detenida.
7. Comprobar cajas custom y eventos; mantener una analítica builtin en la misma cámara para comprobar coexistencia. Reiniciar Edge y verificar selección persistida. No deben aparecer alertas sin una regla habilitada que coincida.
8. Detener/iniciar cámara y cerrar con X durante un job para comprobar liberación. La comprobación física y visual queda pendiente del usuario; no fue ejecutada por el agente.

Comandos manuales sin publish:

```powershell
dotnet build HandRaiseDetection.slnx -c Release
dotnet test HandRaiseDetection.slnx -c Release --no-restore --filter 'FullyQualifiedName!~PhysicalWebcam'
powershell -ExecutionPolicy Bypass -File scripts/verify-source-integrity.ps1
```

## Inventario de archivos de Prompt 1 (histórico)

Creados:

- `src/HandRaise.Domain/Training/TrainingEntities.cs`
- `src/HandRaise.Application/Training/TrainingContracts.cs`
- `src/HandRaise.Application/Training/TrainingApplicationService.cs`
- `src/HandRaise.Infrastructure.Windows/Training/TrainingStorage.cs`
- `src/HandRaise.Infrastructure.Windows/Training/TrainingDatasetServices.cs`
- `src/HandRaise.Infrastructure.Windows/Training/TrainingWorkerProtocol.cs`
- `src/HandRaise.Infrastructure.Windows/Training/WindowsTrainingProcessLease.cs`
- `src/HandRaise.Infrastructure.Windows/Training/PythonYoloTrainingWorker.cs`
- `src/HandRaise.Infrastructure.Windows/Training/CustomModelServices.cs`
- `tools/training-worker/worker.py`
- `tools/training-worker/requirements.txt`
- `tests-csharp/HandRaise.Infrastructure.Windows.Tests/TrainingCoreTests.cs`
- `tests-csharp/HandRaise.Infrastructure.Windows.Tests/TrainingWorkerStub.ps1`
- `docs/CUSTOM_MODEL_TRAINING.md`

Modificados sobre su estado encontrado (se preservaron cambios anteriores del usuario):

- `src/HandRaise.Domain/Models/ModelEntities.cs`
- `src/HandRaise.Infrastructure.Windows/Inference/StandardModelRegistry.cs`
- `src/HandRaise.Infrastructure.Windows/HandRaise.Infrastructure.Windows.csproj`
- `src/HandRaise.Host/HostApplication.cs`
- `tests-csharp/HandRaise.Infrastructure.Windows.Tests/HandRaise.Infrastructure.Windows.Tests.csproj`
- `docs/PROJECT_OVERVIEW.md`, `docs/ARCHITECTURE.md`, `docs/AI_PIPELINE.md`, `docs/STORAGE_AND_PATHS.md`, `docs/DEVELOPMENT_HANDOFF.md` (adiciones puntuales).

Los cambios de Desktop, servicios de cámaras y tests Host/Desktop visibles al iniciar Prompt 1 ya existían y se preservaron. Prompt 2 agrega únicamente la integración descrita arriba sobre ese estado.
