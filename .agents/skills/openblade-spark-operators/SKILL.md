---
name: openblade-spark-operators
description: Route bounded OpenBlade publishing, validation, log-reduction, and offline evidence or localization audits to the repository's GPT-5.3-Codex-Spark custom agents when the runtime exposes exact named-agent selection. Use for substantial deterministic operations with noisy output; do not use for product decisions, architecture, live hardware or capture work, installation, release publication, destructive cleanup, or trivial checks.
---

# OpenBlade Spark Operators

Delegate only work whose inputs and expected result are already defined. The primary agent remains responsible for scope, safety, product decisions, exact-head readiness, and final acceptance.

## Decide whether to delegate

Use a Spark operator when the work is bounded, independently executable, and long-running or log-heavy enough to justify a separate agent. Handle a quick command or small inspection directly because a subagent adds context and token overhead.

Before delegation:

1. Read the repository `AGENTS.md` and preserve its issue, worktree, versioning, publishing, hardware, and cleanup requirements.
2. Confirm the user has authorized the requested operation. Skill activation alone is not authorization to build, publish, mutate files, or access external systems.
3. Keep issue creation, branch/worktree ownership, version-impact decisions, and GitHub mutations in the primary agent.
4. Supply immutable inputs and a concrete stopping condition. Do not ask an operator to infer a release SHA, version impact, hardware target, or safety policy.

## Confirm named-agent availability

Before reading a mode reference or spawning an operator, inspect the current subagent interface. Continue only when it exposes an exact custom-agent selector that can select the required `openblade_*` name. The presence of the skill and `.codex/agents/*.toml` files proves that the repository configuration is discoverable; it does not prove that this client can invoke those profiles.

A task name, thread label, prompt, or generic model override is not a substitute for an exact custom-agent selector unless the current interface explicitly documents that behavior. Do not pass an unsupported model name, spawn a generic subagent under an operator's name, or silently substitute another model.

If exact named-agent selection is unavailable, stop before the delegated operation and report that Spark custom-agent execution is unavailable on the current task surface. Ask for explicit user approval before running the operation directly in the primary agent or delegating it to another exposed model. Skill activation alone does not authorize that fallback.

Only describe a delegated run as Spark when the exact named custom agent was selected and its repository manifest remains pinned to `gpt-5.3-codex-spark`. If the runtime reports the child's actual model, verify that it agrees with the manifest; treat a conflict as a failed preflight.

## Route the work

- For an exact-SHA local stable or nightly artifact build, read [references/release-operator.md](references/release-operator.md), then spawn the `openblade_release_operator` custom agent.
- For affected, focused, full, build-contract, or localization-snapshot validation, read [references/validation-operator.md](references/validation-operator.md), then spawn the `openblade_validation_operator` custom agent.
- For read-only analysis of sanitized fixtures, admission manifests, cached diagnostics, localization invariants, CI routing, or test coverage, read [references/offline-auditor.md](references/offline-auditor.md), then spawn the `openblade_offline_auditor` custom agent.

Spawn one operator by default. Never run two write-capable operators in the same worktree concurrently. Wait for the selected operator, inspect its evidence, and retain all novel diagnosis, remediation, approval, and acceptance decisions in the primary agent.

## Non-delegable boundary

Do not delegate any of the following to these Spark operators:

- source-code fixes or self-remediation after a failure;
- exact-device admission, protocol-command inference, ownership, recovery, IPC, or architecture decisions;
- live `OpenBlade.Capture`, USBPcap, Synapse, FirmwareSmoke, installed-service, Session Agent, HID, or physical-validation operations;
- signing, installation, UAC, live tray patching, pushing, tagging, merging, GitHub release publication, or PR readiness decisions;
- destructive worktree, branch, submodule, artifact, or capture cleanup.

If the requested task crosses this boundary, keep it with the primary agent or an attended human and use Spark only for a separately bounded offline or synthetic subtask.
