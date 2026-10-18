//! Embeds the app icon and the version information in the launcher, so it looks like Wol in
//! the Explorer and the taskbar.

fn main() {
    let icon = "../src/Wol.Desktop/Assets/wol-gui.ico";
    println!("cargo:rerun-if-changed={icon}");

    let mut resource = winresource::WindowsResource::new();
    resource
        .set_icon(icon)
        .set("FileDescription", "Wol")
        .set("ProductName", "Wol")
        .set("LegalCopyright", "Sébastien Faut");
    resource
        .compile()
        .expect("cannot embed the icon and version information");
}
