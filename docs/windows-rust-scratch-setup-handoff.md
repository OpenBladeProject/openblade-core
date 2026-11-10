# Windows Rust migration: scratch Setup handoff

The existing broker launches the verified, fixed-staging bundle and binds the
PID returned by the process runner. Its exit observation is for that process.
The WiX 7 bundle starts `OpenBlade.Installer.UI.exe` as its bootstrapper
application. The BA calls `Engine.Apply` and handles package notifications;
it does not launch `OpenBlade.Setup.exe` or receive a handle to it. The BA
generates or reuses the persisted `OpenBladeBundleOwnerId`. Burn expands it
into the install, repair and uninstall commands for the per-machine
`OpenBladeSetup` ExePackage. Setup parses it for bundle ownership registration.

The extracted manifest of the pinned Phase 9x authored fixture confirms
`Scope="perMachine"`, `Cache="keep"`, the three command templates, and the
embedded `OpenBladeSetup` payload reference. The fixture's Setup payload is
inert. The local WiX engine also contains separate elevated and embedded mode
markers. Static artifacts establish the package declaration and the possibility
of additional engine processes. They do not establish the live clean-room,
elevation, cache or rollback transitions for the broker launch. Package events,
command-line flags, the owner GUID, and parent PIDs are insufficient as proof.

The opt-in candidate receipt fixture rehearses a direct handoff. The fixture
launches its fixed scratch apphost with redirected stdin and holds the image
before launch. It publishes a protected, create-once record tying the exact
active in-flight bytes to the broker and receiver PIDs and creation times, the
held image digest, and a fresh secret. The child receives the scratch locator
and secret through inherited stdin only after publication. The locator selects
the scratch tree. Setup independently opens the fixed image and live broker,
checks the OS-reported receiver image and both process generations, then
reauthenticates the staged release and the Service and Session Agent roles.
It keeps the evidence and active binding open and uses the existing atomic
pending-to-claimed receipt move. It checks broker liveness and cancellation
before claiming, activating, and committing. A failed activation or commit
uses the existing rollback and does not make a consumed receipt reusable.

This fixture uses one nonadministrative Windows account. Its ACLs and inherited
channel rehearse the direct launch protocol and do not isolate mutually
untrusted processes of that same account. The scratch apphost is a fixture
image; it is not the package's Setup executable. No changes are made to
production Burn authoring, Setup arguments, broker staging, or listeners.

Before enabling an installed publisher, observe the exact pinned signed bundle
in an authorized Windows rehearsal from the broker's launch through each actual
Burn process and the final cached Setup image. Verify which supported mechanism
can convey broker-issued, request-specific evidence across any clean-room and
elevation boundary. Verify that it identifies the actual Setup process object
and package bytes before the claim, excludes related bundles and rollback or
uninstall executions, and fails closed on cancellation and restart. That
mechanism has not been shown by this fixture. Proof of the generation loaded
by SCM is a separate requirement.
