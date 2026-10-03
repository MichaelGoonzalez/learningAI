# AGENTS.md — Reglas para agentes (HandRaiseDetection)

## Fuente de verdad
- `README.md` es el único documento vivo del proyecto; `docs/ARCHIVE.md` es histórico y `GEMINI.md`/`CLAUDE.md` solo redirigen. No crear otros `.md`.
- Lee solo las secciones del README que el bloque toque. No lo resumas ni lo repitas.

## Ahorro de tokens
- Respuesta final: código + resumen de máx. 5 líneas. Sin explicaciones largas.
- Sin confirmaciones intermedias: ante dudas menores, decide y anótalo en 1 línea.
- Cambios mínimos y localizados. No reescribas archivos completos sin necesidad.

## Alcance
- Implementa SOLO el bloque pedido. No adelantes bloques ni hagas refactors no pedidos.
- `HandRaise.Domain` es puro: sin OpenCV, ONNX, WPF, SQLite ni ASP.NET.
- No modifiques el dominio salvo bug real; avisa antes.

## Validación
- NO ejecutes webcam, ventanas, cámaras, RTSP ni pruebas manuales: el dueño las hace y reporta.
- Al final, una sola vez: `dotnet build` y los tests unitarios.
- Tests solo de lógica esencial. Nada de integración con hardware ni snapshots en vivo.
- Declara en 1 línea lo que no se validó.

## Convenciones técnicas
- C# / .NET 10, Windows x64, Nullable habilitado.
- `CancellationToken` e `36*21QAZra `Mat`, tensores y sesiones de forma determinista.
- Dependencias: gratis/open source, licencia verificada y registrada en el README, sin telemetría. Prefiere no agregar ninguna.
- Reloj: las reglas usan el reloj monotónico del frame; los eventos llevan UTC real anclado. Nunca `DateTime.Now` en el dominio.
- Credenciales RTSP: nunca en logs ni en texto plano en `appsettings.json`.
- Cambio de dispositivo en caliente: crear y validar la sesión nueva antes de reemplazar; si falla, conservar la activa.
- Sin descargas silenciosas de runtimes ni servicios de pago.

## Comandos (cmd.exe)
```
set DOTNET_CLI_HOME=%CD%\.dotnet_cli
set DOTNET_CLI_TELEMETRY_OPTOUT=1
"C:\Program Files\dotnet\dotnet.exe" build HandRaiseDetection.slnx --configuration Release --no-restore
"C:\Program Files\dotnet\dotnet.exe" test HandRaiseDetection.slnx --configuration Release --no-restore
```

## Al terminar un bloque
- Actualiza el README con máx. 10 líneas: estado del bloque, conteo de pruebas y hasta 3 comandos para probar a mano.
- Mantén "Registro de avance". Elimina o resume texto obsoleto en lugar de acumularlo.
- Formato de respuesta final: **Estado** · **Archivos clave** · **No validado** · **Probar a mano**.

## Mapa del repo
- `HandRaiseDetection.slnx` -> solución .NET 10 y proyectos incluidos.
- `src/HandRaise.Domain/` -> detección, estados y geometría pura.
- `src/HandRaise.Application/` -> contratos, tracking, pipelines, eventos y puertos.
- `src/HandRaise.Infrastructure.Windows/` -> OpenCV, Windows ML, DXGI, SQLite, settings y logs.
- `src/HandRaise.Host/` -> host headless, composición y REST `/api/v1`.
- `src/HandRaise.Desktop/` -> WPF/MVVM y editor de zonas.
- `src/HandRaise.DebugApp/` -> diagnóstico, grabación y benchmark.
- `src/HandRaise.DeviceProbe/`, `src/HandRaise.InferenceProbe/` -> sondas de hardware/inferencia.
- `tests-csharp/` -> suites xUnit; `models/` -> ONNX y manifiesto.
- `app/`, `tests/`, `scripts/`, `config.yaml` -> prototipo/herramientas Python, no producción.
- `packaging/`, `publish.cmd`, `dist/` -> publicación portable y artefactos.

## Dónde tocar según la tarea
- Endpoint/auth/ProblemDetails -> `src/HandRaise.Host/HostApplication.cs` y `Api/`.
- Ciclo de cámaras/API D2 -> `src/HandRaise.Host/Services/`.
- Regla de mano/estado/zona pura -> `src/HandRaise.Domain/`.
- Pipeline/tracking/evento/contrato -> `src/HandRaise.Application/`.
- Captura/overlay/dispositivo/inferencia -> carpeta homónima en `src/HandRaise.Infrastructure.Windows/`.
- SQLite/migración/snapshot/retención -> `src/HandRaise.Infrastructure.Windows/Storage/`.
- WPF/editor de zonas -> `src/HandRaise.Desktop/`.
- Config -> `appsettings.json` del ejecutable y sus tipos `Configuration/`.
- Tests -> proyecto homónimo bajo `tests-csharp/`; Host usa TestServer sin hardware.
