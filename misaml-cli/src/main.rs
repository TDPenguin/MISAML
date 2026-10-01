use std::{
    os::unix::process::CommandExt,
    path::{Path, PathBuf},
    process::Command,
};

use anyhow::{anyhow, bail, Context, Result};

const MNEMONIMOV_APP_ID: u32 = 3854110;
const BUILD_ID: &str = env!("CARGO_PKG_VERSION");
const RUNTIME_VERSION: &str = "8.0.30";
#[cfg(windows)] const RUNTIME_PACKAGE: &str = "win-x64";
#[cfg(not(windows))] const RUNTIME_PACKAGE: &str = "linux-x64";
// hardcoded is perhaps not the best approach

macro_rules! debug_log {
    ($($arg:tt)*) => {
        if std::env::var_os("MISAML_DEBUG").is_some() {
            eprintln!("[misaml {BUILD_ID}] {}", format_args!($($arg)*));
        }
    };
}

macro_rules! log {
    ($($arg:tt)*) => {
        eprintln!("[misaml {BUILD_ID}] {}", format_args!($($arg)*));
    };
}

fn main() -> Result<()> {
    let args: Vec<String> = std::env::args().collect();

    match args.get(1).map(String::as_str) {
        Some("inspect-pck") => cmd_inspect_pck(args.get(2)),
        Some("extract-pck") => cmd_extract_pck(args.get(2), args.get(3)),
        _ => cmd_launch(),
    }
}

fn cmd_launch() -> Result<()> {
    let game_dir = find_game_dir()
        .context("could not find Mnemonimov, is it installed via Steam?")?;
    log!("Found Mnemonimov at: {}", game_dir.display());

    let exe_path = game_dir.join("mnemonimov.x86_64");
    if !exe_path.is_file() {
        bail!("mnemonimov.x86_64 not found at {}", exe_path.display());
    }

    let misaml_dir = own_install_dir()
        .context("could not find misaml-cli directory")?;
    let payload_dir = misaml_dir.join("MISAML");

    ensure_runtime(&payload_dir)?;

    let shim_path = payload_dir.join("libmisaml_shim.so");
    let framework_dir = find_framework_dir(&payload_dir)
        .context("could not find bundled .NET runtime")?;

    require_exists(&shim_path, "shim library")?;
    require_exists(&payload_dir.join("MISAML.Core.dll"), "MISAML.Core.dll")?;

    log!("Launching Mnemonimov with MISAML...");
    log!("  shim: {}", shim_path.display());
    log!("  core: {}", payload_dir.display());
    log!("  framework: {}", framework_dir.display());

    let err = Command::new(&exe_path)
        .current_dir(&game_dir)
        .env("LD_PRELOAD", &shim_path)
        .env("MISAML_CORE_DIR", &payload_dir)
        .env("MISAML_FRAMEWORK_DIR", &framework_dir)
        .exec();

    Err(anyhow!(err).context(format!("failed to launch {}", exe_path.display())))
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
    let root = root.join("runtime/shared/Microsoft.NETCore.App");

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

fn cmd_inspect_pck(path: Option<&String>) -> Result<()> {
    let path = path.ok_or_else(|| anyhow!("usage: misaml-cli inspect-pck <path-to.pck>"))?;
    let pck = misaml_pck::open(path)?;

    log!(
        "Godot {}.{}.{} - profile: {} - {} entries",
        pck.header.godot_version.0,
        pck.header.godot_version.1,
        pck.header.godot_version.2,
        pck.matched_profile,
        pck.entries.len(),
    );
    for entry in &pck.entries {
        println!("{:#010x} {:8} flags={:#x} {}", entry.abs_off, entry.size, entry.flags, entry.path);
    }
    Ok(())
}

fn cmd_extract_pck(path: Option<&String>, out_dir: Option<&String>) -> Result<()> {
    let path = path.ok_or_else(|| anyhow!("usage: misaml-cli extract-pck <path-to.pck> [out_dir]"))?;
    let out_dir = PathBuf::from(out_dir.map(String::as_str).unwrap_or("extracted"));

    let pck = misaml_pck::open(path)?;
    let profile = pck.matched_profile_ref()
        .context("matched_profile has no corresponding Profile")?;
    let key = profile.eff_key();

    let mut ok = 0;
    let mut failed = 0;
    
    for entry in &pck.entries {
        let out_path = out_dir.join(&entry.path);
        if let Some(parent) = out_path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        match misaml_pck::extract(Path::new(path), entry, &key) {
            Ok(data) => { std::fs::write(&out_path, data)?; ok += 1; }
            Err(e) => { eprintln!("failed to extract {}: {e}", entry.path); failed += 1; }
        }
    }
    log!("Extracted {ok} files, {failed} failed.");
    Ok(())
}

fn ensure_runtime(payload_dir: &Path) -> Result<()> {
    let marker = payload_dir
        .join("runtime/shared/Microsoft.NETCore.App")
        .join(RUNTIME_VERSION);

    if marker.is_dir() {
        debug_log!("Runtime {RUNTIME_VERSION} already present!");
        return Ok(());
    }  

    log!("Runtime {RUNTIME_VERSION} not found, downloading...");

    let runtime_dir = payload_dir.join("runtime");
    std::fs::create_dir_all(&runtime_dir)
        .context("could not create runtime directory")?;

    let url = format!(
        "https://builds.dotnet.microsoft.com/dotnet/Runtime/{RUNTIME_VERSION}/dotnet-runtime-{RUNTIME_VERSION}-{RUNTIME_PACKAGE}"
    );

    log!("Downloading from {url}...");

    let mut response = ureq::get(&url)
        .call()
        .with_context(|| format!("failed to download .NET runtime from {url}"))?;

    tar::Archive::new(flate2::read::GzDecoder::new(
        response.body_mut().as_reader(),
    ))
    .unpack(&runtime_dir)
    .context("failed to extract .NET runtime archive")?;

    if !marker.is_dir() {
        bail!(
            "Runtime download completed but expected folder {} is missing, the download may be corrupt!",
            marker.display()
        );
    }

    log!("Runtime {RUNTIME_VERSION} ready!");
    Ok(())
}