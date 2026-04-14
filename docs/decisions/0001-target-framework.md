# 0001: Target Framework For Initial Skeleton

## Decision

Use `net8.0` for the initial skeleton.

## Context

The project plan prefers `net10.0` because the long-term target is a modern C#/.NET implementation. The local machine currently has .NET SDK `8.0.303` and `9.0.100`, so `net10.0` cannot be built here yet.

`net8.0` is a conservative LTS fallback and is sufficient for the first Microsoft Agent Framework learning slice: Generic Host, dependency injection, tests and Docker packaging.

## Consequence

When a .NET 10 SDK is installed in the project environment, revisit this decision and update target frameworks from `net8.0` to `net10.0` if the runtime packages and Home Assistant add-on base image support it cleanly.
