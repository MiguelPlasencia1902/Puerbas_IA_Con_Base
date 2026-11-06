# Historial de avances — CAPRI Music

Este archivo registra los cambios de CAPRI Music dentro del repositorio. El estado de uso, instalación, seguridad y limitaciones está consolidado en [README.md](README.md). No contiene credenciales ni datos de la cuenta Spotify.

## Base v0.1

- Se creó `capri-music.capri-code` para reconocer órdenes directas en español mediante `ICapriMod.Heard` y responder con `ICapriHost.Say`.
- Se creó `CapriMusicHelper` como proceso .NET separado para OAuth Authorization Code con PKCE, callback de loopback en `127.0.0.1:8888`, renovación de tokens y llamadas a Spotify Web API.
- Los tokens quedaron protegidos con DPAPI del usuario Windows. El Client ID y los datos de sesión quedaron fuera del repositorio.
- Se añadieron consultas de dispositivos, selección explícita, búsqueda y reproducción de pistas, controles de reproducción y consultas de playlists, con pruebas simuladas sin cuenta real.

## Diagnóstico posterior de v0.1

- Se revisó la selección de dispositivos cuyo identificador llega nulo desde Spotify. El auxiliar exige que uno de esos dispositivos esté activo antes de usarlo y conserva nombre y tipo para volver a validarlo.
- Se mejoraron los mensajes ante HTTP 403, 404 y 429. `Refused` se identifica como detalle de la respuesta de Spotify cuando aparece, sin atribuirle una causa no demostrada.
- Se mantuvo la distinción entre solicitud HTTP aceptada y reproducción confirmada mediante una consulta posterior al estado del reproductor.
- Estas correcciones se verificaron con respuestas HTTP simuladas. La disponibilidad y el comportamiento reales de Spotify siguen dependiendo de una prueba personal.

## 2026-11-06 — v0.2: herramienta nativa para el cerebro

### Arquitectura

- `app/Hands.cs` registra `spotify_music` con acciones y argumentos cerrados. `play_music` continúa representando la petición musical y ahora permite preguntar por proveedor si el usuario no lo indicó.
- `app/Brain.cs` deja pasar las peticiones musicales al modelo en vez de ejecutarlas siempre por el atajo directo. Se evita que la corrección de herramientas reemplace una llamada válida a `spotify_music`.
- `app/MusicBridge.cs` conecta la herramienta con el **mismo auxiliar** de v0.1. Solo envía comandos permitidos; no acepta una URL o un comando de consola generado por el modelo.
- `app/Capri.cs` conserva temporalmente la elección Spotify/YouTube y, si falta, la del dispositivo. Al recibir la respuesta, reanuda la canción o playlist solicitada. El contexto caduca a los cinco minutos y se descarta si cambia el tema.
- El CodeMod v0.2 se restringe a conectar, desconectar, consultar estado y consultar permisos. Ya no intercepta música, dispositivos ni playlists, para que no compita con la herramienta nativa.
- `ci/build-windows.ps1` incluye `MusicBridge.cs` en la compilación de Windows. `cross/Music.cs` no cambió: pertenece a la función musical multiplataforma original.

### Comportamiento y seguridad

- Una petición sin proveedor espera la respuesta «Spotify» o «YouTube» antes de reproducir. Una petición que nombra Spotify usa la API; una que nombra YouTube conserva la ruta existente.
- La reproducción en Spotify requiere un dispositivo elegido explícitamente. El puente muestra la lista si falta elección y acepta número, «la primera», «mi computadora» o una selección por nombre. El auxiliar vuelve a validar el dispositivo.
- La herramienta distingue título y artista para evitar que un título que contiene «de» se divida incorrectamente. Se añadieron comandos estructurados `track-v2` y `search-v2` al auxiliar sin crear otro OAuth.
- El núcleo comprueba que el título y el artista propuestos por el modelo figuren en la petición del usuario antes de reproducir. No convierte una palabra aislada como «Spotify» en una nueva orden cuando no hay elección pendiente.
- Una playlist indicada como «mi playlist de favoritos» se consulta por el nombre «favoritos»; el auxiliar pide precisión si hay varias coincidencias.
- La aceptación HTTP no se presenta como reproducción confirmada. Las respuestas de la integración son en español y el auxiliar conserva sus diagnósticos seguros.

### Verificación realizada

| Comprobación | Resultado | Alcance |
| --- | --- | --- |
| Compilación de Capri Windows | Pasó | `Capri.exe` y `Capri-Setup.exe` generados en `out/capri-music-v02/`. |
| Compilación y publicación del auxiliar | Pasó | Artefactos en `mods/capri-music/dist/`; sin autenticación real. |
| `SmokeMusicBridge` | Pasó | Proveedor, dispositivo, búsqueda, playlist, no confirmación y llamada de herramienta con **NVIDIA simulado**. |
| `SmokeRequests` y `SmokeCloudBrains` | Pasaron | Regresiones de solicitudes y proveedores con servicios simulados. |
| Pruebas del CodeMod | Pasaron | Compilador de .NET Framework; conexión y diagnóstico sin capturar órdenes musicales. |
| Autopruebas del auxiliar | Pasaron | PKCE, state, DPAPI, persistencia, renovación, dispositivos y errores HTTP simulados. |
| `git diff --check` | Pasó | Sin errores de formato detectados. |

No se realizó ninguna solicitud a Spotify ni autorización OAuth en las pruebas automatizadas. No se leyeron tokens ni archivos de recuerdos. No se modificó `main`; no hubo commits ni pushes.

### Pendiente de validación real

1. Instalar el `Capri.exe`, CodeMod y auxiliar compilados siguiendo `README.md`.
2. Confirmar en una conversación real que NVIDIA selecciona `play_music` o `spotify_music` y envía el título, artista y proveedor correctos.
3. Pedir una canción sin proveedor, responder «Spotify», escoger el dispositivo y comprobar de forma independiente la pista activa en Spotify Desktop.
4. Probar Spotify explícito, YouTube explícito, pausa, continuación, búsqueda, playlist y rechazo de API; verificar que no se ejecuten dos rutas para una misma petición.
5. Evaluar respuestas breves para resultados musicales ambiguos. «La primera» está implementado para la selección de dispositivo; seleccionar por posición una pista ambigua todavía no está implementado.

**Estado:** código preparado y probado con simulaciones; integración real con NVIDIA y reproducción en el dispositivo elegido aún no confirmadas.
