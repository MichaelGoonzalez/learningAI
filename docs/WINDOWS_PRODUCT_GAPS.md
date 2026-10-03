# Brechas de Producto del Nodo Windows (Vision Edge Node Gaps)

Fecha de auditoría: 2 de Octubre de 2026  
Objetivo: Convertir el nodo en un **Vision Edge Node autónomo e industrial**, instalable y operable sin intervención técnica ni dependencia activa de UI.

---

## 1. Experiencia de Instalación y Primer Arranque (Zero-Config)

### Brechas detectadas:
1. **Falta de Autogeneración de Configuración Inicial (Self-Healing Configuration)**:
   - Si `appsettings.json` o los archivos en `%LOCALAPPDATA%` están ausentes, incompletos o corruptos, el Host puede arrojar excepciones en arranque en lugar de inicializar valores predeterminados seguros (ej. generar `nodeId` UUID automático, puerto default `5080`, binding a `0.0.0.0` / `localhost`).
2. **Verificación Proactiva de Readiness en Arranque (Pre-flight Checks)**:
   - No existe un validador de arranque consolidado que verifique en secuencia:
     - Permisos de escritura en directorio de almacenamiento y logs.
     - Disponibilidad y hashes de modelos ONNX en `models/`.
     - Disponibilidad y compatibilidad del runtime DirectML / CPU.
     - Disponibilidad de puertos de red (`5080`) para evitar crashes silenciosos si el puerto está ocupado.
3. **Falta de Instalador / Servicio Windows Nativo Configurado**:
   - Actualmente se ejecuta por consola o binario publicado. Se requiere que el nodo pueda registrarse como **Windows Service** nativo (`sc.exe create` o instalador WiX/InnoSetup) con auto-arranque en inicio de sistema y recuperación automática ante fallos (`Restart on Crash`).

---

## 2. Operación Autónoma y Resiliencia (Industrial Hardening)

### Brechas detectadas:
1. **Watchdog y Reconexión Exponencial de Fuentes RTSP**:
   - Si una cámara RTSP o webcam pierde señal (cable desconectado, corte de red, reinicio de cámara IP), el pipeline actual marca error o finaliza el bucle.
   - Se requiere una política de reconexión con backoff exponencial y jitter (ej. 2s, 5s, 10s, 30s) sin bloquear la memoria ni degradar otras cámaras activas.
2. **Supervivencia de Pipelines ante Fallos Aislados de Inferencia o Memoria**:
   - Una excepción transitoria en decodificación OpenCV o ejecución DirectML no debe matar el proceso del Host; debe registrarse en métricas de errores de la cámara e intentar reiniciar el pipeline de esa cámara de forma aislada.
3. **Readiness y Liveness Endpoints Diferenciados**:
   - `/api/v1/health` actual devuelve información básica.
   - Se requiere un endpoint `/api/v1/health/readiness` que indique si el nodo está listo para procesar (modelos cargados, base de datos migrada, backend activo, espacio en disco suficiente).
4. **Exportación de Diagnóstico y Logs en un Clic**:
   - Los operadores de planta necesitan descargar un bundle `.zip` o JSON estructurado con el estado del sistema, logs recientes, hardware detectado y métricas sin tener que acceder a carpetas internas de Windows.

---

## 3. Jerarquía y Manejo de Configuración de Producto

### Clasificación requerida:

| Nivel de Configuración | Propósito | Dónde vive | Exposición en API |
|---|---|---|---|
| **Sistema / Autonómica** | UUID de nodo, hardware backend óptimo, paths de DB, migraciones. | Calculado / `%LOCALAPPDATA%` | Solo lectura en `/system/*` |
| **Configuración Operativa (Frontend)** | Lista de cámaras, zonas, umbrales de confianza, FPS de streaming, retención de snapshots. | `cameras.json`, `appsettings.json` | Lectura y Escritura en `/cameras`, `/cameras/{id}/zones`, `/system/settings` |
| **Secretos Críticos** | Contraseñas RTSP, Tokens de autenticación, API Keys maestras. | DPAPI (`camera-credentials.json`) | **Nunca expuestos** (solo indicación `has_password: true`) |

---

## 4. Desacoplamiento de Analíticas y Escalabilidad Multicámara

### Brechas detectadas:
1. **Acoplamiento Directo a `TrackedHandEvaluator`**:
   - En el `CameraPipeline` actual, la lógica de evaluación de manos está instanciada directamente (`TrackedHandEvaluator`).
   - Para soportar futuros módulos (ej. `PackageCounting`, `ConveyorFlow`, `PPE Detection`) sin modificar el núcleo de captura y tracking, el pipeline debe orquestar una abstracción `IAnalyticModule` o cadena de evaluadores de dominio.
2. **Distribución de Carga y Capacidad Dinámica**:
   - La API `/system/capacity` estima capacidad teórica. El motor debe monitorear el tiempo promedio de inferencia y FPS reales para alertar al frontend si se está superando la capacidad del hardware antes de que ocurran pérdidas de frames.
