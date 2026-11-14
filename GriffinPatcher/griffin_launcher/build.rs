fn main() {
    // The game folder is usually under Program Files: the launcher must be able to swap the client code file there.
    println!("cargo:rustc-link-arg-bin=griffin_launcher=/MANIFEST:EMBED");
    println!("cargo:rustc-link-arg-bin=griffin_launcher=/MANIFESTUAC:level='requireAdministrator'");
    println!("cargo:rerun-if-changed=client/Assembly-CSharp.bsdiff");
}
