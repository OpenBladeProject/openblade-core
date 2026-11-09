---
title: Attribute residual allocations before changing resource ownership
author: Jack
date: 2026-11-04
branch: fix/codebase-reliability-audit
touches:
  - CHANGELOG.md
  - build/benchmarks/NativeResources/README.md
  - build/benchmarks/NativeResources/RESULTS.md
  - build/benchmarks/NativeResources/results/2026-11-04-localized-shell-retirement.json
  - build/benchmarks/NativeResources/results/2026-11-04-native-layoutsync-reference.json
---

## Decided

Separate native allocation ownership from process private bytes. The pinned MRM.dll (SHA-256 1E542A07FD1351B40465D5A06A2B3DB7323089B01ACF40F17F6129115772BADA) leaves 101 ResourceMapSubtree allocations after 101 public MrmGetChildResourceMap calls and MrmDestroyResourceManager. A private-process, UI-free reproduction confirms this independently of OpenBlade subscriptions. Sampled four-byte identifier arrays in the language stress are owned by those 104-byte subtrees. See the native-resource-map attribution artifact for raw artifact hashes and distinct source revisions.

Keep the persistent native-shell, Mica, icon and cached AppWindow-projection fixes. They reclaim tracked localized views and stop the measured continuing Composition, Timer, GDI and USER growth. The resource map finding is a separate SDK ownership defect; do not relabel it as an application event root or claim all native memory is fixed.

## Tried and ruled out

The app already caches its own Resources subtree. That does not intercept WinUI's per-x:Uid TryGetSubtree calls. ResourceManagerRequested alone cannot override the sealed public ResourceMap methods. No child-map destroy API exists in the pinned public MRM header; MrmFreeResource is for returned buffers, not maps. Do not force COM releases, free borrowed native pointers, patch SDK binaries, or add product GC.

Default-heap busy blocks are not automatically leaked application objects. Growing 131351-byte blocks point through common objects into the Intel graphics runtime; this does not prove a Vulkan path or a driver leak. A broad conditional RtlAllocateHeap breakpoint exceeded its bounded deadline and supplied no allocation stack; do not repeat it as a successful control.

## Watch out

The long census kept 781 retired weak references dead but still found native ChangedArgs families increasing twice per language change and persistent MRM allocations. Event handles made a one-time step then stayed in a range; endpoint deltas alone falsely suggest a per-change slope. HeapWalk samples cover only the default process heap, and allocation state does not establish reachability. Keep raw dumps, addresses, process IDs and private paths local. Claude's new review hit its weekly quota and returned zero findings, so earlier successful reviews do not cover this refactor.
