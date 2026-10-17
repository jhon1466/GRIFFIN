use anyhow::Result;
use ilhook::x64::Registers;

use super::{Gf2Context, Gf2Module, ModuleType};
use crate::rvas;

pub struct Tls;

impl Gf2Module for Gf2Context<Tls> {
    unsafe fn init(&mut self) -> Result<()> {
        self.interceptor.replace(
            self.assembly_base + rvas::CERTIFICATE_HANDLER_VALIDATE_NATIVE,
            on_validate_certificate_native,
        )?;

        let cert_verify = self.get_export("crypt32.dll", "CertVerifyCertificateChainPolicy")?;
        self.interceptor.replace(cert_verify, on_cert_verify_chain_policy)?;

        println!("[tls] certificate validation bypass attached");
        Ok(())
    }

    fn get_module_type(&self) -> ModuleType {
        ModuleType::Tls
    }
}

/// Unity custom certificate handler path — always accept.
unsafe extern "win64" fn on_validate_certificate_native(
    _reg: *mut Registers,
    _ori_func_ptr: usize,
    _user_data: usize,
) -> usize {
    1
}

/// Windows Schannel / system store path — always accept.
unsafe extern "win64" fn on_cert_verify_chain_policy(
    _reg: *mut Registers,
    _ori_func_ptr: usize,
    _user_data: usize,
) -> usize {
    1
}
