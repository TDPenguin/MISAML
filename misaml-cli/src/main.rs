use std::{
    os::unix::process::CommandExt,
    path::{Path, PathBuf},
    process::Command,
};

use anyhow::{anyhow, bail, Context, Result};

const MNEMONIMOV_APP_ID: u32 = 3854110;
const BUILD_ID: &str = env!("CARGO_PKG_VERSION");

macro_rules! debug_log {
    ($($arg:tt)*) => {
        if std::env::var_os("MISAML_DEBUG").is_some() {
            eprintln!("[misaml-cli {BUILD_ID}] {}", format_args!($($arg)*));
        }
    };
}

macro_rules! log {
    ($($arg:tt)*) => {
        eprintln!("[misaml-cli {BUILD_ID}] {}", format_args!($($arg)*));
    };
}

fn main() -> Result<()> {
    let game_dir = find_game_dir()
        .context("could not find Mnemonimov")?;

    log!("Found Mnemonimov at: {}", game_dir.display());

    let exe_path = game_dir.join("mnemonimov.x86_64");
    if !exe_path.is_file() {
        bail!("mnemonimov.x86_64 not found at {}", exe_path.display());
    }

    let misaml_dir = own_install_dir()
        .context("could not find misaml-cli directory")?;

    let shim_path = misaml_dir.join("libmisaml_shim.so");
    let core_dll = misaml_dir.join("MISAML.Core.dll");
    let harmony_dll = misaml_dir.join("0Harmony.dll");

    let framework_dir = find_framework_dir(&misaml_dir)
        .context("could not find bundled .NET runtime")?;

    require_exists(&shim_path, "libmisaml_shim.so")?;
    require_exists(&core_dll, "MISAML.Core.dll")?;
    require_exists(&harmony_dll, "0Harmony.dll")?;

    log!("Launching Mnemonimov with MISAML...");
    log!("  shim: {}", shim_path.display());
    log!("  core: {}", misaml_dir.display());
    log!("  framework: {}", framework_dir.display());

    let err = Command::new(&exe_path)
        .current_dir(&game_dir)
        .env("LD_PRELOAD", &shim_path)
        .env("MISAML_CORE_DIR", &misaml_dir)
        .env("MISAML_FRAMEWORK_DIR", &framework_dir)
        .exec();

    Err(err).context(format!("failed to launch {}", exe_path.display()))
}

fn find_game_dir() -> Result<PathBuf> {
    let steam_dir = steamlocate::SteamDir::locate()
        .context("could not locate Steam")?;

    let (app, library) = steam_dir
        .find_app(MNEMONIMOV_APP_ID)?
        .ok_or_else(|| {
            anyhow!(
                "Mnemonimov (app id {MNEMONIMOV_APP_ID}) is not installed"
            )
        })?;

    Ok(library.resolve_app_dir(&app))
}

fn own_install_dir() -> Result<PathBuf> {
    std::env::current_exe()?
        .parent()
        .map(Path::to_path_buf)
        .ok_or_else(|| anyhow!("could not get executable directory"))
}

fn find_framework_dir(root: &Path) -> Result<PathBuf> {
    let root = root.join("core/shared/Microsoft.NETCore.App");

    let mut versions = std::fs::read_dir(&root)?
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| path.is_dir());

    let version = versions
        .next()
        .ok_or_else(|| anyhow!("no .NET runtime found in {}", root.display()))?;

    if versions.next().is_some() {
        bail!("multiple .NET runtimes found in {}", root.display());
    }

    Ok(version)
}

fn require_exists(path: &Path, name: &str) -> Result<()> {
    if !path.exists() {
        bail!("{} not found at {}", name, path.display());
    }

    Ok(())
}