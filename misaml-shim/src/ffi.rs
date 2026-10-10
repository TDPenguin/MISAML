// every raw ptr and C ABI sige the shim is here.
// nothing outside this file should call libc directly or
// transmute a function pointer.

use std::ffi::{c_char, c_int, c_uint, c_void};

// function pointer types for symbols exported by the dynamic linker and
// CoreCLR, these match the native CoreCLR ABI
pub type DlsymFn = unsafe extern "C" fn(*mut c_void, *const c_char) -> *mut c_void;

pub type CoreclrInitializeFn = unsafe extern "C" fn(
    *const c_char,        // exePath
    *const c_char,        // appDomainFriendlyName
    c_int,                // propertyCount
    *const *const c_char, // propertyKeys
    *const *const c_char, // propertyValues
    *mut *mut c_void,     // hostHandle (out)
    *mut c_uint,          // domainId (out)
) -> c_int;

pub type CoreclrCreateDelegateFn = unsafe extern "C" fn(
    *mut c_void,      // hostHandle
    c_uint,           // domainId
    *const c_char,    // entryPointAssemblyName
    *const c_char,    // entryPointTypeName
    *const c_char,    // entryPointMethodName
    *mut *mut c_void, // delegate (out)
) -> c_int;

// signature of the managed entry point exposed through CoreCLR native callable delegate
pub type BootstrapFn = unsafe extern "C" fn();

pub type Open64Fn = unsafe extern "C" fn(*const c_char, c_int, ...) -> c_int;

// resolve the real, unhooked dlsym, bypassing our own override
//
// have to use dlsym() with an explicit glibc symbol ver here, plain
// dlsym(RTLD_NEXT, "dlsym") resolves back into our own exported dlsym since
// that's what the shim relies on, obviously, and we don't want inf recursion.
pub fn real_dlsym() -> Option<DlsymFn> {
    let ptr = unsafe { libc::dlvsym(libc::RTLD_NEXT, c"dlsym".as_ptr(), c"GLIBC_2.2.5".as_ptr()) };

    if ptr.is_null() {
        return None;
    }

    // safe: dlvsym found w/ name "dlsym" at this glibc version,
    // signature's stable ABI on every glibc that matters here
    Some(unsafe { std::mem::transmute::<*mut c_void, DlsymFn>(ptr) })
}

// resolve a symbol off a specific lib handle, using the real dlsym
pub unsafe fn resolve(handle: *mut c_void, name: &std::ffi::CStr) -> *mut c_void {
    match real_dlsym() {
        Some(rd) => unsafe { rd(handle, name.as_ptr()) },
        None => std::ptr::null_mut(),
    }
}
