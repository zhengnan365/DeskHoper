# MonitorSwitch Phase 1–2 Design

MonitorSwitch is a .NET 8 WPF desktop app using MVVM. The UI calls a discovery service only; that service delegates all monitor communication to `DdcCiService`. Win32 declarations stay in `Native/User32.cs` and `Native/Dxva2.cs`.

`DdcCiService` enumerates HMONITOR values via `EnumDisplayMonitors`, obtains physical monitor handles through dxva2, reads VCP 0x60, and owns each handle. A refresh disposes the prior handle set, and application exit disposes the active set through `DestroyPhysicalMonitors`. No input setting runs automatically; `SetInputAsync` is the gated base API for the next phase.

Projection control is owned by `ProjectionService`. V1 starts `DisplaySwitch.exe` with the matching standard projection switch and uses `QueryDisplayConfig` only to display a best-effort current topology. A profile may optionally carry `ProjectionMode`; profile execution changes DDC inputs first, waits 750ms asynchronously, then changes projection. Any projection failure is returned independently from individual monitor-input results.
