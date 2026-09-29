# Access Assignment Core

This module records authorized business access assignments for employees. It is an inventory and administration feature, not a remote-control engine.

## RDP assignments

RDP records contain the employee, business label, host, port, optional username reference, optional credential-manager reference, validity dates, active state, and notes.

The system intentionally does **not** store or return RDP passwords, private keys, or reusable authentication secrets. `CredentialReference` is only a reference to an approved external secret-management location or internal credential identifier.

## IP assignments

IP records contain the employee, IP address, device name, optional MAC address, assignment/release dates, notes, and one of these states:

- `Active`
- `Reserved`
- `Released`

An IP address cannot be active/reserved in more than one assignment through the service layer at the same time. Released rows are retained as history.

## Website assignments

Website access records contain the employee, website name/URL, optional username reference, access level (`View`, `Work`, `Admin`), effective dates, active state, and notes. Passwords or session cookies are not stored.

## Security

- Read access requires `access.assignments.read`.
- Create/update/deactivate/release operations require `access.assignments.manage`.
- Active assignments cannot be given to inactive employees.
- Administrative changes write audit-log events.
- Client-side permission checks are presentation-only; backend authorization remains authoritative.
