# CAPRI Music v0.2 — integración con el cerebro

Este es el documento principal del mod. El registro cronológico de trabajo está en [HISTORIAL_CAMBIOS.md](HISTORIAL_CAMBIOS.md). Ambos archivos viven junto al código de CAPRI Music en `mods/capri-music/`.

## Estado y arquitectura

El núcleo registra `spotify_music` para el modelo NVIDIA y encamina `play_music` hacia el auxiliar de Spotify o la ruta original de YouTube. La elección de proveedor y dispositivo se guarda solo en memoria durante cinco minutos y se descarta si la conversación cambia de tema. El CodeMod conserva conexión, desconexión, estado y permisos; ya no intercepta reproducción ni playlists. El auxiliar existente conserva OAuth PKCE, tokens DPAPI y llamadas a Spotify Web API. Ningún token se entrega al modelo.

La compilación y las pruebas simuladas **no demuestran** que NVIDIA haya elegido la herramienta en una conversación real ni que Spotify haya reproducido una canción. Ambas pruebas requieren intervención personal.

`cross/Music.cs` pertenece al control musical multiplataforma original de Capri; CAPRI Music v0.2 para Windows no lo modifica.

## Requisitos

- Windows 11, .NET Framework 4.8 para Capri y runtime .NET 10 para el auxiliar.
- Cuenta Spotify Premium autorizada en la aplicación `CAPRI Music` de Spotify for Developers, con redirect URI exacta `http://127.0.0.1:8888/callback`.
- Spotify Desktop disponible. Se debe elegir expresamente el dispositivo antes de reproducir.
- Client ID local en `capri-music-data/client-id.txt`, fuera de Git.

## Compilación e instalación

1. Desde la raíz del repositorio, ejecuta `powershell -ExecutionPolicy Bypass -File ci/build-windows.ps1 -OutputDirectory out/capri-music-v02`.
2. Desde `mods/capri-music/`, ejecuta `dotnet publish CapriMusicHelper/CapriMusicHelper.csproj -c Release -o dist --no-restore`.
3. Cierra Capri. Haz copia de seguridad del `Capri.exe` instalado y reemplázalo por `out/capri-music-v02/Capri.exe`. Conserva los demás archivos de la instalación.
4. Copia `mods/capri-music/capri-music.capri-code` directamente a la carpeta de mods que abre Mainframe → Mods. Capri solicitará aprobar el CodeMod modificado.
5. Copia **todo** `dist/` a `capri-music-data/helper/` dentro de esa carpeta de mods, incluidos `.exe`, `.dll`, `.deps.json` y `.runtimeconfig.json`.
6. Conserva la carpeta de datos existente con `client-id.txt`, `spotify-tokens.dpapi` y preferencia de dispositivo. No la copies al repositorio ni a otro usuario Windows.

El cargador de Capri solo lee los `.capri-code` situados directamente en la carpeta de mods. La carpeta `mods/capri-music/` contiene el código fuente.

## Vinculación y uso

- «Capri, conecta Spotify» inicia OAuth PKCE en el navegador. No ocurre al abrir la aplicación.
- «Estado de Spotify», «permisos de Spotify» y «desconecta Spotify» siguen en el CodeMod.
- «Pon Riptide» debe preguntar «¿Quieres que la reproduzca en Spotify o en YouTube?». «Spotify» conserva Riptide; «YouTube» usa el mecanismo original. «Pon Riptide en Spotify» no pregunta por proveedor.
- Si falta un dispositivo elegido, el puente muestra los disponibles y espera «la primera», un número, «en mi computadora» o «selecciona dispositivo NOMBRE». Tras elegirlo continúa la solicitud pendiente. No selecciona otro equipo por su cuenta.
- `spotify_music` admite `search`, `play_track`, `play_playlist`, `devices`, `select_device`, `pause`, `resume`, `next`, `previous`, `current`, `playlists` y `find_playlist`. La búsqueda no inicia reproducción.
- El auxiliar solicita precisión cuando varias pistas coinciden. Si la API acepta la reproducción pero la consulta posterior no confirma la pista, informa que todavía no pudo confirmarla. Una búsqueda de YouTube tampoco confirma reproducción.

## Seguridad y almacenamiento

El modelo recibe esquemas de herramienta, nombres solicitados y respuestas textuales; no recibe tokens, códigos OAuth ni el Client ID. El núcleo solo envía al auxiliar comandos de una lista fija y argumentos validados. No acepta URLs ni comandos del sistema producidos por el modelo. El proceso auxiliar termina después de cada orden, con límite de 30 segundos para operaciones normales. Los scopes siguen siendo `user-read-playback-state`, `user-modify-playback-state`, `playlist-read-private` y `user-library-read`.

Los tokens se guardan en `capri-music-data/spotify-tokens.dpapi`, cifrados con DPAPI del usuario Windows. El Client ID, dispositivo elegido y última lista de dispositivos son archivos locales de texto. «Desconecta Spotify» borra tokens y preferencia local. Para revocar también el consentimiento, elimina CAPRI Music en la página de aplicaciones de Spotify. Para desinstalar, restaura el ejecutable anterior, desactiva/elimina el CodeMod en Mainframe y elimina manualmente `capri-music-data/` si deseas borrar sus datos.

## Pruebas y límites

- `Capri.exe --smoke-test RUTA_TEMPORAL --only SmokeMusicBridge` prueba el puente con respuestas simuladas, sin Spotify real.
- `dotnet run --project CapriMusicHelper/CapriMusicHelper.csproj -c Release --no-build -- --self-test` cubre PKCE, state, DPAPI, renovación, dispositivos y HTTP 403/404/429 simulados.
- `tests/ModTests.cs` verifica que el CodeMod solo consume órdenes de conexión y diagnóstico.

La prueba real debe verificar que NVIDIA elige `spotify_music` o `play_music` con los argumentos correctos, y que Spotify reproduce en el dispositivo elegido. Spotify puede aceptar HTTP antes de reflejar el estado. Un dispositivo sin ID solo se usa cuando está activo y fue elegido por la persona. «Mi computadora» exige un único dispositivo de tipo `Computer`; si hay varios, se pide precisión. Spotify puede limitar endpoints y cuota en Development Mode. No se consulta contenido interno de playlists. La ruta visual antigua de Spotify permanece en el código, pero `play_music` usa el puente en esta versión. YouTube mantiene su ruta original.

## Reversión

Cierra Capri y restaura el `Capri.exe` anterior y, si quieres volver a v0.1, el CodeMod anterior. Conserva o elimina `capri-music-data/` según quieras mantener la sesión OAuth. La reversión no cambia NVIDIA, memoria, voz ni Obsidian.

## Referencias oficiales

- [PKCE](https://developer.spotify.com/documentation/web-api/tutorials/code-pkce-flow)
- [Renovación](https://developer.spotify.com/documentation/web-api/tutorials/refreshing-tokens)
- [Migración de febrero de 2026](https://developer.spotify.com/documentation/web-api/tutorials/february-2026-migration-guide)
- [Dispositivos](https://developer.spotify.com/documentation/web-api/reference/get-a-users-available-devices)
- [Reproducción](https://developer.spotify.com/documentation/web-api/reference/start-a-users-playback)
