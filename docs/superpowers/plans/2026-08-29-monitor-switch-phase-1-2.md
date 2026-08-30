# MonitorSwitch Phase 1–2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `subagent-driven-development` (recommended) or `executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a runnable .NET 8 WPF debug application that enumerates physical monitors and safely reads DDC/CI VCP 0x60.

**Architecture:** `Views` bind only to `ViewModels`; `MonitorDiscoveryService` orchestrates discovery; `DdcCiService` owns physical handles and all DDC calls; `Native` contains P/Invoke declarations. Each refresh destroys the previous physical monitor array.

**Tech Stack:** C# 12, .NET 8 WPF, user32.dll, dxva2.dll.

---

### Task 1: WPF/MVVM shell

**Files:** `MonitorSwitch.csproj`, `App.xaml`, `Views/MainWindow.xaml`, `ViewModels/*.cs`.

- [ ] Create the Windows-targeted WPF project, bind `MainWindow` to `MainViewModel`, and compile with `dotnet build`.

### Task 2: Native DDC boundary

**Files:** `Native/User32.cs`, `Native/Dxva2.cs`, `Services/DdcCiService.cs`.

- [ ] Declare `EnumDisplayMonitors`, physical-monitor lifecycle APIs, and VCP read/write APIs with x64-safe handles.
- [ ] Implement asynchronous refresh and make each active physical-monitor handle owned by a disposable lease.
- [ ] Run `dotnet build` and manually verify that Refresh lists each physical monitor and its DDC read state.

### Task 3: Windows projection control

**Files:** `Models/ProjectionMode.cs`, `Services/ProjectionService.cs`, `Native/DisplayConfig.cs`, `Views/MainWindow.xaml`.

- [ ] Map the four standard projection modes to DisplaySwitch's `/internal`, `/clone`, `/extend`, and `/external` arguments.
- [ ] Query active display paths only to mark the detected mode; never alter display positions, resolutions, refresh rate, orientation, or primary display.
- [ ] Allow an optional `ProjectionMode` in a profile and run it after all DDC input commands plus an asynchronous 750ms reconnect delay.
