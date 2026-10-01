# Standalone Employee Installer

TaskMonitoring supports a true single-file Windows employee installer. The final `TaskMonitoring.EmployeeSetup.exe` embeds the release manifest, runtime package, updater, deployment scripts, rollback support and maintenance files. The EXE extracts the payload to a private temporary directory, validates it, installs the components, and removes the temporary payload after setup.

## Local office test

This path is for development testing only. It intentionally allows unsigned binaries and HTTP and must not be used as a production deployment path.

1. Close any existing localhost-only TaskMonitoring API window.
2. Double-click `Start-Admin-LAN-Test.cmd` from the safe project folder. Run it as Administrator if Windows Firewall needs the Private-network rule.
3. The launcher binds the development API to port `5080`, scopes the firewall rule to the Private profile and `LocalSubnet`, and records the detected test server URL under `.local/employee-test-server.txt`.
4. Double-click `Build-Employee-Test-Installer.cmd`.
5. Accept the detected LAN URL or enter the correct `http://<admin-pc-ip>:5080` URL.
6. The builder creates `artifacts/employee-test-installer/TaskMonitoring.EmployeeSetup-TEST.exe` and opens Explorer to the file.
7. Copy only that EXE to the employee test PC and run it as Administrator. Choose **Next** and **Install**.
8. Use the installed **TaskMonitoring Employee Workspace** Desktop/Start Menu shortcut and sign in with an employee account created in the Admin Console.

The development test installer has automatic updates disabled because it is not backed by a signed HTTPS release channel. It is visibly marked as a development test build.

## Production release

Production installation remains fail-closed:

- the server URL must use HTTPS;
- the update manifest URL must use HTTPS;
- the release executables and PowerShell deployment files must be Authenticode-signed;
- the setup wizard pins the SHA-256 fingerprint of the organization signing certificate;
- the standalone setup executable itself is signed after the payload is embedded;
- the release package hash, version and size are validated before activation;
- rollback and the existing updater safety checks remain in force.

The `Production Windows Release` workflow requires the production server URL, update package base URL, and repository signing-certificate secrets. It uploads both the full signed release bundle and the signed standalone `TaskMonitoring.EmployeeSetup.exe` artifact.

## Security boundary

LAN Test mode is only a convenience for office development testing. It does not weaken the production release path. It opens only TCP port `5080` on the Windows **Private** firewall profile and scopes remote access to `LocalSubnet`. Production deployments should use the normal HTTPS server/domain and signed release workflow rather than LAN Test mode.
