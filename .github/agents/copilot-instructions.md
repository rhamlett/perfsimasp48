# Performance Problem Simulator Development Guidelines

Auto-generated from all feature plans. Last updated: 2026-03-18

## Session Startup

At the start of every new chat session, before answering any questions, read the repository memory file at `/memories/repo/project-context.md` using the memory tool. This file contains important project-specific context including the project's purpose, architecture, sister app locations, and working guidelines. If the file does not exist, continue normally without error.

## Active Technologies

- C# 7.3 / .NET Framework 4.8 + ASP.NET 4.8 with OWIN/Katana, SignalR 2.x (real-time dashboard), System.Diagnostics (metrics)

## Project Structure

```text
backend/
frontend/
tests/
```

## Commands

# Add commands for C# 12 / .NET 8.0 LTS

## Code Style

C# 7.3 / .NET Framework 4.8: Follow standard conventions

## Recent Changes

- 001-perf-problem-simulator: Ported to ASP.NET Framework 4.8 with OWIN/Katana self-hosting, SignalR 2.x

<!-- MANUAL ADDITIONS START -->
<!-- MANUAL ADDITIONS END -->
