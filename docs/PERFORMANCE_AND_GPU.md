# VisionControl Edge — Rendimiento, GPU y Optimización de Hardware

## 1. Estrategia de Aceleración de Hardware

VisionControl Edge soporta ejecución híbrida optimizada para GPUs integradas y dedicadas bajo Windows 10/11:

- **GPU DirectML**: Aceleración nativa a través de DirectX 12 / DirectML para tarjetas gráficas Intel (Iris Xe, Arc), NVIDIA (GeForce, RTX, Quadro) y AMD (Radeon). No requiere drivers propietarios CUDA complejos para la inferencia estándar.
- **CPU Fallback**: Si no se detecta una GPU compatible con DirectX 12, el motor conmuta automáticamente a los núcleos CPU optimizados con instrucciones AVX2 / AVX-512.

---

## 2. Perfiles de Rendimiento

El sistema permite seleccionar perfiles de operación según las restricciones térmicas y energéticas del host:

| Perfil | Inferencia Objetivo | Streaming UI | Caso de Uso |
| :--- | :---: | :---: | :--- |
| **Equilibrado (Recomendado)** | 15 FPS | 30 FPS | Operación estándar en mini PCs industriales y equipos de escritorio. |
| **Eficiencia (Bajo consumo)** | 10 FPS | 15 FPS | Laptops en batería o equipos con ventilación pasiva. |
| **Rendimiento Máximo** | 30 FPS | 60 FPS | Servidores Edge dedicados con GPU dedicada. |

---

## 3. Optimizaciones de Memoria y Cero Latencia (PERF1)

1. **Buffer Pooling**: Reutilización de matrices de OpenCV y arrays de bytes para evitar recolecciones de basura (`GC Garbage Collection`) frecuentes en el bucle caliente de video.
2. **Decodificación Asíncrona de Video**: Ingesta en un hilo dedicado desacoplado del hilo de inferencia y del hilo de UI.
3. **Drop Late Frames**: Si el tiempo de inferencia supera el intervalo de fotogramas, se descarta el frame antiguo en lugar de encolar retraso.
4. **Desactivación de Renderizado Innecesario**: Cuando la ventana de la aplicación o una pestaña de cámara no está visible para el operador, el renderizado JPEG/WPF se detiene automáticamente mientras la inferencia continúa en segundo plano.
