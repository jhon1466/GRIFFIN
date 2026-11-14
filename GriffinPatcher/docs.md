# Griffin Patcher — notas técnicas

Inyector en tiempo de ejecución + hooks sobre `GameAssembly.dll` para **GF2 Exilium**.

Redirige el tráfico HTTP del Mica SDK de `*.sunborngame.com` (y `exiliumgf.com`) a `https://127.0.0.1`, de modo que el cliente habla con el servidor SDK de Griffin en el puerto 443 sin proxy.

## Requisitos

- [Rust](https://rustlang.org/tools/install/) (stable)
- GF2 Exilium (probado con el cliente **4.0.5136**)
- Griffin en marcha con Kestrel escuchando en `https://127.0.0.1:443`

## Compilar

```bash
cargo build --release
```

Salida:

- `target/release/griffin_patcher.dll` — biblioteca inyectada con los hooks
- `target/release/griffin_launcher.exe` — suspende `GF2_Exilium.exe`, inyecta la DLL y lo reanuda

## Qué se parchea

| Offset nativo (4.0.5136) | Destino | Efecto |
|--------------------------|---------|--------|
| `GameAssembly + 0x12B9150` | `WebRequestUtils.MakeInitialUrl` | Reescribe la URL antes de la petición |
| `GameAssembly + 0x12B1100` | `UnityWebRequest.SetRequestHeader` | Fuerza `Host: 127.0.0.1` |
| `GameAssembly + 0x12AC990` | `CertificateHandler.ValidateCertificateNative` | Acepta el certificado de desarrollo de Kestrel |
| `crypt32.dll!CertVerifyCertificateChainPolicy` | Pila TLS de Windows | Acepta el certificado local no confiable |

Griffin usa **HTTPS en el 443**, por eso el parche omite la validación del certificado de desarrollo de ASP.NET en `127.0.0.1`.

Las direcciones (RVA) están en `griffin_patcher/src/rvas.rs`. Hay que volver a extraerlas tras una actualización del juego.

## Notas

- Solo parchea **`GF2_Exilium.exe`**; `PCLauncher.exe` no se toca.
- El TCP del juego (puerta → `127.0.0.1:7001`) llega por `/zone/gatelist` de Griffin; no hace falta hook de sockets.

## Estructura

```
GriffinPatcher/
├── griffin_patcher/    # cdylib inyectada en el proceso del juego
├── griffin_launcher/   # inyector CreateProcess + LoadLibrary
└── Cargo.toml          # workspace
```

## Parche del código del cliente (eventos antiguos)

`griffin_launcher.exe` lleva dentro `griffin_launcher/client/Assembly-CSharp.bsdiff`, un delta del código hot-update (HybridCLR) del cliente **4.0.5136**: los paneles de eventos antiguos leen su `LuaBindingNew` del padre y los botones de minijuegos tienen clic de respaldo.

- Al arrancar, si `LocalCache/Data/ClientRes_Windows/<versión>/Codes/Assembly-CSharp.dll.bytes` es el oficial de esa versión, guarda una copia `.griffin-official` y pone el parcheado.
- Al cerrarse el juego vuelve a poner el oficial: el juego oficial no queda modificado (deja la ventana del launcher abierta mientras juegas).
- Otra versión del cliente se deja como está; hay que regenerar el delta para ella.
- El launcher pide administrador para poder escribir en la carpeta del juego.
