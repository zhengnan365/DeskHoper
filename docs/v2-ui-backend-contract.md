# MonitorSwitch V2 UI Backend Contract

The V2 UI consumes these backend-owned properties and commands. It does not implement DDC/CI or input mapping logic.

## Monitor Rename

- `MonitorViewModel.CustomName`, `DisplayTitle`, `ModelSubtitle`, `IsEditingName`
- `BeginRenameCommand`, `CommitRenameCommand`, `CancelRenameCommand`

## Input Learning and Mapping

- `IsDisplayPortLearned`, `IsUsbCLearned`, `IsHdmiLearned`
- `DisplayPortMapping`, `UsbCMapping`, `HdmiMapping`
- `LearnDisplayPortCommand`, `LearnUsbCCommand`, `LearnHdmiCommand`
- `ResetMappingCommand`
- `CanSwitchToDisplayPort`, `CanSwitchToUsbC`, `CanSwitchToHdmi`
- `SwitchToHdmiCommand`

## Conflict Handling

- `IsConflict` and a user-facing conflict message when a learned value is already assigned to another source.
- Commands or result actions for `仍然保存` and `取消`.

## Monitor Identification

- `MainViewModel.IdentifyMonitorsCommand`
- `MonitorViewModel.DisplayNumber`

## Application Icon

Configure the executable application icon to `Assets/MonitorSwitch.ico` during project integration.

No Auto Return, disconnect detection, capability, or automatic fallback UI is part of V2.
