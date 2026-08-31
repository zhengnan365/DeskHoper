using MonitorSwitch.Models;
using MonitorSwitch.Services;
using MonitorSwitch.ViewModels;

var failures = new List<string>();
Assert(ProjectionService.GetDisplaySwitchArgument(ProjectionMode.Internal) == "/internal", "仅电脑屏幕必须映射 /internal");
Assert(ProjectionService.GetDisplaySwitchArgument(ProjectionMode.Duplicate) == "/clone", "复制必须映射 /clone");
Assert(ProjectionService.GetDisplaySwitchArgument(ProjectionMode.Extend) == "/extend", "扩展必须映射 /extend");
Assert(ProjectionService.GetDisplaySwitchArgument(ProjectionMode.External) == "/external", "仅第二屏幕必须映射 /external");
Assert(ProjectionService.GetDisplaySwitchArgument(ProjectionMode.Unchanged) is null, "不修改不得启动 DisplaySwitch");
var attempts = new List<int>();
var delays = new List<TimeSpan>();
var retryResult = await DdcRetryPolicy.ExecuteAsync(
    attempt => { attempts.Add(attempt); return Task.FromResult(attempt == 3); },
    (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
Assert(retryResult.Succeeded, "第三次 DDC 尝试成功时应返回成功");
Assert(attempts.SequenceEqual([1, 2, 3]), "DDC 必须最多尝试三次");
Assert(delays.SequenceEqual([TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250)]), "DDC 重试延迟必须为 100ms 和 250ms");
var ddc = new FakeDdcCiService();
var projection = new FakeProjectionService();
await new ProfileExecutionService(ddc, projection).ApplyAsync(new SwitchProfile { Name = "不改投影" });
Assert(!projection.WasCalled, "ProjectionMode 为 null 时不得修改 Windows 投影模式");
var mapping = new MonitorInputMapping();
Assert(mapping.ToSource(17) == MonitorInputSource.Hdmi, "AOC 默认 17 应映射 HDMI");
Assert(new MonitorInputMapping { Hdmi = null }.ToSource(17) == MonitorInputSource.Unknown, "未配置 HDMI 时 17 必须保持未知");
Assert(new SwitchProfile { Name = "Test", Monitors = [] }.ProjectionMode is null, "旧 Profile 默认投影模式必须为 null");
var desktopVm = new ProfileViewModel(new SwitchProfile { Name = "台式机", Monitors = [new SwitchProfileMonitor { MonitorId = "1", InputKind = InputSourceKind.DisplayPort }, new SwitchProfileMonitor { MonitorId = "2", InputKind = InputSourceKind.DisplayPort }] }, _ => Task.CompletedTask);
Assert(desktopVm.ConnectionSummary == "DP + DP", "Desktop ConnectionSummary 必须为 DP + DP");
var laptopVm = new ProfileViewModel(new SwitchProfile { Name = "笔记本", Monitors = [new SwitchProfileMonitor { MonitorId = "1", InputKind = InputSourceKind.UsbC }, new SwitchProfileMonitor { MonitorId = "2", InputKind = InputSourceKind.UsbC }] }, _ => Task.CompletedTask);
Assert(laptopVm.ConnectionSummary == "Type-C + Type-C", "Laptop ConnectionSummary 必须为 Type-C + Type-C");
var mixedProfile = new SwitchProfile { Id = "laptop", Name = "笔记本", Monitors = [new SwitchProfileMonitor { StableId = "A", InputKind = InputSourceKind.Hdmi }, new SwitchProfileMonitor { StableId = "B", InputKind = InputSourceKind.UsbC }] };
var perProfilePath = Path.Combine(Path.GetTempPath(), $"per-profile-{Guid.NewGuid():N}.json");
var perPreferences = new MonitorPreferencesService(perProfilePath);
await perPreferences.SetMappingAsync("A", new MonitorInputMapping { DisplayPort = 15, UsbC = 16, Hdmi = 18 });
await perPreferences.SetMappingAsync("B", new MonitorInputMapping { DisplayPort = 15, UsbC = 19, Hdmi = 17 });
var recordingDdc = new RecordingDdcCiService();
await new ProfileExecutionService(recordingDdc, projection, perPreferences).ApplyAsync(mixedProfile);
Assert(recordingDdc.Writes.TryGetValue("A", out var aValue) && aValue == 18 && recordingDdc.Writes.TryGetValue("B", out var bValue) && bValue == 19, "Per-monitor Profile 必须按 StableId 使用各自 learned mapping");
File.Delete(perProfilePath);
var legacyExecution = new RecordingDdcCiService();
await new ProfileExecutionService(legacyExecution, projection).ApplyAsync(new SwitchProfile { Name = "legacy", Monitors = [new SwitchProfileMonitor { MonitorId = "A", Input = 16 }] });
Assert(legacyExecution.Writes.TryGetValue("A", out var legacyValue) && legacyValue == 16, "Legacy raw Input=16 必须继续执行");
var normalizePath = Path.Combine(Path.GetTempPath(), $"legacy-profile-{Guid.NewGuid():N}.json");
await File.WriteAllTextAsync(normalizePath, "[{\"name\":\"legacy\",\"monitors\":[{\"monitorId\":\"DISPLAY2\",\"input\":16}]}]");
var normalizedLegacy = (await new ProfileService(normalizePath).LoadAsync()).Single();
Assert(normalizedLegacy.Monitors.Single().StableId is null && normalizedLegacy.Monitors.Single().MonitorId == "DISPLAY2", "临时 DISPLAY 编号不得迁移为 StableId");
File.Delete(normalizePath);
var renamePath = Path.Combine(Path.GetTempPath(), $"rename-profile-{Guid.NewGuid():N}.json");
var renameService = new ProfileService(renamePath);
var originalLaptop = new SwitchProfile { Id = "laptop", Name = "笔记本", Monitors = mixedProfile.Monitors };
await renameService.SaveAsync([originalLaptop]);
var renameVm = new ProfileViewModel(originalLaptop, _ => Task.CompletedTask, renameService);
await renameVm.BeginRenameCommand.ExecuteAsync(); renameVm.EditableName = "ThinkPad"; await renameVm.CommitRenameCommand.ExecuteAsync();
var renamedLaptop = (await renameService.LoadAsync()).Single();
Assert(renamedLaptop.Id == "laptop" && renamedLaptop.Name == "ThinkPad" && renamedLaptop.Monitors.Count == 2, "Profile rename 必须保留 Id 和 per-monitor targets");
File.Delete(renamePath);
Assert(new ProfileViewModel(mixedProfile, _ => Task.CompletedTask).ConnectionSummary == "HDMI + Type-C", "Per-monitor ConnectionSummary 必须保留每台屏幕输入顺序");
var prefPath = Path.Combine(Path.GetTempPath(), $"monitor-prefs-{Guid.NewGuid():N}.json");
var prefService = new MonitorPreferencesService(prefPath);
await prefService.RenameAsync("AOC|LEFT", "  左侧显示器  ");
var prefs = await prefService.LoadAsync();
Assert(prefs.Single().CustomName == "左侧显示器", "显示器自定义名称必须 Trim 后持久化");
await prefService.SetMappingAsync("AOC|LEFT", new MonitorInputMapping { DisplayPort = 18, UsbC = 19, Hdmi = 20 });
Assert((await prefService.LoadAsync()).Single().InputMappings.UsbC == 19, "显示器输入映射必须独立持久化");
File.Delete(prefPath);
var logicalProfile = new SwitchProfile { Name = "逻辑输入", Monitors = [new SwitchProfileMonitor { MonitorId = "AOC|SERIAL", InputKind = InputSourceKind.UsbC, Input = 16 }] };
Assert(logicalProfile.Monitors[0].InputKind == InputSourceKind.UsbC && logicalProfile.Monitors[0].Input == 16, "Profile 必须同时兼容逻辑输入和旧 raw 值");
var profile = new SwitchProfile { Name = "测试", Monitors = [new SwitchProfileMonitor { MonitorId = "a", Input = 15 }], ProjectionMode = ProjectionMode.Extend };
var profileFile = Path.Combine(Path.GetTempPath(), $"monitorswitch-{Guid.NewGuid():N}.json");
await new ProfileService(profileFile).SaveAsync([profile]);
var loadedProfiles = await new ProfileService(profileFile).LoadAsync();
Assert(loadedProfiles.Count == 1 && loadedProfiles[0].ProjectionMode == ProjectionMode.Extend, "Profile 必须持久化投影模式");
File.WriteAllText(profileFile, "{ broken json");
var recoveredProfiles = await new ProfileService(profileFile).LoadAsync();
Assert(recoveredProfiles.Count == 0, "损坏 Profile 配置必须回退为空列表");
Assert(File.Exists(profileFile + ".corrupt"), "损坏 Profile 配置必须创建备份");
File.Delete(profileFile); File.Delete(profileFile + ".corrupt");
var learningPath = Path.Combine(Path.GetTempPath(), $"monitor-learning-{Guid.NewGuid():N}.json");
var learningPrefs = new MonitorPreferencesService(learningPath);
var learning = new InputLearningService(new FixedDdcCiService(18), learningPrefs);
var learnedUsbC = await learning.LearnInputSourceAsync("AOC|LEARN", InputSourceKind.UsbC);
Assert(learnedUsbC.Success && (await learningPrefs.GetAsync("AOC|LEARN"))!.InputMappings.UsbC == 18, "Type-C 学习值必须立即持久化");
var conflict = await learning.LearnInputSourceAsync("AOC|LEARN", InputSourceKind.DisplayPort);
Assert(!conflict.Success && conflict.IsConflict, "重复输入值必须返回冲突且默认不覆盖");
var forced = await learning.LearnInputSourceAsync("AOC|LEARN", InputSourceKind.DisplayPort, true);
Assert(forced.Success && (await learningPrefs.GetAsync("AOC|LEARN"))!.InputMappings.DisplayPort == 18, "允许冲突时必须覆盖映射");
await learningPrefs.ResetMappingAsync("AOC|LEARN", "Q27V5G5R3");
Assert((await learningPrefs.GetAsync("AOC|LEARN"))!.InputMappings.DisplayPort == 15, "AOC 重置映射必须恢复 DP=15");
File.Delete(learningPath);
if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
Console.WriteLine("Projection mode contract tests passed.");
Console.WriteLine("Per-monitor profile contract tests passed.");
Console.WriteLine("V2 monitor preferences/input learning contract tests passed.");

void Assert(bool condition, string message) { if (!condition) failures.Add(message); }

sealed class FakeDdcCiService : IDdcCiService
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<IReadOnlyList<MonitorDevice>> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MonitorDevice>>([]);
    public Task<uint?> ReadInputAsync(string monitorId, CancellationToken cancellationToken = default) => Task.FromResult<uint?>(null);
    public Task<DdcOperationResult> SetInputAsync(string monitorId, uint inputValue, CancellationToken cancellationToken = default) => Task.FromResult(new DdcOperationResult(true, 1, 0, TimeSpan.Zero));
}

sealed class FakeProjectionService : IProjectionService
{
    public bool WasCalled { get; private set; }
    public ProjectionMode? CurrentMode => null;
    public Task<ProjectionResult> ApplyAsync(ProjectionMode mode, CancellationToken cancellationToken = default) { WasCalled = true; return Task.FromResult(ProjectionResult.NoChange); }
    public Task<ProjectionMode?> RefreshCurrentModeAsync(CancellationToken cancellationToken = default) => Task.FromResult<ProjectionMode?>(null);
}

sealed class FixedDdcCiService(uint value) : IDdcCiService
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<IReadOnlyList<MonitorDevice>> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MonitorDevice>>([]);
    public Task<uint?> ReadInputAsync(string monitorId, CancellationToken cancellationToken = default) => Task.FromResult<uint?>(value);
    public Task<DdcOperationResult> SetInputAsync(string monitorId, uint inputValue, CancellationToken cancellationToken = default) => Task.FromResult(new DdcOperationResult(true, 1, 0, TimeSpan.Zero));
}

sealed class RecordingDdcCiService : IDdcCiService
{
    public Dictionary<string, uint> Writes { get; } = [];
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<IReadOnlyList<MonitorDevice>> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MonitorDevice>>([]);
    public Task<uint?> ReadInputAsync(string monitorId, CancellationToken cancellationToken = default) => Task.FromResult<uint?>(null);
    public Task<DdcOperationResult> SetInputAsync(string monitorId, uint inputValue, CancellationToken cancellationToken = default) { Writes[monitorId] = inputValue; return Task.FromResult(new DdcOperationResult(true, 1, 0, TimeSpan.Zero)); }
}
