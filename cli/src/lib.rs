//! Wol: monitors remote machines and wakes them up with Wake-on-LAN magic packets.
//!
//! The library holds all the logic; the `wol` binary is a thin entry point over [`cli::run`].

pub mod cli;
pub mod config;
pub mod error;
pub mod mac;
pub mod magic;
