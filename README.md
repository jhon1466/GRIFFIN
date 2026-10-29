# Griffin

Servidor privado de **Girls' Frontline 2: Exilium** (cliente CN de PC **4.0.5136** y de Android **4.0.5137**, tablas `1195672`), escrito en C# (.NET 8) con PostgreSQL.
Inspirado en [NTRSimulator](https://github.com/raphaeIl/NTRSimulator) (ver [Créditos](#créditos)).

## Proyectos relacionados

| Proyecto | Para qué sirve |
|---|---|
| [**GRIFFIN-ES-EN**](https://github.com/jhon1466/GRIFFIN-ES-EN) | Parche de idioma: pone el juego en **español** o **inglés**, en Android (app con Shizuku) y PC. |
| [**GRIFFIN-TOOLS**](https://github.com/jhon1466/GRIFFIN-TOOLS) | **Griffin GM Tools**: app de escritorio para administrar el servidor (dar dolls, armas, objetos, gestionar cuentas…). |

> Proyecto sin ánimo de lucro, hecho con fines educativos y de preservación. No está afiliado a SunBorn ni a ningún editor del juego y **no incluye archivos del juego**: necesitas tu propio cliente instalado.

## Requisitos

| Requisito | Detalle |
|---|---|
| Sistema | Windows 10/11 (el parche y el lanzador son solo para Windows x64) |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | para compilar y ejecutar el servidor |
| [PostgreSQL](https://www.postgresql.org/download/) 12 o superior | en `localhost:5432`, usuario `postgres`, contraseña `password` (se cambia en `Griffin/appsettings.json`, clave `ConnectionStrings:Postgres`). La base `griffin` se crea sola |
| Cliente CN de GF2 Exilium **4.0.5136** | instalado en `C:\Program Files\GF2Exilium\GF2 Game` |
| Griffin Patcher | `griffin_patcher.dll` + `griffin_launcher.exe`, descargables en [Releases](../../releases) (opcional: [Rust](https://rustup.rs) si quieres compilarlo tú, código en `GriffinPatcher/`) |

Las tablas del cliente (`Resources/Tables`) se preparan solas la primera vez que arranca el servidor: las copia de la caché de tu cliente si es la misma versión (o de la ruta que pongas en la variable `GRIFFIN_CLIENT_TABLES_DIR`) y, si no, las descarga.

## Cómo iniciar

1. **Clona** el repositorio:
   ```powershell
   git clone https://github.com/jhon1466/GRIFFIN.git
   cd GRIFFIN
   ```
2. **Instala el parche** (una sola vez). Descarga `griffin_patcher.dll` y `griffin_launcher.exe` de [Releases](../../releases), colócalos en la carpeta `GriffinPatcher\` y, en PowerShell **como administrador**, ejecuta:
   ```powershell
   .\scripts\install-griffin-patcher.ps1
   ```
   Comprueba el SHA-256 de ambos archivos y los copia a la carpeta del juego. (También puedes copiarlos a mano a `GF2Exilium\GF2 Game\`.)
3. **Inicia el servidor**:
   ```powershell
   .\scripts\start-server.ps1
   ```
   Compila en Release, comprueba que PostgreSQL responde y que los puertos 80, 443 y 7001 están libres, y arranca el servidor. Déjalo abierto; se detiene con `Ctrl+C`.
4. **Inicia el juego** con `griffin_launcher.exe` (**como administrador**) desde la carpeta del juego. El parche redirige el HTTP de `*.sunborngame.com` a `https://127.0.0.1` y acepta el certificado local, así que no hace falta mitmproxy.
5. Crea tu cuenta/entra y, ya en el lobby, abre el chat con el contacto integrado y escribe:
   - `/inventory provision`: monedas, regalos, ingredientes, llaves y objetos de las tablas.
   - `/inventory addall`: todos los personajes, armas, módulos, aspectos, trajes y fondos.
   - `/help`: lista de comandos.

Variables de entorno opcionales (`GRIFFIN_*`): `GRIFFIN_CLIENT_TABLES_DIR`, `GRIFFIN_STAMINA=full` (energía siempre llena, solo para pruebas), `GRIFFIN_FREE_STAGES=1` (misiones sin coste, solo para pruebas).

## Android

El servidor también admite el cliente CN de **Android 4.0.5137**, y a la vez que el de PC: detecta qué cliente le habla y responde a cada uno con su versión.

| | PC | Android |
|---|---|---|
| Cliente | 4.0.5136 | 4.0.5137 |
| Recursos (`ResVersion`) | 4.0.5136.14945.29000 | 4.0.5137.14945.29001 |
| Versión binaria | 4.0.5136.3257 | 4.0.5137.3257 |
| Tablas | 1195672 | 1195672 |

Para jugar desde el teléfono:

1. El teléfono y el PC del servidor tienen que estar en la **misma red** (wifi o punto de acceso del PC).
2. En `Griffin/appsettings.json`, pon en `GameServer:Host` la **IP local del PC** (la que ve el teléfono), no `127.0.0.1`.
3. El APK tiene que apuntar su SDK a `http://<IP del PC>` (puerto 80). Este repositorio **no incluye** ningún APK ni herramienta para modificarlo.
4. Deja abierto el puerto 80 y el 7001 en el firewall de Windows para la red local.
5. La primera vez el juego descarga todos sus recursos (unos **44 GB**) del CDN oficial, así que necesita internet y espacio libre.

Si sale una versión nueva del APK, los valores de Android se cambian sin recompilar con `GRIFFIN_ANDROID_CLIENT_VERSION`, `GRIFFIN_ANDROID_AB_RESOURCE_VERSION`, `GRIFFIN_ANDROID_BINARY_VERSION` y `GRIFFIN_ANDROID_GAME_CLIENT_VERSION`.

## Estado y hoja de ruta

El servidor responde a unos 430 de los ~1020 mensajes de petición del cliente con comportamiento propio; el resto devuelve respuestas vacías o errores nativos del cliente para que las pantallas no se rompan.

### Funciona
- [x] Inicio de sesión, SDK local (HTTPS 443), lista de servidores y conexión de juego (TCP 7001)
- [x] Inventario: personajes, armas, módulos de arma, aspectos, trajes, piezas, fondos, objetos y monedas
- [x] Comandos de administración desde el chat (`/inventory`, `/help`, `/timewarp`)
- [x] Gacha (banners de personajes, armas y aspectos) con pity guardado
- [x] Campaña completa: todas las fases de las tablas (más de 5000) se pueden iniciar, jugar y resolver, con recompensas, estrellas y desbloqueo de capítulos y de historias de eventos anteriores por logros
- [x] Energía que se recupera con el tiempo y se compra
- [x] Personajes: nivel, clase, grado, árbol de talentos (incluido el nodo clave), Espiral Mental, llaves de talento públicas y núcleo de remodelación (a nivel 55)
- [x] Armas: nivel, bloqueo, ruptura y desmontaje
- [x] Dormitorio y lobby 3D: trajes, piezas de ropa, poses desbloqueadas por nivel, cocina, jardín, regalos
- [x] Afecto de personajes: regalos con bonificación por preferencia, eventos por nivel (petición y batalla conjunta) y recompensas
- [x] Correo, misiones diarias, logros, pase de batalla, tienda y perfil
- [x] Sala de control y expediciones
- [x] Amigos y chat con un contacto integrado
- [x] Parche propio (Griffin Patcher) que redirige el SDK sin proxy

### Parcial / sin verificar en el cliente real
- [ ] Afecto: juramento (nivel 8) y batalla conjunta de los niveles 5–7
- [ ] Núcleo de remodelación: sus plugins (inventario de chips) y su coste real
- [ ] Modos de práctica y simulación (muchas peticiones solo devuelven una respuesta vacía)
- [ ] Eventos de temporada y actividades (la mayoría solo responde con datos de ejemplo)

### Todavía no funciona
- [ ] PvP y combate multijugador (requieren un servidor de batalla)
- [ ] Banda / Gremio
- [ ] Zona oscura y otros modos de exploración en línea
- [ ] Soporte para el servidor global y otras versiones del cliente (ya funcionan PC 4.0.5136 y Android 4.0.5137)
- [ ] Combate validado por el servidor (el cliente simula la batalla y el servidor acepta el resultado)
- [ ] Panel de administración propio (por ahora solo el chat)

## Estructura

| Carpeta | Contenido |
|---|---|
| `Griffin/` | proyecto ejecutable (host, `appsettings.json`) |
| `Griffin.GameServer/` | handlers del juego, servicios y comandos |
| `Griffin.SDKServer/` | servidor HTTPS del SDK |
| `Griffin.Common/` | red TCP, cifrado, despacho de paquetes |
| `Griffin.Common.Proto/` | definiciones `.proto` del protocolo y de las tablas |
| `Griffin.Common.Table/` | lector de las tablas del cliente |
| `Griffin.Database/` | Entity Framework Core + PostgreSQL |
| `Griffin.Command/` | motor de comandos |
| `Griffin.ProtocolSmoke/` | cliente de pruebas del protocolo |
| `GriffinPatcher/` | código (Rust) del parche; los binarios van en Releases |
| `scripts/` | `start-server.ps1` e `install-griffin-patcher.ps1` |
| `tools/` | generadores de código (ver más abajo) |

## Generadores de código

Parte del código del servidor se genera a partir de los `.proto` y de las tablas del cliente. **El código generado ya está en el repositorio, así que para usar el servidor no hace falta ejecutar nada de esto.** Solo se necesita (con [Python 3](https://www.python.org/downloads/)) si cambias los `.proto` o actualizas las tablas a otra versión del cliente:

| Script | Genera | Cuándo ejecutarlo |
|---|---|---|
| `python tools/gen-handlers.py` | `Griffin.Common.Proto/Handlers/PacketHandlerBases.g.cs` (las clases base de los handlers de cada petición) | al cambiar `Griffin.Common.Proto/protos/GF2_deobfuscated.proto` |
| `python tools/gen-achievements.py` | `Griffin.GameServer/Services/AchievementConditions.g.cs` (condiciones de los logros que desbloquean capítulos, poses y árboles de talento) | al cambiar de versión de las tablas del cliente |
| `python tools/gen-cores.py` | `Griffin.GameServer/Services/RemouldingCores.g.cs` (núcleo de remodelación de cada personaje) | al cambiar de versión de las tablas del cliente |

Los dos últimos leen las tablas de `Griffin/bin/Release/net8.0/Resources/Tables` (se pueden indicar otra carpeta como argumento). `tools/tabledeep.py` es el lector de tablas que usan; también sirve para inspeccionar una tabla a mano.

## Problemas frecuentes

- **No compila / faltan paquetes**: ejecuta `dotnet restore Griffin.sln` y vuelve a compilar.
- **«datos del cliente anormales» al entrar**: PostgreSQL no responde o los datos de la cuenta no cargan; comprueba que el servicio está activo y que la cadena de conexión es correcta.
- **Android: «当前客户端版本已过时» (cliente desactualizado) o «解析错误提示» (error de análisis)**: el APK no es el 4.0.5137, o el servidor no lo reconoce como Android; ajusta las variables `GRIFFIN_ANDROID_*` a la versión de tu APK.
- **Android: «下载时发生未知错误» (error al descargar)**: el teléfono no tiene salida a internet para bajar los recursos del CDN oficial.
- **El juego no conecta**: abre `griffin_launcher.exe` como administrador con el servidor ya encendido y comprueba que nada más usa los puertos 80, 443 y 7001.

## Créditos

- **[NTRSimulator](https://github.com/raphaeIl/NTRSimulator)** (raphaeIl): Griffin está inspirado en este proyecto y parte de su base.

## Licencia

[MIT](LICENSE).
