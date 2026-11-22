# Security Policy

## Supported Versions

CryoTray actively maintains security patches for the following versions:

| Version | Supported | Notes |
| :--- | :---: | :--- |
| `>= 0.0.100` / `main` | :white_check_mark: | Actively supported (current baseline `0.0.103`; incrementing `0.0.104`, `0.0.105`, etc.) |
| `< 0.0.100` | :x: | Unsupported / unreleased builds |

We strongly recommend always running the latest official release available from the [CryoTray Releases](https://github.com/hackerlemonaru/CryoTray/releases) page.

---

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues, discussions, or pull requests.**

If you discover a security vulnerability or potential exploit in CryoTray, report it privately through one of the following channels:

1. **GitHub Private Vulnerability Reporting (Preferred)**:
   - Navigate to the [CryoTray Security Advisories](https://github.com/hackerlemonaru/CryoTray/security/advisories/new) page.
   - Click **Report a vulnerability** and submit the advisory form.
2. **Confidential Email**:
   - Send an email to the project maintainer: **Aryan** at `renu8059r@gmail.com`.
   - Please include "[CryoTray Security]" in the subject line.

### What to Include in Your Report

To help us investigate and resolve the issue quickly, please provide:
- A clear description of the vulnerability and its potential impact.
- Step-by-step instructions or proof-of-concept (PoC) demonstrating how to reproduce the issue.
- Operating system version and architecture (e.g., Windows 11 x64, build 26100).
- Any specific configuration or permissions needed to trigger the vulnerability.

### Response SLA & Disclosure Process

- **Acknowledgment**: We aim to acknowledge receipt of your vulnerability report within **48 hours**.
- **Investigation**: We will validate and investigate the issue within **5 business days**, keeping you informed of findings.
- **Coordinated Disclosure**: Once a fix is verified, a patched release will be published alongside an advisory. We follow responsible disclosure principles and will credit you for your discovery (unless you prefer to remain anonymous).

---

## Threat Model & Security Boundaries

CryoTray runs locally on Windows and interacts directly with low-level system services. Understanding our security boundaries helps identify what constitutes a vulnerability:

### 1. Elevated Privileges (`requireAdministrator`)
CryoTray requires elevation in its application manifest (`app.manifest`) to access kernel-level CPU/GPU hardware sensors via `LibreHardwareMonitorLib` and the `PawnIO` driver.
- **In Scope**: Privilege escalation attacks, DLL hijacking/planting in the application directory, arbitrary code execution, or unsanitized input passing into system APIs.
- **Out of Scope**: Attacks that require an attacker to already possess interactive Administrator or SYSTEM privileges on the machine.

### 2. Windows Power Scheme Management
CryoTray creates and applies the dedicated `"CryoTray Cooling"` power scheme using direct Win32 API calls (`PowerWriteACValueIndex`, `PowerWriteDCValueIndex`, and `PowerSetActiveScheme` in `powrprof.dll`).
- **In Scope**: Injections or corruption allowing unauthorized modification of system-wide security settings or arbitrary power schemes.
- **Out of Scope**: Standard CPU throttling behavior intended by Windows power management policies.

### 3. Update Verification & Authenticode
CryoTray includes automated update checking and downloading via GitHub Releases (`UpdateService.cs`).
- **Security Controls**:
  - Downloads are strictly retrieved over TLS from `https://api.github.com` and `https://github.com`.
  - Cryptographic SHA-256 hash digests are verified against the corresponding release `.sha256` asset before any update archive is extracted.
  - Downloads and rollback copies are staged under `%ProgramData%\CryoTray` with an Administrators owner and a protected DACL limited to Administrators and SYSTEM. Startup creates and secures both roots before reading the completion marker, and rollback reads secure the root again before opening its manifest. Reparse points are rejected, including a post-create recheck after a missing directory is created, existing children are re-secured, and untrusted pre-existing root contents are cleared. The elevated updater validates the manifest version and every per-file SHA-256 before copying, deletes stale files from the release delete list, restores the verified backup on failure, and rejects reparse destinations.
  - The application must run from an administrator-protected folder, unless an administrator has created the developer marker `%ProgramData%\CryoTray\dev-mode` on that machine (the marker cannot be created by a standard user, and update installation still refuses a user-writable folder). Startup rejects an application directory whose owner is not trusted, that grants standard users write/delete/change-permission/take-ownership rights, whose parent chain does the same, or that contains a reparse point. The application folder and its children are then secured with an Administrators owner and a protected DACL (Administrators/SYSTEM full control, Builtin Users read/execute). The installer forces the default Program Files location and does not reuse a previous custom directory. After a successful install it removes a previously registered Inno installation only when the old folder contains both `CryoTray.exe` and `unins000.exe`, is not the new folder, an ancestor or child of it, or an exact Windows or System folder. A portable copy must be extracted to an administrator-protected folder; Downloads, Desktop, user profiles, and user-writable data volumes are rejected.
  - The updater launches the absolute `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe` with `-NoProfile -NonInteractive -File`; it never resolves `powershell.exe` from PATH.
  - Installed copies use the signed-or-unsigned Inno Setup installer silently; portable copies use an update archive with versioned per-file hashes and a cumulative delete list built from the previous release's owned-file history. Release artifacts are checksummed in CI. The app is signed before the installer is compiled, and the setup is signed afterward, when signing secrets exist.
  - A manual install clears any stale one-time success marker. The elevated helper preserves and completes the marker only for the update it was launched to apply (`/PRESERVEUPDATE` plus its `UpdateId`).
  - Digital signatures are validated through `AuthenticodeVerifier.cs` (`WinVerifyTrust` via Win32 `wintrust.dll`) when present. A signature is **not required** for the current unsigned release policy.
- **In Scope**: Checksum validation bypasses, rollback hash bypasses, writable staging or rollback paths, path traversal during update extraction, updater path hijacking, application-folder ACL or owner bypasses, parent-junction/hardlink replacement, or installer/portable selection failures.

### 4. Privacy & Data Handling
- CryoTray runs entirely offline and contains **zero telemetry, analytics, or tracking**.
- The only outbound network requests occur during explicit checks or the adaptive background update checks against GitHub Releases (`5` minutes with the dashboard open, `30` minutes in the tray, plus startup, resume, and network reconnect).
- User settings are stored locally in plaintext JSON at `%APPDATA%\CryoTray\settings.json`. No credentials, personal data, or encryption keys are handled or stored.

### 5. Third-Party Hardware Drivers
CryoTray relies on `LibreHardwareMonitorLib` and the `PawnIO` kernel driver for hardware sensing.
- Vulnerabilities originating strictly within upstream drivers should be reported to the respective maintainers ([LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)). If an issue in our usage pattern enables exploitation, please report it to us directly.
