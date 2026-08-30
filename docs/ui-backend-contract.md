# UI Backend Requirements

This contract records presentation data needed by the redesigned WPF views. Backend services remain the source of truth; the UI does not implement hardware or projection behavior.

1. `MainViewModel.IsSwitching`
   Purpose: disable profile actions and show a loading state while a profile is executing.

2. `MainViewModel.CurrentProfile`
   Purpose: identify the active desktop/laptop profile and render the `当前` indicator.

3. `MainViewModel.Profiles`
   Purpose: render profile cards with target input, shortcut text, and command state.

4. `ProfileViewModel.IsCurrent`
   Purpose: subtle current border, check indicator, and accessible selected state.

5. `ProfileViewModel.ShortcutDisplayText`
   Purpose: show the configured shortcut without hardcoding it in the view.

6. `ProfileViewModel.LastSwitchResult`
   Purpose: inline success, partial-success, and error feedback after execution.

7. `MonitorViewModel.IsOnline`
   Purpose: render online/offline status badge and disabled input controls.

8. `MonitorViewModel.InputDisplayName`
   Purpose: render friendly `DisplayPort`, `Type-C`, or other input names without exposing VCP codes.

9. `MonitorViewModel.IsDdcSupported`
   Purpose: distinguish normal, DDC unsupported, and unavailable states in the monitor card.

10. `MonitorViewModel.LastSwitchResult`
    Purpose: identify which monitor failed when a profile completes partially.

11. `MainViewModel.CurrentProjectionMode` and `MainViewModel.IsProjectionModeKnown`
    Purpose: selected state for the projection segmented control; show `当前模式未知` without incorrect highlighting.

12. `SettingsViewModel`
    Purpose: expose General, Monitor, Profile, Hotkey, and Advanced/Diagnostic settings with real save/test/log commands.

13. `ProfileViewModel.IsEditingName`, `BeginRenameCommand`, `CommitRenameCommand`, `CancelRenameCommand`
    Purpose: drive the double-click inline rename editor used by the compact profile cards.

14. `MainViewModel.RefreshMonitorsCommand`
    Purpose: provide the refresh action in the `当前显示器` section header.

15. `MainViewModel.IdentifyMonitorsCommand`
    Purpose: trigger the native display identification overlay; the UI does not infer display numbering.

16. `MonitorViewModel.DisplayNumber`, `DisplayTitle`, and `ModelSubtitle`
    Purpose: render the number badge and avoid repeating a model when no custom display name exists.

17. `MonitorViewModel.SwitchToHdmiCommand` or equivalent mapped input command
    Purpose: enable the reserved HDMI action without hardcoding a VCP value in XAML.

18. Application icon integration
    Purpose: configure the executable application icon to `Assets/MonitorSwitch.ico`; `MainWindow` uses `Assets/MonitorSwitch.png` for the title area.
