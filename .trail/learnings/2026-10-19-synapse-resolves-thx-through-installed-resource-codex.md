---
title: Synapse resolves THX through installed resource paths
author: Codex
date: 2026-10-19
touches:
  - src/OpenBlade.SessionAgent/ThxAudioAdmission.cs
  - src/OpenBlade.SessionAgent/ThxAudioNativeAdapter.cs
---

## Decided
Static inspection of AppEngine 4.0.827 and the reviewed cached Blade middleware confirms that Synapse does not reconstruct the THX DLL's basename at load time: THX startup resolves THXNativeDLLV4Common-<product> (or legacy common name), calls loadSpecificDll, and passes the returned path to initElectron. loadSpecificDll chooses the installed resource by exact name and usedBy product/background-manager admission, then combines userDataDir/Apps with that record's filePath. AppEngine defines userDataDir as process.env.LOCALAPPDATA plus Razer/RazerAppEngine/User Data. The resource checker constructs its expected target with overwriteApp || appFolder, manifest path, saveToDisk.filePath, and URL basename. Locally the configured app folder and service deployment logs establish Common/thxv4common/ThxV4Native_v1.1.1.0.dll, matching OpenBlade's current path.

Verification fingerprints: installed app-4.0.827/resources/app.asar, SHA256 B2CE8C54DC5C991FEEF3A24E1880CED57BA88A0F40A687A5B082187AC0A1CCA6, userDataDir at byte 3020389. Cached bundle b6ea2287bab90dda_0, SHA256 AF329F52E1F4F1F9A4F823D32A2B113D5D485D7F0EA0EB5EE1F0EACCC06D86CC: THX startup byte 1224401; resource existence path byte 2009005; loadSpecificDll body byte 2277859. Parent independently read these exact byte spans. No vendor code was executed.

## Tried and ruled out
The inspected loader does not search product-number driver directories as a fallback. The machine-wide versioned AppEngine Apps/Common directory is not the downloaded current-user resource root on the inspected installation. Neither the current live 709/710/736 manifests nor this local code establish a Blade-specific DLL rename. Do not infer that newer headset DLL versions are compatible.

## Watch out
Both remote reporters' current Synapse installation state is unknown; the user retracted an earlier installed/uninstalled distinction. Confirmed local source and deployment do not prove remote file placement or uninstall retention. The external support helper v2 records installed AppEngine executable versions and THX DLL module paths of already-running Synapse/THX processes, in addition to its bounded file search and exact hash checks. That can show actual alternate loaded paths without asking users to identify Synapse versions. File/process absence is not conclusive uninstall evidence, and module enumeration can fail for inaccessible or exiting processes. The helper remains observational: no DLL loading, audio IPC, service mutation, downloads or elevation. Full product admission still needs the exact model, driver stack, configuration and service independently verified after the native DLL gate.
