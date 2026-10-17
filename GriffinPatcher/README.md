# Griffin Patcher

Redirige el HTTP del Mica SDK del cliente de GF2 Exilium (`*.sunborngame.com`) a `https://127.0.0.1` (Griffin en el puerto 443), así que no hace falta mitmproxy.

### Cómo usarlo
1. Descarga `griffin_patcher.dll` y `griffin_launcher.exe` de la sección **Releases** del repositorio y colócalos en `GF2Exilium\GF2 Game\` (o usa `scripts/install-griffin-patcher.ps1`, que comprueba su SHA-256 y los copia).
2. Enciende Griffin y ejecuta `griffin_launcher.exe` (**COMO ADMINISTRADOR**).

### ¿Quieres compilarlo tú?
- Instala [Rust](https://rust-lang.org/tools/install/)
- Ejecuta `cargo build --release` en esta carpeta (`GriffinPatcher`)
- Los archivos quedan en `target/release/`; actualiza los SHA-256 de `scripts/install-griffin-patcher.ps1`

### Qué parchea
Ver `docs.md`. Está hecho para el cliente **4.0.5136**; si el juego se actualiza hay que volver a sacar las direcciones (`griffin_patcher/src/rvas.rs`).
