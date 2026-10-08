# VisionControl Edge — Experiencia de Usuario y Diseño Desktop (WPF)

## 1. Principios de Diseño para el Operador

VisionControl Edge está diseñada como una aplicación moderna, fluida y orientada al operador de seguridad y monitoreo:
- **La cámara es el contexto central**: El video se muestra de forma prominente en las áreas de configuración y métricas.
- **Navegación clara y sin fricción**: Secciones principales organizadas jerárquicamente:
  1. **Resumen**: Estado general del Edge, total de cámaras y alertas recientes.
  2. **Cámaras**: Cuadrícula y listado de cámaras locales con estado en tiempo real.
  3. **Métricas / Alertas**: Visualización de eventos, gráficos de detecciones y registro de alertas operacionales con evidencia.
  4. **Configuración**: Opciones esenciales del sistema (General, Rendimiento e IA, Conexión Web, Modelos IA).

---

## 2. Onboarding y Primera Ejecución ("Cámara Virgen")

Para instalaciones nuevas sin cámaras:
1. Se presenta una experiencia guiada paso a paso (*Wizard* de primera ejecución).
2. **Paso 1: Bienvenida**: Explicación clara de la función del Edge.
3. **Paso 2: Selección de Origen**: USB, RTSP o Archivo de video.
4. **Paso 3: Verificación en Vivo**: Prueba de conexión con visualización inmediata de FPS y resolución.
5. **Paso 4: Identidad y Guardado**: Asignación de nombre y activación inicial.
6. **Paso 5: Éxito**: Acceso directo a la configuración de Soluciones IA o al monitor general.

---

## 3. Workspace Unificado de Cámara

La vista de detalle de cada cámara integra todas las herramientas operacionales en un solo entorno:
- **Lienzo Central de Video**: Visualización fluida del stream con capacidad de dibujo interactivo de zonas (rectángulos / polígonos) y líneas.
- **Panel Lateral de Herramientas**:
  - **Soluciones IA**: Activación y ajuste de parámetros data-driven para analíticas (`hand_raise`).
  - **Espacios y Geometría**: Administración de zonas de detección y líneas de conteo.
  - **Reglas y Alertas**: Creación de condiciones de notificación basadas en eventos.
  - **Ajustes de Cámara**: Modificación de origen, credenciales y opciones de conexión.

---

## 4. Temas Visuales y Accesibilidad

- **Soporte Completo de Modo Oscuro y Modo Claro**: Paleta de colores de alto contraste optimizada para entornos de centros de control y oficinas iluminadas.
- **Controles Personalizados**: Botones, cuadros de texto, menús desplegables y tarjetas con estilo cohesivo y tipografía clara (Segoe UI / Segoe UI Variable).
