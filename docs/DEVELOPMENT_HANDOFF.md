# VisionControl Edge — Guía de Transferencia Técnica (Development Handoff)

## 1. Requisitos de Entorno de Desarrollo

- **Sistema Operativo**: Windows 10 / Windows 11 (x64)
- **SDK**: .NET 8.0 / .NET 9.0 SDK
- **Herramientas**: Visual Studio 2022 (v17.8+) o Visual Studio Code con extensiones C# Dev Kit
- **Dependencias del Sistema**: Media Foundation, DirectX 12 Runtime (incluido en Windows 10/11)

---

## 2. Comandos de Compilación y Validación

### Compilación limpia de la solución:
```powershell
dotnet build HandRaiseDetection.slnx -c Release
```

### Ejecución de la suite de pruebas unitarias y de integración:
```powershell
dotnet test HandRaiseDetection.slnx -c Release --no-restore
```

### Verificación de integridad de archivos fuente:
```powershell
powershell -ExecutionPolicy Bypass -File scripts/verify-source-integrity.ps1
```

---

## 3. Puntos de Extensión Comunes

### Cómo agregar una nueva Solución IA / Analítica:
1. **Definición de Dominio**: Crear una clase que implemente `IAnalyticDefinition` en `HandRaise.Application.Inference` o `HandRaise.Domain`.
2. **Esquema de Parámetros**: Declarar sus `ParameterDefinition` (tipos, valores por defecto, rangos). La UI de WPF y React generarán los controles visuales automáticamente.
3. **Procesamiento en Pipeline**: Conectar la lógica de evaluación en `CameraPipeline` / `AnalyticWorker` para procesar los keypoints o bounding boxes producidos por el modelo ONNX.
4. **Disparo de Eventos**: Emitir `AnalyticEvent` al `IEventBus` cuando se cumpla la condición analítica.

---

## 4. Prácticas y Reglas del Proyecto

- **No romper contratos congelados**: Mantener retrocompatibilidad en `/api/v1/*` y formatos JSON `snake_case`.
- **Preservar Single Capture**: Nunca instanciar múltiples objetos `VideoCapture` para una misma cámara física.
- **Liberación Determinista**: Asegurar que toda suscripción a streams de video implemente `IAsyncDisposable` / `IDisposable` y libere los canales asociados.

## 5. Training Lab V1 — Desktop y detección custom por cámara

Ver [CUSTOM_MODEL_TRAINING.md](CUSTOM_MODEL_TRAINING.md) para contratos, runtime, seguridad, pruebas y pasos manuales. Prompt 2 agrega la sección WPF, editor de cajas/negativos, importación de imágenes/carpetas/videos, perfiles, jobs/progreso/cancelación, métricas/versiones y prueba de imágenes. `NodeHostController.GetTrainingAsync` conserva una sola instancia de TrainingCore y el mismo StandardModelRegistry entre navegación y reinicios del Host; el cierre espera su disposición.

La solución `custom_object_detection` persiste `model_id` y confianza en las analíticas de cada cámara. `CustomObjectRuntime` usa el frame del pipeline existente, una ejecución por modelo/frame y sesiones CPU reutilizadas, con postproceso detect separado y GUID/nombres custom. No entrega objetos a HandEvaluator ni al tracking de personas. Publicar no activa cámaras. Los fallos de modelos se aíslan y se muestran en la cámara. Los eventos `custom_object_detected` conservan metadata por clase; las alertas requieren reglas explícitas.

Pendientes reales: completar y validar manualmente un entrenamiento/modelo real, captura de ejemplos desde cámara sin overlays y selector de clase custom en la UI de reglas. El bootstrap ya alcanzó Ready físicamente en el equipo del usuario. El motor de reglas admite campos de metadata, pero no se amplió su editor. Inferencia custom V1 en CPU; el builtin conserva su GPU/CPU habitual. Sin nuevos endpoints, cambios React ni publish.

Nota de entorno: los proyectos actuales son .NET 10. Los venv Python presentes en este checkout apuntan a un intérprete ausente; se inspeccionaron sus paquetes para fijar versiones, pero no se ejecutó entrenamiento real ni se aprovisionó un runtime.

## 6. Runtime privado administrado y elección de procesamiento

**El usuario elige CPU o GPU para cada entrenamiento**, sin selección automática ni fallback silencioso. El perfil sigue siendo independiente. El worker consulta capacidad real de entrenamiento y representa varias GPU; V1 usa una por job. La selección, configuración técnica, snapshot/seed, versión y hash del runtime quedan en el job y request. El recurso de inferencia de cámaras no cambia.

Estrategia B: **Preparar entorno** inicia bootstrap desde fuentes oficiales con versiones fijadas. `TrainingRuntimeProvisioner` descarga a una caché persistente por SHA-256, prepara en staging, valida con el worker y compromete el runtime por renombrado; errores/cancelación conservan el anterior. Parciales y validadores HTTP sobreviven cierre/reinicio y se retoman con Range; sin soporte Range se reinicia solo el artefacto afectado. La UI ofrece **Continuar preparación**. `OfficialTrainingRuntimeSource` no usa latest. No hay dependencia de Python/pip/CUDA toolkit global ni cambios en PATH.

Los componentes pesados viven en `training/runtime/2.0.0-win-x64-cu130`, fuera del ejecutable/repo; packaging sigue copiando únicamente worker/requirements. El progreso ahora se persiste desde que arranca el proceso: inicialización/carga y fases sin fracción son indeterminadas; epochs muestran porcentaje real. Detalles técnicos expone actividad, dispositivo, epoch, mensajes, estado/error y `worker.log`. Se eliminó el timeout total predeterminado. El worker empaquetado no cambió de hash, por lo que este hotfix no invalida ni reinstala el runtime actual. Contratos y prueba manual en [CUSTOM_MODEL_TRAINING.md](CUSTOM_MODEL_TRAINING.md).

Validación de runtime administrado: build Release correcto (0 errores, 23 advertencias CS0067/NU1900); 76 pruebas focalizadas aprobadas (66 Infrastructure, 10 Desktop). Integridad: 0 fuentes vacías y diff sin errores. No se ejecutó la suite global ni hardware/descargas reales.

Validación final de Prompt 2: build Release sin errores (16 advertencias CS0067), 57 pruebas focalizadas y 471 pruebas de la suite amplia aprobadas. La suite final usó `--filter 'FullyQualifiedName!~PhysicalWebcam'` para excluir la enumeración de hardware. La prueba manual visual, captura e inferencia con un modelo entrenado real siguen pendientes; no se ejecutaron Desktop interactivo, entrenamiento ni publish.

Hotfix posterior: el job físico observado tardó 34:44 minutos en imports/probe CUDA antes del primer JSONL; por eso WPF permanecía en 0%. .NET informa inicio indeterminado de inmediato, traduce epoch/total a porcentaje real, persiste observabilidad y enlaza Detalles técnicos/`worker.log`. Las descargas guardan parciales/validadores y caché por SHA fuera de staging. Build Release: 0 errores y 6 advertencias; focalizadas: 71 Infrastructure + 9 Desktop; integridad correcta. No se ejecutaron entrenamiento, descargas, Desktop ni hardware.

Hotfix de persistencia posterior: la lista vacía no era pérdida de datos. `TrainingLabViewModel.InitializeAsync` esperaba el hash de 19.744 archivos/3,28 GiB y el probe antes de `RefreshAsync`. Ahora carga proyectos primero, deja edición disponible y comprueba runtime en segundo plano con etapa/progreso visibles. Fallar el probe no sugiere reinstalar; un runtime válido se reutiliza sin resolver fuentes ni descargar. La ruta histórica bajo `%LOCALAPPDATA%\HandRaiseDetection` no cambió. Build Release sin errores; 73 pruebas focalizadas Infrastructure y 11 Desktop aprobadas.

Hotfix de inicio rápido: la apertura normal lee el receipt comprometido, verifica compatibilidad/recipe, estructura mínima y hashes de cinco archivos esenciales. No enumera los 19.744 archivos, no importa PyTorch/Ultralytics, no inicializa CUDA y no usa red. El receipt conserva runtime/worker, estado completado, fecha de validación profunda, hashes de componentes y última capacidad CPU/GPU conocida; receipts legados siguen aceptados por su commit posterior al probe. La acción **Verificación profunda** conserva el recorrido SHA-256 y el probe real para diagnóstico, instalación y actualización. Antes de entrenar se ejecuta el probe real sobre el dispositivo elegido y nunca se cambia GPU a CPU silenciosamente. Proyectos y datasets siguen independientes del estado del runtime. Build Release: 0 errores y 8 advertencias; focalizadas: 35 runtime + 12 Desktop; integridad correcta.

Hotfix GPU: un receipt legado sin `last_known_devices` ya no se normaliza como «GPU no disponible». Tras el check rápido, `TrainingLabViewModel` inicia un probe de dispositivos no bloqueante que usa el Python privado e importa solo PyTorch para comprobar CUDA, nombre/índice, memoria y una operación mínima por GPU. No ejecuta el probe integral, Ultralytics/ONNX, modelo base, red ni hashes masivos. El resultado válido se guarda en el receipt; los fallos conservan los datos previos, deshabilitan la GPU durante la sesión y ofrecen **Comprobar dispositivos**. El job mantiene su revalidación integral y no hace fallback a CPU. Build Release: 0 errores y 8 advertencias; focalizadas: 39 runtime + 13 Desktop; integridad correcta.

Integridad final correcta: 0 archivos fuente vacíos y `git diff --check` sin errores; solo advertencias LF/CRLF.
