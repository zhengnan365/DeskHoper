using System.IO;

namespace MonitorSwitch.Services;

public sealed record FileOfferFile(string Name, long Size);

public sealed record FileOffer(IReadOnlyList<FileOfferFile> Files)
{
    public long TotalBytes => Files.Sum(file => file.Size);
}

internal sealed record FileOfferPayload(string TransferId, List<FileOfferItemPayload> Files);
internal sealed record FileOfferItemPayload(string Name, long Size);
internal sealed record FileDecisionPayload(string TransferId, bool Accepted);
internal sealed record FileEndPayload(string TransferId);

/// 接收端的落盘状态机：按声明顺序把连续的字节流切成一个个文件，先写 .part 再改名，
/// 中途断掉不会留下看起来完整的半成品。
internal sealed class FileReceiveContext : IDisposable
{
    private readonly string _directory;
    private readonly List<FileOfferFile> _files;
    private readonly long _total;
    private FileStream? _stream;
    private string? _partPath;
    private string? _finalPath;
    private long _remaining;
    private long _received;
    private int _index;

    public FileReceiveContext(FileOffer offer, string directory)
    {
        _directory = directory;
        _files = offer.Files.ToList();
        _total = offer.TotalBytes;
        Directory.CreateDirectory(directory);
        OpenNext();
    }

    public IReadOnlyList<string> ReceivedPaths { get; private set; } = [];

    /// 整批传输的完成度，界面上的接收进度就取这个。声明总长<=0 的走不到这里（发送端已经拦掉）。
    public int Percent => _total <= 0 ? 100 : (int)Math.Min(100, _received * 100 / _total);
    public long TotalBytes => _total;
    /// 实际落盘的字节数，不是对面声明的总量：对面少发了也不会在界面上虚报"已接收"。
    public long ReceivedBytes => _received;

    private void OpenNext()
    {
        CloseStream();
        if (_index >= _files.Count) return;
        // 净化放在这里而不是只靠调用方：落盘是安全边界，不能假设上游已经处理过文件名。
        var final = FileNameSafety.UniquePath(_directory, FileNameSafety.Sanitize(_files[_index].Name));
        _finalPath = final;
        _partPath = final + ".part";
        _stream = new FileStream(_partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        _remaining = _files[_index].Size;
    }

    public void Write(byte[] payload)
    {
        var offset = 0;
        while (offset < payload.Length)
        {
            if (_stream is null) return;                      // 声明的字节已经收满，多余的直接丢
            var take = (int)Math.Min(payload.Length - offset, _remaining);
            if (take > 0)
            {
                _stream.Write(payload, offset, take);
                _remaining -= take;
                _received += take;
                offset += take;
            }
            if (_remaining > 0) continue;
            var paths = ReceivedPaths.ToList();
            paths.Add(_finalPath!);
            ReceivedPaths = paths;
            _index++;
            OpenNext();
        }
    }

    public IReadOnlyList<string> Complete()
    {
        CloseStream();
        return ReceivedPaths;
    }

    private void CloseStream()
    {
        _stream?.Dispose();
        _stream = null;
        if (_partPath is not null && _finalPath is not null && File.Exists(_partPath))
        {
            try { File.Move(_partPath, _finalPath, false); }
            catch (Exception ex) { _ = AppLogger.ErrorAsync($"接收文件改名失败: {_partPath}", ex); }
            _partPath = null;
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        if (_partPath is not null) { try { File.Delete(_partPath); } catch { } }
    }
}
