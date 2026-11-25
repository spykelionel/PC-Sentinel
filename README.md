# PC Sentinel

PC Sentinel is a Windows endpoint security application. It controls access to USB mass-storage devices.
- A background Windows service detects removable storage.
- It records each device's hardware identity and requires a **password authorization for every connection session**.
- It audits file transfers in both directions.
- It revokes access when the device disconnects.
- A WPF dashboard shows devices, authorization requests, security events, and settings.

> **Current state: early development (Milestone 0 complete).**
> PC Sentinel currently **monitors only**. It logs USB volume connect and disconnect events, and it **does not block
> any file transfer**. Do not rely on it for protection. Enforcement is planned for Milestone 4. It requires a kernel
> file-system minifilter, which is still being evaluated (see [docs/ENFORCEMENT.md](docs/ENFORCEMENT.md)).

## Security objective

No USB file transfer without authorization. Default policy: **deny**. A new authorization is needed for every connection,
including reconnects of a previously authorized device. Authorization never survives a disconnect or a service restart.
The threat model and its limits (for example, local administrators are out of scope) are in [docs/SECURITY.md](docs/SECURITY.md).

## Solution structure

| Project | Purpose |
|---|---|
| `src/Sentinel.Core` | Platform-neutral domain: device identity, sessions, policies, audit events, contracts |
| `src/Sentinel.Infrastructure` | Windows integrations (WMI, CfgMgr32, Event Log), SQLite persistence, password hashing |
| `src/Sentinel.Service` | Windows service: monitoring, session management, enforcement coordination, IPC server |
| `src/Sentinel.Desktop` | WPF dashboard and authorization prompt (talks to the service over a named pipe) |
| `tests/*` | xUnit tests (unit, integration, architecture rules) |

## Build and test

Requirements: Windows 11 x64, .NET SDK 10.0.404 (pinned in `global.json`).

```powershell
dotnet build PC-Sentinel.slnx
dotnet test  PC-Sentinel.slnx
dotnet format PC-Sentinel.slnx --verify-no-changes

# Run the monitoring service as a console app (does not install anything)
dotnet run --project src/Sentinel.Service
```

The build treats warnings as errors and enforces code style. NuGet versions are centrally managed in
`Directory.Packages.props`, with lock files committed.

## Documentation

| Document | Contents |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Components, project responsibilities, identity pipeline, data flow |
| [docs/ROADMAP.md](docs/ROADMAP.md) | Milestones with tasks, acceptance criteria, risks |
| [docs/PROGRESS.md](docs/PROGRESS.md) | What is done, verified, planned, or blocked |
| [docs/SECURITY.md](docs/SECURITY.md) | Threat model, password handling, sessions, IPC, audit integrity |
| [docs/ENFORCEMENT.md](docs/ENFORCEMENT.md) | Evaluation of Windows USB enforcement mechanisms, recovery procedure draft |
| [docs/DECISIONS.md](docs/DECISIONS.md) | Architecture decision records |
| [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) | Toolchain, conventions, safety rules |
| [docs/TESTING.md](docs/TESTING.md) | Test layers, hardware checklist, enforcement test matrix |

## Safety

Development must not change Windows security policy, removable-storage policy, or boot configuration on the
development machine. The service is not installed during development. Driver work happens only in a snapshotted test VM.
See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md#safety-rules-for-this-machine).
