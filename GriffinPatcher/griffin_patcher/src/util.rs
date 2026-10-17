use std::{ffi::c_void, sync::LazyLock};

use windows::{
    core::s,
    Win32::System::{
        LibraryLoader::{GetModuleHandleA, GetProcAddress},
        Memory,
    },
};

pub static GAME_ASSEMBLY_BASE: LazyLock<usize> =
    LazyLock::new(|| unsafe { GetModuleHandleA(s!("GameAssembly.dll")).unwrap().0 as usize });

const MAX_IL2CPP_STRING_LEN: u32 = 65536;

/// This GameAssembly obfuscates IL2CPP exports (`il2cpp_string_new` is gone), so
/// new managed strings cannot be allocated by name. Official SDK hosts are always
/// longer than `127.0.0.1`, so the existing UTF-16 buffer can be rewritten in place.
pub unsafe fn overwrite_csharp_string(s: usize, new_text: &str) -> bool {
    if s == 0 {
        return false;
    }

    let utf16: Vec<u16> = new_text.encode_utf16().collect();
    if utf16.len() > MAX_IL2CPP_STRING_LEN as usize {
        return false;
    }

    let len_ptr = s.wrapping_add(16) as *mut u32;
    let old_len = *len_ptr as usize;
    if old_len == 0 || old_len > MAX_IL2CPP_STRING_LEN as usize || utf16.len() > old_len {
        return false;
    }

    let chars = s.wrapping_add(20) as *mut u16;
    let bytes = (utf16.len() * 2) + 6;
    let mut old_prot = Memory::PAGE_READWRITE;
    let _ = Memory::VirtualProtect(
        len_ptr as *mut c_void,
        bytes,
        Memory::PAGE_READWRITE,
        &mut old_prot,
    );

    *len_ptr = utf16.len() as u32;
    std::ptr::copy_nonoverlapping(utf16.as_ptr(), chars, utf16.len());
    if utf16.len() < old_len {
        *chars.add(utf16.len()) = 0;
    }

    true
}

#[inline]
pub unsafe fn read_csharp_string(s: usize) -> String {
    if s == 0 {
        return String::new();
    }

    let str_length = *(s.wrapping_add(16) as *const u32);
    if str_length == 0 || str_length > MAX_IL2CPP_STRING_LEN {
        return String::new();
    }

    let str_ptr = s.wrapping_add(20) as *const u16;
    String::from_utf16_lossy(std::slice::from_raw_parts(str_ptr, str_length as usize))
}

/// Returns true when the URL should be redirected to the local Griffin SDK server.
pub fn should_redirect_url(url: &str) -> bool {
    let lower = url.to_ascii_lowercase();
    lower.contains("sunborngame.com") || lower.contains("exiliumgf.com")
}

/// Rewrite official GF2 HTTP(S) endpoints to the local SDK server (Kestrel :443).
pub fn redirect_url(url: &str) -> Option<String> {
    if !should_redirect_url(url) {
        return None;
    }

    let mut new_url = String::from("https://127.0.0.1");
    for segment in url.split('/').skip(3) {
        new_url.push('/');
        new_url.push_str(segment);
    }

    Some(new_url)
}

pub unsafe fn disable_memory_protection() {
    let ntdll = GetModuleHandleA(s!("ntdll.dll")).unwrap();
    let proc_addr = GetProcAddress(ntdll, s!("NtProtectVirtualMemory")).unwrap();

    let nt_func = if GetProcAddress(ntdll, s!("wine_get_version")).is_some() {
        GetProcAddress(ntdll, s!("NtPulseEvent")).unwrap()
    } else {
        GetProcAddress(ntdll, s!("NtQuerySection")).unwrap()
    };

    let mut prot = Memory::PAGE_EXECUTE_READWRITE;
    Memory::VirtualProtect(proc_addr as *const usize as *mut c_void, 1, prot, &mut prot).unwrap();

    let routine = nt_func as *mut u32;
    let routine_val = *(routine as *const usize);

    let lower_bits_mask = !(0xFFu64 << 32);
    let lower_bits = routine_val & lower_bits_mask as usize;

    let offset_val = *((routine as usize + 4) as *const u32);
    let upper_bits = (offset_val.wrapping_sub(1) as usize) << 32;

    let result = lower_bits | upper_bits;

    *(proc_addr as *mut usize) = result;
    Memory::VirtualProtect(proc_addr as *const usize as *mut c_void, 1, prot, &mut prot).unwrap();
}
