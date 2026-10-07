# VisionControl Edge — Project Agent Rules

Estas reglas aplican a todo el repositorio VisionControl Edge.

## 1. Prioridad del agente

La prioridad es TRABAJAR SOBRE EL CÓDIGO.

El agente debe concentrarse en:
- inspeccionar arquitectura;
- editar archivos;
- implementar cambios;
- corregir bugs;
- mantener contratos;
- crear o actualizar tests cuando sean necesarios;
- documentar cambios relevantes.

No debe gastar tiempo ni contexto ejecutando automáticamente ciclos completos de validación que el usuario puede realizar después.

## 2. Builds, tests y publish

PERMITIDO para el agente:
- `dotnet build HandRaiseDetection.slnx -c Release` (validar compilación limpia tras cambios)
- `dotnet test HandRaiseDetection.slnx -c Release --no-restore` (validar regresión de pruebas unitarias)

PROHIBIDO ejecutar automáticamente:
- `publish.cmd` (empaquetado final reservado para el usuario)
- publicación portable
- paquetes de distribución

El agente puede ejecutar `dotnet build` y `dotnet test` para diagnosticar y verificar sus cambios antes de entregar el resultado. No debe ejecutar `publish.cmd`.

## 3. Servidores y aplicaciones

POR DEFECTO, NO:
- iniciar `VisionControl.Edge.exe`;
- iniciar `HandRaise.Host`;
- abrir Kestrel;
- levantar servidores locales;
- mantener procesos en background;
- abrir puertos;
- ejecutar pruebas LIVE;
- iniciar streams;
- iniciar una cámara física;
- abrir navegador;
- iniciar procesos que queden residentes.

Estas acciones solo deben ejecutarse cuando el usuario solicite explícitamente una prueba LIVE o cuando una tarea dependa necesariamente de ello.

No iniciar servidores "por si acaso".

## 4. Hardware

NO ejecutar automáticamente pruebas con:
- cámara USB;
- RTSP;
- DirectML;
- GPU;
- NVR;
- dispositivos físicos.

La implementación puede prepararse para ellas. La validación física queda a cargo del usuario salvo instrucción explícita.

## 5. Integridad de archivos

El agente sí puede realizar comprobaciones ligeras de integridad cuando modifica o crea archivos.

Está permitido comprobar tamaño/existencia de archivos y ejecutar:
`powershell -ExecutionPolicy Bypass -File scripts/verify-source-integrity.ps1`

Esto es especialmente importante debido a incidentes históricos de archivos truncados.

Si aparece un archivo fuente de 0 bytes: DETENERSE.

No continuar desarrollo ni reconstruir silenciosamente sin informar.

## 6. Git

Permitido para inspección:
- `git status --short`
- `git diff`
- `git diff --stat`
- `git show`
- `git log`

NO hacer automáticamente:
- `git commit`
- `git push`
- `git reset --hard`
- `git restore .`
- rebase
- merge destructivo

salvo instrucción explícita del usuario.

## 7. Edición segura

Preferir:
- cambios incrementales;
- modificar únicamente archivos necesarios;
- preservar código funcional;
- no reescribir archivos completos sin necesidad;
- evitar refactors masivos durante tareas pequeñas.

Después de CREAR un archivo nuevo, comprobar que su tamaño sea mayor que 0 bytes.

## 8. No ampliar alcance

No aprovechar una tarea para:
- agregar features no solicitadas;
- crear endpoints nuevos;
- cambiar contratos congelados;
- introducir nuevas dependencias;
- cambiar arquitectura;
- limpiar código no relacionado.

Si se detecta una mejora adicional, reportarla como recomendación, pero no implementarla sin autorización.

## 9. Principios de arquitectura VisionControl Edge

Mantener:
- captura de cámara propiedad del Edge;
- una captura por pipeline;
- inferencia compartida;
- tracking compartido;
- analíticas modulares;
- configuración por cámara;
- hot CRUD;
- Rule Engine desacoplado;
- notificaciones desacopladas;
- API REST como frontera con VisionControl Console;
- contratos JSON `snake_case`;
- aislamiento de fallos;
- seguridad de API Key;
- no exposición de secretos.

## 10. UI/UX Windows

VisionControl Edge es una aplicación de escritorio para operadores.

Debe sentirse:
- moderna;
- clara;
- profesional;
- visualmente coherente;
- orientada a tareas;
- no como una herramienta para desarrolladores.

La cámara es el contexto principal.

El video solo debe estar presente donde aporte a la tarea:
- Monitor: sí;
- Espacios: sí;
- Analíticas IA: no como panel permanente;
- Reglas: no;
- Eventos: solo evidencia contextual;
- Configuración: no.

## 11. Resultado de cada tarea

El agente debe terminar con un reporte breve que incluya:
- archivos modificados;
- archivos creados;
- decisiones importantes;
- limitaciones;
- validaciones NO ejecutadas;
- comandos que el usuario puede ejecutar manualmente.

No ejecutar esas validaciones automáticamente solo para completar el reporte.

## 12. Regla de eficiencia

No gastar tiempo, tokens o ejecución local en:
- builds repetitivos;
- publish repetitivo;
- arranque/parada repetitiva de servidores;
- tests globales después de cada edición.

Durante desarrollo: trabajar primero en el código.

La validación completa se hace cuando el usuario la solicite o al cerrar una fase.
