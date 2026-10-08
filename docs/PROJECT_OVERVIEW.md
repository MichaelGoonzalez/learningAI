# VisionControl Edge — Visión General del Proyecto

## 1. Identidad y Propósito

**VisionControl Edge** es una aplicación de escritorio y appliance para Windows (x64) diseñada para operar de forma autónoma en el borde (*Edge computing*).

Su función principal es:
1. **Captura local de video**: Ingesta de transmisiones desde cámaras USB (MediaFoundation/DirectShow), flujos RTSP/RTSPS de cámaras IP/NVR y archivos de video locales para pruebas.
2. **Inferencia de Inteligencia Artificial en tiempo real**: Ejecución optimizada de redes neuronales (ONNX Runtime con aceleración DirectML/GPU o CPU) para detección de poses, gestos (`hand_raise`), objetos y analíticas visuales.
3. **Analítica espacial y eventos**: Evaluación de zonas poligonales, rectángulos y líneas virtuales para generar eventos analíticos estructurados.
4. **Motor de Reglas y Alertas locales**: Evaluación desacoplada de condiciones para disparar alertas con evidencia visual (snapshots JPEG).
5. **Runtime REST API & Streaming MJPEG**: Servidor Kestrel embebido con autenticación mediante API Key para permitir la integración total con interfaces remotas como **VisionControl Console (React)**.

---

## 2. Principios de Diseño y Operación

- **Edge-First**: La captura, el procesamiento de video, la inferencia y el disparo de reglas ocurren 100% en el hardware local. No depende de conexión constante a la nube.
- **Single Source of Truth**: El estado de cada cámara (`En línea`, `Detenida`, `Fallida`) y su pipeline de ejecución reside en el runtime del Edge (`HeadlessEngineService` / `ICameraManagementService`).
- **Desacoplamiento total**:
  - Captura y decoding independientes de la presentación.
  - Inferencia y analíticas desacopladas del renderizado UI.
  - Generación de alertas desacoplada del consumo en dashboards.
- **Cero fugas de recursos**: Concurrencia controlada con `Channel<T>` bounded, buffer pooling de memoria no administrada, descarte automático de fotogramas atrasados (drop late frames) y cancelación limpia (`CancellationToken`).

---

## 3. Componentes del Repositorio

- **`HandRaise.Host`**: Núcleo de servicios, servidor web Kestrel, endpoints REST v1, streaming MJPEG, persistencia JSON y orquestación de pipelines de cámaras.
- **`HandRaise.Desktop`**: Interfaz de operador y configuración en WPF / XAML con tema claro/oscuro, asistente de onboarding, configuración espacial interactiva y telemetría en tiempo real.
- **`HandRaise.Infrastructure.Windows`**: Implementaciones de bajo nivel para captura de video, enumeración de dispositivos DirectShow, aceleración DirectML, protección DPAPI de credenciales y diagnósticos de hardware.
- **`HandRaise.Domain` & `HandRaise.Application`**: Modelos de dominio, reglas, analíticas, contratos, DTOs y abstracciones limpias.

## Training Lab V1 — Fase 1

Disponible el [núcleo de modelos personalizados](CUSTOM_MODEL_TRAINING.md): proyectos/datasets/annotations persistidos, jobs, worker YOLO externo, exportación y validación ONNX, registro versionado y prueba sobre imágenes. Sin UX todavía ni activación automática en cámaras. La documentación detalla el gap de integración con analytics live y el trabajo del Prompt 2.
