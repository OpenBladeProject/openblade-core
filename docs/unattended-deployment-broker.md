# Local-development unattended deployment

Issue #296 tracks a developer-only OpenBlade deployment workflow. It exists to
shorten local iteration on an explicitly enrolled development machine after one
attended administrative setup. It is not a release, Stable-update, Store, or
end-user installation feature.

The development broker is built and provisioned only by the dedicated local
development script. Release workflows and ordinary Stable or Nightly output
must neither build nor package the broker, client, provisioner, trust policy,
local manifest, or detached signature. Production installers expose no broker
launch variable, command-line switch, or enrollment argument. An explicit
`LocalDev` publish compiles a fixed enrollment reader into Setup; the normal
Setup project build compiles that source out.

The attended provisioning step installs the fixed broker and client locations,
locks their ACLs to administrators and LocalSystem as appropriate, enrolls one
interactive user, and pins the development TPM public key. The private key is
non-exportable. Provisioning and removal remain explicit administrative acts.

After provisioning, the enrolled user can use the local-development build
script. The script publishes a fixed OpenBlade payload, validates the complete
payload, creates a canonical manifest for those exact bytes, signs the manifest
with the enrolled TPM key, and submits the fixed request to the broker. The
broker accepts only the fixed inbox and enrolled operations, copies the request into
protected staging, verifies the caller, policy, canonical manifest, signature,
file set, hashes, version, and replay state, then invokes Setup with its fixed
quiet behavior. That Setup build reads the enrolled account SID only from the
administrator-owned fixed enrollment and uses it for user-scoped registration
even though Setup itself runs as LocalSystem. It never accepts an arbitrary
executable, path, URL, command, argument list, signing key, user SID, or general
elevated operation.

The broker reports success, rollback, or failure atomically. Restoring a clean
first-install baseline counts as rollback only when the installation tree is
absent and Windows Service Control Manager positively reports that the service
does not exist; an inaccessible, stopped, or untrusted service remains
indeterminate. A normal-integrity client owns the post-install relaunch,
verifies installed ACLs against its enabled token identities, and coordinates
the existing Tray and Session Agent through their fixed session-local shutdown
endpoints before asking the broker to install. It never kills an existing process or
signals a caller-selected endpoint. Exact success must end with one Tray and
one Session Agent at normal integrity. Only an exact broker-attested rollback
may restore the prior process shape; a rejected, failed, mismatched, or
indeterminate deployment does not relaunch a potentially partial installation.

The client checks the current user's Windows desktop shell before stopping
OpenBlade for deployment. It uses that verified, normal-integrity shell as the
parent of each relaunched app, with no inherited handles. Closing the deployment
command then leaves the apps running. Windows-managed power jobs remain in place.
If the shell or user-session checks fail, the client reports the failure without
falling back to a launch tied to the command runner. The client may discard only
its own newly created child when launch verification fails. Start OpenBlade
normally from the Start menu if relaunch is rejected.

Local-development packages are intentionally not release artifacts. They must
not be uploaded, attached to a GitHub release, submitted to the Store, or
treated as publisher-authenticated OpenBlade releases. Release publication and
Store readiness remain unchanged and independent of this workflow.

## Why it lives in the core repository

The broker is developer-only, but its security contract depends on the exact
OpenBlade installer identity, service layout, payload validation, rollback, and
normal-integrity relaunch behavior. Keeping the source and tests beside those
contracts prevents cross-repository drift. Release-boundary tests compensate by
failing if ordinary publishing, Store validation, or Burn authoring includes a
developer broker artifact or launch hook.

## Explicit trust tradeoff

Enrollment authorizes one TPM-backed ECDSA P-256 key belonging to the active
console user. The tool requires the Microsoft Platform Crypto Provider,
hardware implementation, signing-only usage, persistence, and a non-exportable
private key. It signs a canonical manifest that binds the enrollment ID,
monotonic sequence, exact Git revision, fixed installer identity, byte length,
and SHA-256.

This does not make an unsigned local build a public OpenBlade release. It means
that any process able to invoke the enrolled user's TPM key can authorize local
installer bytes for this machine. The fixed inbox, strict broker protocol, and
manifest prevent that authority from becoming an arbitrary command runner, but
they do not protect the development key from malicious code already running as
the enrolled user. This is the risk explicitly accepted for the local
development workflow.

## One-time attended enrollment

From a clean, committed, short non-OneDrive worktree, build a machine-specific
provisioner package without elevation:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  build\development-deployment\Publish-Provisioner.ps1 `
  -TpmKeyName '<new persisted TPM key name>' `
  -CreateTpmKey
```

`-CreateTpmKey` atomically creates a new current-user ECDSA P-256 key through
Microsoft Platform Crypto Provider with no overwrite flag, signing-only usage,
and a non-exportable private key, then verifies that the provider reports a
hardware implementation. It fails if that name already exists. Omit the switch
on later provisioner builds to inspect the existing key without replacing it.
The generated provisioner package contains only the public identity. This step
does not install anything or request UAC.
Review the four-file output under
`artifacts\win-x64\OpenBlade.LocalDevDeployment.Provisioner`, then personally
run the fixed `install` verb once:

```powershell
& 'artifacts\win-x64\OpenBlade.LocalDevDeployment.Provisioner\OpenBlade.DevelopmentDeployment.Setup.exe' install
```

Windows requests UAC for that attended step. The provisioner refuses an
existing enrollment and leaves it unchanged. Re-enrollment or broker changes
require attended maintenance. The CPU-validation extension below has a fixed
upgrade verb that preserves the existing enrollment and replay history.

## Subsequent unattended local builds

After enrollment, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  build\development-deployment\Invoke-LocalDevDeployment.ps1 `
  -TpmKeyName '<persisted TPM key name>'
```

The script requires a clean committed worktree, performs the ordinary
release-excluding OpenBlade publish, stages exactly
`OpenBlade-Local-Dev-Deployment.json`,
`OpenBlade-Local-Dev-Deployment.sig`, and `OpenBlade-Setup.exe`, then invokes
the immutable normal-integrity client. The client demand-starts only the fixed
broker service, verifies its SCM and named-pipe identity, waits for the atomic
terminal result, launches the target Session Agent and Tray only after exact
success, and restores the prior process shape only after exact rollback. Before
the request, that same normal-session client verifies the
fixed installed paths and process identities, opens both required shutdown
events before signaling either, and waits boundedly for an empty exact process
inventory. An exact OpenBlade process in another signed-in session blocks the
request because the enrolled client cannot safely signal that session's local
event. Each build receives a durable monotonic version in the reserved
`65535.x.y.z` range so Burn cannot silently treat a new commit as the already
installed same-version bundle. Managed executable assembly and file versions
use the corresponding `65534.x.y.z` shadow range because .NET reserves `65535`
and rejects it in assembly metadata. Product identity and the signed manifest
retain `65535.x.y.z` and bind that version to the full commit SHA. This path
requests no additional UAC prompt.

The broker starts Burn with the SCM-provided LocalSystem environment needed by
Windows installer infrastructure, strips WiX, .NET, compatibility, and
OpenBlade environment control variables, fixes executable lookup to Windows
system directories, and redirects temporary files into the protected request
staging directory. The developer-only Setup build also resolves shortcut
cleanup through the machine-wide Common Startup folder because LocalSystem has
no interactive user's Startup known folder. It does not derive or traverse an
enrolled user's writable profile while elevated; ordinary release Setup keeps
its existing per-user legacy-cleanup behavior. After exact broker success and a
second normal-integrity active-console check, the client removes only the fixed
legacy `OpenBlade.lnk`, `OpenBlade.SessionAgent.lnk`, and `OpenBlade.cmd` entries
from its own Startup known folder before relaunch. Cleanup failure prevents the
client from reporting success or launching either process.

The reserved version deliberately makes a developer bundle newer than normal
release versions on that enrolled machine. Returning the machine to a Stable or
Nightly installation is therefore an attended maintenance action; this tool is
not a transparent release-channel switcher.

For the exact RZ09-0581 thermal external-drift validation tracked in issue
#354, the same script accepts the explicit
`-ThermalExternalDriftValidation` switch together with an empty external
`-ValidationOutputDirectory`. That opt-in builds one coordinated LocalDev
installer containing the validation-marked Service and matching IPC clients,
then stages those exact installer bytes through the unchanged signed inbox and
fixed broker operation. The external directory receives the installer,
FirmwareSmoke tool, and a manifest binding their source, version, and hashes.
The switch is rejected for Stable and Nightly publishing, does not install
FirmwareSmoke, and does not add a broker command or caller-selected installer
argument.

## CPU voltage validation builds

Issue [#673](https://github.com/OpenBladeProject/openblade-core/issues/673)
adds an explicit CPU-validation operation to the development broker. Existing
enrollments have this operation disabled. Build and review the updated
provisioner, then run its fixed `upgrade-cpu-voltage` verb once while present.
This attended upgrade replaces the broker and client and enables the CPU
operation while preserving the installation ID, enrolled user, TPM key and
replay history. It grants fixed validation-host launch authority; it does not
grant arbitrary elevated command execution.

After that upgrade, use the normal user account for each new committed build:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File `
  build\development-deployment\Invoke-LocalDevDeployment.ps1 `
  -TpmKeyName '<persisted TPM key name>' `
  -CpuVoltageValidation `
  -ValidationOutputDirectory 'C:\OpenBladeValidation\new-build'
```

The output directory must be empty and outside the worktree. CPU and thermal
validation profiles cannot be combined. `-StageOnly` prepares the signed inbox
without contacting the broker. CPU publication uses the standard LocalDev
publisher and matching Capture, FirmwareSmoke and installed Service files.
The closure retains the canonical installed runtime files and excludes only
the product UI images and two XAML assets. Its bounds are 1,536 files, 128 MiB
per file, 1 GiB total, and a 512 KiB manifest.

The schema-2 signed deployment manifest binds `CpuVoltageValidation`, the
full source revision, installer bytes, closure-manifest hash and exact Service
version. That version uses `+voltage-validation.<source8>` to fit the existing
IPC limit; Service metadata still binds the full 40-character revision. The
signer and broker validate every closure file and all three component versions.
The ordinary schema-1 deployment path remains unchanged.

The client requests only `ApplyCpuVoltageInbox`. After verified installation,
the broker starts the fixed protected validation host for the enrolled
interactive user. The client verifies its fixed Status response and leaves Tray
and Session Agent stopped. This operation submits no CPU observation or write
job. Each job still needs its own explicit session-client action and passes the
existing device, power, ownership and readback gates.

No new UAC prompt is needed for these subsequent builds. An unresolved
deployment, retained job or unclosed validation session blocks replacement;
do not redeploy over uncertain resources. Resolve the recorded state through
the existing bounded session path before requesting another build. This
developer workflow and its diagnostics do not establish successful physical
CPU validation; that remains a separate result from an actual admitted run.

## Read installation diagnostics

The enrolled console user can read the current deployment's retained records
and bounded installer log tails without elevation:

```powershell
& 'C:\Program Files\OpenBlade Local Dev Deployment Client\OpenBlade.DeploymentClient.exe' --deployment-status
```

This command connects to the running broker without starting it. It returns
only the current deployment's records and fixed log files; unavailable or
truncated files are marked in the response. New installs write their installer
logs into protected request staging. Existing logs outside that staging are
not included.

A valid enrollment with an unresolved deployment can serve these diagnostics
while installation remains blocked. Reading diagnostics does not reconcile
the deployment, launch OpenBlade, or submit a CPU job. Updating the broker
itself still requires attended administrative maintenance.
