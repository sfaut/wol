//! Wol command-line interface: monitors remote machines and wakes them up
//! with Wake-on-LAN magic packets.

use std::process::ExitCode;

fn main() -> ExitCode {
    wol::cli::run(std::env::args_os())
}
