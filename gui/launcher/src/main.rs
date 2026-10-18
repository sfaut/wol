//! Launcher of the Wol desktop app.
//!
//! A release keeps its executables at the root (this launcher, named `wol-gui.exe`, and the
//! `wol.exe` CLI) and the GUI with the Windows App SDK, about 80 files, in the `runtime`
//! subfolder. The launcher starts `runtime\wol-gui.exe` with the same arguments and exits; the
//! GUI handles everything else, including handing over to an instance already running.

#![windows_subsystem = "windows"]

use std::env;
use std::ffi::{OsStr, c_void};
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};
use std::process::Command;

/// Subfolder holding the GUI.
const RUNTIME: &str = "runtime";

/// `MB_ICONERROR`: the message box shows an error icon.
const MB_ICONERROR: u32 = 0x10;

/// Primary language identifier of French (`LANG_FRENCH`).
const LANG_FRENCH: u16 = 0x0C;

#[link(name = "user32")]
unsafe extern "system" {
    /// Lets a process bring its window to the foreground.
    fn AllowSetForegroundWindow(process_id: u32) -> i32;

    /// Shows a message box.
    fn MessageBoxW(window: *mut c_void, text: *const u16, caption: *const u16, kind: u32) -> i32;
}

#[link(name = "kernel32")]
unsafe extern "system" {
    /// Language of the user interface.
    fn GetUserDefaultUILanguage() -> u16;
}

fn main() {
    let gui = match gui_path() {
        Ok(gui) => gui,
        Err(error) => return fail(&error.to_string()),
    };

    match Command::new(&gui).args(env::args_os().skip(1)).spawn() {
        // The launcher got the foreground from the shell; pass it on to the GUI window.
        Ok(child) => unsafe {
            AllowSetForegroundWindow(child.id());
        },
        Err(error) => fail(&format!("{}\n{error}", gui.display())),
    }
}

/// Path of the GUI executable: `runtime\` next to the launcher, under the launcher's own name.
fn gui_path() -> std::io::Result<PathBuf> {
    let launcher = env::current_exe()?;
    let folder = launcher.parent().unwrap_or(Path::new("."));
    let name = launcher.file_name().unwrap_or(OsStr::new("wol-gui.exe"));
    Ok(folder.join(RUNTIME).join(name))
}

/// Tells that Wol cannot start, in French or in English, with the reason.
fn fail(reason: &str) {
    let french = unsafe { GetUserDefaultUILanguage() } & 0x3FF == LANG_FRENCH;
    let text = if french {
        format!("Wol ne peut pas démarrer.\n\n{reason}")
    } else {
        format!("Wol cannot start.\n\n{reason}")
    };
    unsafe {
        MessageBoxW(
            std::ptr::null_mut(),
            wide(&text).as_ptr(),
            wide("Wol").as_ptr(),
            MB_ICONERROR,
        );
    }
}

/// Converts a string to a null-terminated UTF-16 string for the Windows API.
fn wide(text: &str) -> Vec<u16> {
    OsStr::new(text).encode_wide().chain([0]).collect()
}
