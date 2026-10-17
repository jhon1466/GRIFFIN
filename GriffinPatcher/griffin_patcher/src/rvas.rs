//! RVAs from IDA GameAssembly.dll (image base 0x180000000).
//! Dummy-DLL offsets from 3.0.3531 do not match this binary.

pub const CLIENT_VERSION: &str = "4.0.5136";

/// UnityEngineInternal.WebRequestUtils::MakeInitialUrl(string, string)
/// Static — target URL in RCX.
pub const UNITY_WEB_REQUEST_SET_URL: usize = 0x012B_9150;

/// UnityEngine.Networking.UnityWebRequest::SetRequestHeader(string, string)
pub const UNITY_WEB_REQUEST_SET_REQUEST_HEADER: usize = 0x012B_1100;

/// UnityEngine.Networking.CertificateHandler::ValidateCertificateNative(byte[])
pub const CERTIFICATE_HANDLER_VALIDATE_NATIVE: usize = 0x012A_C990;
