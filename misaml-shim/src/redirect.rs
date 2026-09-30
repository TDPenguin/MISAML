// redirects open64() calls for the games trimmed framework dlls to the
// untrimmed copies MISAML ships w/
//
// narrow on purpose, a file only gets redirected if ALL of these are true:
// 1. its dir is exactly the games own runtime dir
//    (APP_CONTEXT_BASE_DIRECTORY, grabbed once during coreclr_initialize)
// 2. its filename is one we're actually replacing
// 3. the replacement file exists

use std::{ffi::OsStr, os::unix::ffi::OsStrExt, path::{Path, PathBuf}, sync::OnceLock};
use std::ffi::{c_char, CStr, CString, OsString};

use crate::ffi::Open64Fn;

// the game's own runtime dir, grabbed once from APP_CONTEXT_BASE_DIRECTORY
// during coreclr_initialize. only opens inside this exact dir are ever
// even considered for redirection
static GAME_RUNTIME_DIR: OnceLock<PathBuf> = OnceLock::new();

// (redir from filename -> full replacement path), built on first
// use instead of rereading the framework dir every open64 call
static REDIRECTS: OnceLock<std::collections::HashMap<OsString, PathBuf>> = OnceLock::new();

pub fn set_game_runtime_dir(dir: PathBuf) {
    let _ = GAME_RUNTIME_DIR.set(dir);
}

fn redirects() -> &'static std::collections::HashMap<OsString, PathBuf> {
    REDIRECTS.get_or_init(|| {
        // build redirects from the framework directory
        let Some(framework_dir) = std::env::var_os("MISAML_FRAMEWORK_DIR") else {
            return std::collections::HashMap::new();
        };

        let Ok(entries) = std::fs::read_dir(framework_dir) else {
            return std::collections::HashMap::new();
        };

        let mut map = std::collections::HashMap::new();

        for entry in entries.filter_map(Result::ok) {
            let path = entry.path();

            if !path.extension().is_some_and(|ext| ext.eq_ignore_ascii_case("dll")) {
                continue;
            }

            let Some(name) = path.file_name() else {
                continue;
            };

            map.insert(name.to_os_string(), path);
        }

        map
    })
}

// finds if "requested" should get redirected, and to where.
// lookup against the configured game runtime dir and redirect cache
fn redirect_target(requested: &Path) -> Option<PathBuf> {
    let game_dir = GAME_RUNTIME_DIR.get()?;

    // only redirect requests directly from the game runtime directory
    if requested.parent()? != game_dir.as_path() {
        return None;
    }

    let name = requested.file_name()?;

    redirects().get(name).cloned()
}

// only ever call this as the installed open64 hook, not directly
pub unsafe fn hook(
    pathname: *const c_char,
    flags: libc::c_int,
    real: Open64Fn
) -> libc::c_int {
    if pathname.is_null() || GAME_RUNTIME_DIR.get().is_none() {
        return unsafe { call_real(real, pathname, flags) };
    }

    let requested = unsafe { CStr::from_ptr(pathname) };
    let requested_path = Path::new(OsStr::from_bytes(requested.to_bytes()));

    match redirect_target(requested_path) {
        Some(replacement) => {
            if std::env::var_os("MISAML_DEBUG").is_some() {
                eprintln!(
                    "[misaml-shim] redirecting {} -> {}",
                    requested_path.display(),
                    replacement.display()
                );
            }

            let replacement = replacement.as_os_str();

            let Ok(cstr) = CString::new(replacement.as_encoded_bytes()) else {
                return unsafe { call_real(real, pathname, flags) };
            };

            unsafe { call_real(real, cstr.as_ptr(), flags) }
        }

        None => unsafe { call_real(real, pathname, flags) },
    }
}

// O_CREAT adds a variadic mode arg that Open64Fn can't represent
// safely in Rust, so we use the raw syscall here like the original
unsafe fn call_real(
    real: Open64Fn,
    pathname: *const c_char,
    flags: libc::c_int,
) -> libc::c_int {
    if flags & libc::O_CREAT != 0 {
        return unsafe {
            libc::syscall(
                libc::SYS_openat,
                libc::AT_FDCWD,
                pathname,
                flags,
                0o666,
            ) as libc::c_int
        };
    }

    unsafe { real(pathname, flags) }
}