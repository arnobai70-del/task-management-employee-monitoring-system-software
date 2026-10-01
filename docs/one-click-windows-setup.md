# One-click Windows setup

TaskMonitoring now has two Windows-friendly entry points so normal operators and employees do not need to type the development commands manually.

## Local Admin launcher

From a source checkout, double-click:

```text
Start-Admin.cmd
```

The launcher:

1. detects a checkout under a protected Windows directory such as `C:\Windows\System32` and copies it to `Documents\TaskMonitoring` before relaunching;
2. checks Docker Desktop, .NET 10 and Node.js 24 prerequisites;
3. starts Docker Desktop when the engine is installed but stopped;
4. requires a configured, gitignored `.env` and refuses placeholder secrets or a bootstrap password shorter than 12 characters;
5. starts PostgreSQL and waits for container health;
6. starts the API on `http://127.0.0.1:5080` in Development mode;
7. runs `npm install` only when Admin Web dependencies are not present;
8. starts the Admin Web on `http://127.0.0.1:5173` and opens the browser.

If an old local PostgreSQL volume was initialized with a different password, use the explicit development-only reset once:

```text
Start-Admin.cmd -ResetLocalDatabase
```

That switch deletes the local Docker database volume. It must never be used against production data.

The launcher is a source/development convenience. Production remains the guarded Linux/Docker deployment path in `docs/production-environment.md`.

## Employee setup wizard

Production Windows release bundles now contain:

```text
TaskMonitoring.EmployeeSetup.exe
```

The executable is a self-contained Windows setup wizard with an elevation manifest. In a signed production release it is Authenticode-signed with the same organization certificate used for the Desktop, Service and Updater.

The wizard provides the normal flow:

```text
Welcome -> Next -> confirm organization configuration -> Install
```

Before invoking the existing `install-employee-windows.ps1` installer, the wizard explicitly validates the Authenticode signature and exact pinned publisher-certificate SHA-256 for every deployment PowerShell file it relies on. It then runs the already-validated installer non-interactively. This avoids machine-specific PowerShell trusted-publisher prompts without weakening the release trust boundary. The underlying installer still verifies the release manifest, runtime ZIP SHA-256, executable version metadata and pinned publisher identity before activation.

### Preconfigure the wizard

The production release workflow accepts optional values:

- `server_url` — organization TaskMonitoring HTTPS origin;
- `package_base_url` — stable/beta HTTPS directory containing `release.json` and the runtime ZIP.

When supplied, these values are embedded as assembly metadata before `TaskMonitoring.EmployeeSetup.exe` is signed. The publisher certificate SHA-256 is also embedded before signing. Those preconfigured values are read-only in the wizard.

If an endpoint is not embedded, the setup wizard requires the installing administrator to enter it. Production installation always requires HTTPS and a 64-hex publisher certificate SHA-256 fingerprint.

The enrollment secret for centralized agent-update control is deliberately not embedded into the employee setup executable or stored in release metadata.

## Production release bundle validation

`test-employee-release.ps1` now requires `TaskMonitoring.EmployeeSetup.exe`, verifies its version against `release.json`, and includes it in Authenticode publisher validation for signed bundles.

CI additionally parses the one-click PowerShell launcher, builds the Windows setup project, builds a preconfigured unsigned development bundle, and verifies the setup executable is present. Unsigned development bundles remain invalid for real production installation.
