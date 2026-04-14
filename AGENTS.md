## Project Workflow

- Do not bump `addon/config.yaml` version unless the user explicitly asks for a release/version bump.
- Do not create release commits or tags unless the user explicitly asks for a release.
- Do not run or trigger Home Assistant add-on image/release builds unless the user explicitly asks for it.
- Do not push to `main` just to trigger the Home Assistant add-on image build unless the user explicitly asks for it.
- When completing a backlog task, move it into the `Done` section as a short one-line summary and remove the old detailed task body from `Ready`, `Next`, or `Later`.
- Local build, test, and format checks are allowed for implementation verification.
- Because this project is learning-first, every C# class, record, and interface should have a Russian XML documentation header that explains what it is, why it exists, and how it works.
- Keep agent conversations transport-agnostic: Telegram is only one adapter, and future adapters such as Web UI must reuse the same dialogue/runtime/memory contracts.
- Do not store outbound system notifications as normal dialogue memory. For example, future camera alerts sent to Telegram should be event/notification records, not user-assistant turns that pollute conversational context.
- Keep risky action approval domain-agnostic: Home Assistant, file writes/deletes, scripts and future tools should use the generic `Confirmation` layer instead of implementing separate approve/reject flows.
