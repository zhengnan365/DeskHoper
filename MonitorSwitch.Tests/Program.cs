using System.Security.Cryptography;
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
var learnedMappings = new Dictionary<string, MonitorInputMapping> { ["A"] = new() { DisplayPort = 15, UsbC = 16, Hdmi = 18 }, ["B"] = new() { DisplayPort = 15, UsbC = 19, Hdmi = 17 } };
Assert(ProfileStateMatcher.Matches(mixedProfile, new Dictionary<string, uint?> { ["A"] = 18, ["B"] = 19 }, learnedMappings), "A HDMI + B Type-C 必须匹配 Laptop，无论 target JSON 顺序");
Assert(!ProfileStateMatcher.Matches(mixedProfile, new Dictionary<string, uint?> { ["A"] = 15, ["B"] = 19 }, learnedMappings), "A DP + B Type-C 必须不匹配 Laptop");
var renamedProfile = new SwitchProfile { Id = "laptop", Name = "ThinkPad", Monitors = mixedProfile.Monitors };
Assert(ProfileStateMatcher.Matches(renamedProfile, new Dictionary<string, uint?> { ["A"] = 18, ["B"] = 19 }, learnedMappings), "Profile rename 不得影响 StableId 匹配");
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
var editorPath = Path.Combine(Path.GetTempPath(), $"profile-editor-{Guid.NewGuid():N}.json");
var editorService = new ProfileService(editorPath);
await editorService.AddAsync(new SwitchProfile { Id = "custom-1", Name = "会议室", Monitors = [new SwitchProfileMonitor { StableId = "A", InputKind = InputSourceKind.Hdmi, Input = 17 }], ProjectionMode = ProjectionMode.Extend });
await editorService.AddAsync(new SwitchProfile { Id = "custom-2", Name = "演示", Monitors = [new SwitchProfileMonitor { StableId = "A", InputKind = InputSourceKind.DisplayPort, Input = 15 }] });
var addedProfiles = await editorService.LoadAsync();
Assert(addedProfiles.Count == 2 && addedProfiles[0].Name == "会议室" && addedProfiles[1].ProjectionMode is null, "新建切换按钮必须落盘，并保留各自的投影模式");
await editorService.DeleteAsync("custom-1");
var keptProfiles = await new ProfileService(editorPath).LoadAsync();
Assert(keptProfiles.Count == 1 && keptProfiles[0].Id == "custom-2", "删除切换按钮必须只移除被删的那一个");
File.Delete(editorPath);
var editorDevice = new MonitorDevice { Id = "AOC|A", StableId = "AOC|A", FriendlyName = "AOC", Model = "Q27", WindowsDisplayName = "\\\\.\\DISPLAY2", CurrentInput = 17, IsDdcSupported = true };
var editorMonitor = new MonitorViewModel(editorDevice, new FixedDdcCiService(17), new MonitorInputMapping(), _ => { });
var editorVm = new ProfileEditViewModel(new SwitchProfile { Id = "laptop", Name = "笔记本", ProjectionMode = ProjectionMode.Extend, Monitors = [new SwitchProfileMonitor { StableId = "AOC|A", InputKind = InputSourceKind.DisplayPort, Input = 15 }] }, [editorMonitor]);
Assert(editorVm.Rows.Count == 1 && editorVm.SelectedProjection?.Mode == ProjectionMode.Extend && editorVm.Rows[0].SelectedOption?.Kind == InputSourceKind.DisplayPort, "编辑已有按钮必须预选它原来保存的输入源，而不是显示器当前输入");
var rebuiltProfile = editorVm.Build();
Assert(rebuiltProfile.Id == "laptop" && rebuiltProfile.Name == "笔记本" && rebuiltProfile.Monitors[0].Input == 15 && rebuiltProfile.ProjectionMode == ProjectionMode.Extend, "编辑器保存必须沿用原 Id 并写回选定的输入值与投影模式");
var createdVm = new ProfileEditViewModel(null, [editorMonitor]);
var createdProfile = createdVm.Build();
Assert(createdVm.IsNew && createdProfile.Id is { Length: 32 } && createdProfile.ProjectionMode is null && createdProfile.Monitors[0].Input == 17, "新建按钮必须生成新 Id、默认不改投影，并预选显示器当前输入源");
Assert(ClipboardSyncPolicy.HasChanged("abc", "ab") && !ClipboardSyncPolicy.HasChanged("abc", "abc") && !ClipboardSyncPolicy.HasChanged(null, "abc") && !ClipboardSyncPolicy.HasChanged(string.Empty, "abc"), "剪贴板只在内容真的变化时才同步，空内容和相同内容都不发（这也是防回声的依据）");
Assert(!ClipboardSyncPolicy.IsTooLong(new string('x', ClipboardSyncPolicy.MaxTextLength)) && ClipboardSyncPolicy.IsTooLong(new string('x', ClipboardSyncPolicy.MaxTextLength + 1)), "剪贴板大小上限的边界判断必须准确");
// ---------- 共享链路：同机回环真配对 ----------
const int peerPort = 47899;
const string peerSubjectA = "MonitorSwitcher Test A";
const string peerSubjectB = "MonitorSwitcher Test B";
var peerFileA = Path.Combine(Path.GetTempPath(), $"peer-a-{Guid.NewGuid():N}.json");
var peerFileB = Path.Combine(Path.GetTempPath(), $"peer-b-{Guid.NewGuid():N}.json");
var peerSettingsA = new PeerSettingsService(peerFileA);
var peerSettingsB = new PeerSettingsService(peerFileB);
await peerSettingsA.SaveSettingsAsync(new PeerSettings { LocalName = "机器A", Port = peerPort });
await peerSettingsB.SaveSettingsAsync(new PeerSettings { LocalName = "机器B", Port = peerPort + 1 });
PeerIdentity.Remove(peerSubjectA);
PeerIdentity.Remove(peerSubjectB);
using var frameStream = new MemoryStream();
await FrameCodec.WriteAsync(frameStream, FrameType.Text, [1, 2, 3], default);
frameStream.Position = 0;
var decodedFrame = await FrameCodec.ReadAsync(frameStream, default);
Assert(decodedFrame?.Type == FrameType.Text && decodedFrame.Value.Payload.SequenceEqual(new byte[] { 1, 2, 3 }), "帧编解码必须原样往返");
frameStream.SetLength(3);
frameStream.Position = 0;
Assert(await FrameCodec.ReadAsync(frameStream, default) is null, "被截断的帧必须返回 null 而不是抛异常");
var linkA = new PeerLink(peerSettingsA, PeerIdentity.LoadOrCreate(peerSubjectA));
var linkB = new PeerLink(peerSettingsB, PeerIdentity.LoadOrCreate(peerSubjectB));
await linkA.StartAsync();
var pairingCode = linkA.BeginPairing();
Assert(pairingCode.Length == 6 && pairingCode.All(char.IsDigit), "配对码必须是 6 位数字");
var arrivedAtB = new TaskCompletionSource<string>();
linkB.TextReceived += text => arrivedAtB.TrySetResult(text);
Assert(!await linkB.ConnectAsync("127.0.0.1", "000000", peerPort) && linkB.State == PeerLinkState.Failed, "配对码错误时必须拒绝，不能建立连接");
Assert(linkA.LastError?.Contains("配对码") == true && !linkA.IsConnected, $"错误配对码必须被服务端以配对码理由拒绝（实际原因：{linkA.LastError ?? "无" }）");
Assert(!linkA.IsConnected && linkA.LocalPairingCode == pairingCode, "一次错误尝试不能作废还没用过的配对码");
Assert(await linkB.ConnectAsync("127.0.0.1", pairingCode, peerPort) && linkA.IsConnected, "正确配对码必须让双方进入已连接");
var pinnedOnA = (await peerSettingsA.LoadAsync()).PinnedPeers;
var pinnedOnB = (await peerSettingsB.LoadAsync()).PinnedPeers;
var pinnedOnAFromFile = (await new PeerSettingsService(peerFileA).LoadAsync()).PinnedPeers;
var pinnedOnBFromFile = (await new PeerSettingsService(peerFileB).LoadAsync()).PinnedPeers;
Assert(pinnedOnAFromFile.Any(peer => peer.Name == linkB.LocalName) && pinnedOnBFromFile.Any(peer => peer.Name == "机器A"), $"配对成功后双方必须互相 pin 对端指纹（A={pinnedOnAFromFile.Count} 条，B={pinnedOnBFromFile.Count} 条）");
Assert(await linkA.SendTextAsync("你好"), "已连接时发送文本帧必须成功");
Assert(await arrivedAtB.Task.WaitAsync(TimeSpan.FromSeconds(3)) == "你好", "文本帧必须原样到达对端");
await linkB.DisconnectAsync();
await Task.Delay(400);
Assert(!linkA.IsConnected && linkA.State == PeerLinkState.Unpaired, "对端主动断开后本机必须退出已连接状态");
Assert(await linkB.ConnectAsync("127.0.0.1", code: null, peerPort) && linkA.IsConnected, "已 pin 的对端必须能免配对码重连");
await linkA.DisposeAsync();
await linkB.DisposeAsync();
PeerIdentity.Remove(peerSubjectA);
PeerIdentity.Remove(peerSubjectB);
File.Delete(peerFileA);
File.Delete(peerFileB);
// ---------- 文件名净化与接收落盘 ----------
Assert(FileNameSafety.Sanitize(@"..\..\Windows\evil.txt") == "evil.txt", "路径穿越必须只留最后一段文件名");
Assert(FileNameSafety.Sanitize(@"C:\temp\report.docx") == "report.docx", "带盘符的绝对路径必须只留文件名");
Assert(FileNameSafety.Sanitize("..") == "未命名文件" && FileNameSafety.Sanitize(null) == "未命名文件" && FileNameSafety.Sanitize("   ") == "未命名文件", "空名和纯点必须退回默认名");
var sanitizedOdd = FileNameSafety.Sanitize("a<b>c:d|e?.txt");
Assert(!sanitizedOdd.Any(character => "<>:\"|?*".Contains(character)) && sanitizedOdd.EndsWith(".txt"), $"非法字符必须替换掉并保留扩展名（实际：{sanitizedOdd}）");
Assert(FileNameSafety.Sanitize("CON.txt") == "_CON.txt" && FileNameSafety.Sanitize("nul") == "_nul", "Windows 保留设备名必须加前缀，否则落地即失效");
Assert(FileNameSafety.Sanitize(string.Concat(Enumerable.Repeat("名", 200)) + ".txt").Length <= 120, "超长文件名必须截断");
var uniqueDir = Path.Combine(Path.GetTempPath(), $"unique-{Guid.NewGuid():N}");
Directory.CreateDirectory(uniqueDir);
var uniqueFirst = FileNameSafety.UniquePath(uniqueDir, "a.txt");
File.WriteAllText(uniqueFirst, "1");
var uniqueSecond = FileNameSafety.UniquePath(uniqueDir, "a.txt");
Assert(uniqueSecond != uniqueFirst && Path.GetFileName(uniqueSecond) == "a (2).txt", "同名文件必须自动加序号，不能覆盖已有文件");
Directory.Delete(uniqueDir, true);
var evilDir = Path.Combine(Path.GetTempPath(), $"evil-{Guid.NewGuid():N}");
var evilContext = new FileReceiveContext(new FileOffer([new FileOfferFile(@"..\..\evil.txt", 5)]), evilDir);
evilContext.Write("hello"u8.ToArray());
var evilLanded = evilContext.Complete();
evilContext.Dispose();
Assert(evilLanded.Count == 1 && Path.GetDirectoryName(evilLanded[0]) == evilDir && File.ReadAllText(evilLanded[0]) == "hello", "恶意路径文件名必须被限制在接收目录内");
Assert(!File.Exists(Path.Combine(evilDir, "..", "..", "evil.txt")), "接收目录之外不能出现任何文件");
Directory.Delete(evilDir, true);
// ---------- 文件传输：同机回环真发真收 ----------
var xferDir = Path.Combine(Path.GetTempPath(), $"xfer-{Guid.NewGuid():N}");
Directory.CreateDirectory(xferDir);
var xferTextPath = Path.Combine(xferDir, "小文件 报告.txt");
File.WriteAllText(xferTextPath, "中文内容 hello");
var xferBigPath = Path.Combine(xferDir, "big.bin");
var xferPayload = new byte[300 * 1024];
RandomNumberGenerator.Fill(xferPayload);
File.WriteAllBytes(xferBigPath, xferPayload);
var xferFileA = Path.Combine(xferDir, "peer-a.json");
var xferFileB = Path.Combine(xferDir, "peer-b.json");
var xferSettingsA = new PeerSettingsService(xferFileA);
var xferSettingsB = new PeerSettingsService(xferFileB);
await xferSettingsA.SaveSettingsAsync(new PeerSettings { LocalName = "发送方", Port = peerPort + 10 });
await xferSettingsB.SaveSettingsAsync(new PeerSettings { LocalName = "接收方", Port = peerPort + 11 });
const string xferSubjectA = "MonitorSwitcher Xfer A";
const string xferSubjectB = "MonitorSwitcher Xfer B";
PeerIdentity.Remove(xferSubjectA);
PeerIdentity.Remove(xferSubjectB);
var xferA = new PeerLink(xferSettingsA, PeerIdentity.LoadOrCreate(xferSubjectA));
var xferB = new PeerLink(xferSettingsB, PeerIdentity.LoadOrCreate(xferSubjectB));
await xferA.StartAsync();
Assert(await xferB.ConnectAsync("127.0.0.1", xferA.BeginPairing(), peerPort + 10), "文件传输用例的配对必须成功");
var seenOffers = new List<FileOffer>();
var willAccept = false;
xferB.OfferHandler = offer => { seenOffers.Add(offer); return Task.FromResult(willAccept); };
var landedFiles = new TaskCompletionSource<IReadOnlyList<string>>();
xferB.FilesReceived += paths => landedFiles.TrySetResult(paths);
Assert(!await xferA.SendFilesAsync([xferTextPath]), "对端拒绝时发送必须返回失败");
Assert(seenOffers.Count == 1 && seenOffers[0].Files.Single().Name == "小文件 报告.txt" && seenOffers[0].TotalBytes == new FileInfo(xferTextPath).Length, "接收方必须看到原始文件名和总大小");
Assert(!Directory.Exists(xferB.ReceiveDirectory) || Directory.GetFiles(xferB.ReceiveDirectory).Length == 0, "被拒绝的传输不能在接收目录留下任何文件");
willAccept = true;
Assert(await xferA.SendFilesAsync([xferTextPath, xferBigPath]), "同意后两个文件都必须传完");
var xferLanded = await landedFiles.Task.WaitAsync(TimeSpan.FromSeconds(15));
Assert(xferLanded.Count == 2 && File.ReadAllText(xferLanded[0]) == "中文内容 hello", "小文件内容必须逐字一致");
Assert(new FileInfo(xferLanded[1]).Length == xferPayload.Length && File.ReadAllBytes(xferLanded[1]).AsSpan().SequenceEqual(xferPayload), "跨多个数据块的大文件必须逐字节一致");
await xferA.DisposeAsync();
await xferB.DisposeAsync();
PeerIdentity.Remove(xferSubjectA);
PeerIdentity.Remove(xferSubjectB);
if (Directory.Exists(xferB.ReceiveDirectory)) Directory.Delete(xferB.ReceiveDirectory, true);
Directory.Delete(xferDir, true);
if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
Console.WriteLine("Projection mode contract tests passed.");
Console.WriteLine("Per-monitor profile contract tests passed.");
Console.WriteLine("V2 monitor preferences/input learning contract tests passed.");
Console.WriteLine("Custom switch button editor contract tests passed.");
Console.WriteLine("Peer pairing and encrypted link contract tests passed.");
Console.WriteLine("File transfer and receive-path safety contract tests passed.");

void Assert(bool condition, string message) { if (!condition) failures.Add(message); }

sealed class FakeDdcCiService : IDdcCiService
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<IReadOnlyList<MonitorDevice>> RefreshAsync(IProgress<MonitorDevice>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MonitorDevice>>([]);
    public Task<uint?> ReadInputAsync(string monitorId, CancellationToken cancellationToken = default) => Task.FromResult<uint?>(null);
    public Task<DdcOperationResult> SetInputAsync(string monitorId, uint inputValue, CancellationToken cancellationToken = default) => Task.FromResult(new DdcOperationResult(true, 1, 0, TimeSpan.Zero));
}

sealed class FakeProjectionService : IProjectionService
{
    public bool WasCalled { get; private set; }
    public ProjectionMode? CurrentMode => null;
    public Task<ProjectionResult> ApplyAsync(ProjectionMode mode, CancellationToken cancellationToken = default) { WasCalled = true; return Task.FromResult(ProjectionResult.NoChange); }
    public Task<ProjectionMode?> RefreshCurrentModeAsync(CancellationToken cancellationToken = default) => Task.FromResult<ProjectionMode?>(null);
    public Task<TopologySnapshot> GetTopologySnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(new TopologySnapshot(ProjectionMode.Extend, 2));
}

sealed class FixedDdcCiService(uint value) : IDdcCiService
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<IReadOnlyList<MonitorDevice>> RefreshAsync(IProgress<MonitorDevice>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MonitorDevice>>([]);
    public Task<uint?> ReadInputAsync(string monitorId, CancellationToken cancellationToken = default) => Task.FromResult<uint?>(value);
    public Task<DdcOperationResult> SetInputAsync(string monitorId, uint inputValue, CancellationToken cancellationToken = default) => Task.FromResult(new DdcOperationResult(true, 1, 0, TimeSpan.Zero));
}

sealed class RecordingDdcCiService : IDdcCiService
{
    public Dictionary<string, uint> Writes { get; } = [];
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task<IReadOnlyList<MonitorDevice>> RefreshAsync(IProgress<MonitorDevice>? progress = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MonitorDevice>>([]);
    public Task<uint?> ReadInputAsync(string monitorId, CancellationToken cancellationToken = default) => Task.FromResult<uint?>(null);
    public Task<DdcOperationResult> SetInputAsync(string monitorId, uint inputValue, CancellationToken cancellationToken = default) { Writes[monitorId] = inputValue; return Task.FromResult(new DdcOperationResult(true, 1, 0, TimeSpan.Zero)); }
}
