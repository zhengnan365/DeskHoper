namespace MonitorSwitch.Models;

public sealed record InputLearningResult(bool Success, string StableId, InputSourceKind InputKind, uint? LearnedValue, string? ErrorMessage = null, bool IsConflict = false);
