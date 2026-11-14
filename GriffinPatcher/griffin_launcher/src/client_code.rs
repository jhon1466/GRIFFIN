//! Runtime-only patch of the client hot-update code (HybridCLR `Codes/Assembly-CSharp.dll.bytes`).
//!
//! The delta in `client/Assembly-CSharp.bsdiff` turns the official file of the client it was made for into the Griffin one
//! (the old event panels: LuaBindingNew read from the parent, click fallback of the mini game buttons). The launcher applies it
//! only while the game started by it runs and restores the official file when the game closes, so the official game is never
//! changed. A file of any other client version is left alone (the delta has to be rebuilt for it).
use std::fs;
use std::io::Cursor;
use std::path::{Path, PathBuf};

use sha2::{Digest, Sha256};

const DELTA: &[u8] = include_bytes!("../client/Assembly-CSharp.bsdiff");
const OFFICIAL_SHA256: &str = "c2109460d95b34a8a1ece991775314e5d36c63ef4daf1140681b210c28225cf1";
const PATCHED_SHA256: &str = "3038963efa6eaad65ca26b8808b2e5cba1cbf880e623fa524d9598e9ed39a19b";
const BACKUP_SUFFIX: &str = ".griffin-official";

fn sha256(data: &[u8]) -> String {
    Sha256::digest(data).iter().map(|b| format!("{b:02x}")).collect()
}

fn backup_of(file: &Path) -> PathBuf {
    let mut name = file.as_os_str().to_owned();
    name.push(BACKUP_SUFFIX);
    PathBuf::from(name)
}

/// Every downloaded client version: GF2_Exilium_Data/LocalCache/Data/ClientRes_Windows/<version>/Codes/Assembly-CSharp.dll.bytes
fn code_files(game_dir: &Path) -> Vec<PathBuf> {
    let root = game_dir.join("GF2_Exilium_Data").join("LocalCache").join("Data").join("ClientRes_Windows");
    let Ok(dirs) = fs::read_dir(&root) else { return Vec::new() };
    dirs.flatten()
        .map(|d| d.path().join("Codes").join("Assembly-CSharp.dll.bytes"))
        .filter(|f| f.is_file())
        .collect()
}

/// Puts the patched code in place. Returns the files to restore when the game closes.
pub fn apply(game_dir: &Path) -> Vec<PathBuf> {
    let mut applied = Vec::new();
    for file in code_files(game_dir) {
        let Ok(data) = fs::read(&file) else { continue };
        let hash = sha256(&data);
        let backup = backup_of(&file);
        if hash == PATCHED_SHA256 {
            if backup.is_file() {
                applied.push(file);
            }
            continue;
        }
        if hash != OFFICIAL_SHA256 {
            println!("Client code {} is another version: started without the Griffin client patch.", file.display());
            continue;
        }
        let mut patched = Vec::new();
        let ok = qbsdiff::Bspatch::new(DELTA)
            .and_then(|p| p.apply(&data, Cursor::new(&mut patched)))
            .is_ok()
            && sha256(&patched) == PATCHED_SHA256;
        if !ok {
            eprintln!("Client patch could not be built for {}", file.display());
            continue;
        }
        if fs::write(&backup, &data).is_err() || fs::write(&file, &patched).is_err() {
            eprintln!("Cannot write {} (run as administrator): started without the client patch.", file.display());
            let _ = fs::write(&file, &data);
            let _ = fs::remove_file(&backup);
            continue;
        }
        println!("Griffin client patch applied to {}", file.display());
        applied.push(file);
    }
    applied
}

/// Puts the official code back.
pub fn restore(files: &[PathBuf]) {
    for file in files {
        let backup = backup_of(file);
        match fs::read(&backup) {
            Ok(data) if sha256(&data) == OFFICIAL_SHA256 => {
                if fs::write(file, &data).is_ok() {
                    let _ = fs::remove_file(&backup);
                    println!("Official client code restored: {}", file.display());
                }
            }
            _ => eprintln!("No official backup for {}", file.display()),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Needs the official file of the client the delta was made for (GRIFFIN_OFFICIAL_CODE).
    #[test]
    fn apply_then_restore() {
        let Ok(official) = std::env::var("GRIFFIN_OFFICIAL_CODE") else { return };
        let game = std::env::temp_dir().join("griffin-launcher-test");
        let codes = game.join("GF2_Exilium_Data/LocalCache/Data/ClientRes_Windows/4.0.5136/Codes");
        let _ = fs::remove_dir_all(&game);
        fs::create_dir_all(&codes).unwrap();
        let file = codes.join("Assembly-CSharp.dll.bytes");
        fs::copy(official, &file).unwrap();
        let applied = apply(&game);
        assert_eq!(applied.len(), 1);
        assert_eq!(sha256(&fs::read(&file).unwrap()), PATCHED_SHA256);
        assert_eq!(apply(&game).len(), 1, "a leftover patched file is still restored");
        restore(&applied);
        assert_eq!(sha256(&fs::read(&file).unwrap()), OFFICIAL_SHA256);
        assert!(!backup_of(&file).exists());
        let _ = fs::remove_dir_all(&game);
    }
}
