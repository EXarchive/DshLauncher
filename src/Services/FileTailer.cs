using System.IO;
using System.Text;

namespace DshLauncher.Services;

/// <summary>
/// 增量读取一个正在被别的进程写入的日志文件，按行回调。
///
/// 为什么需要它：只要子进程的输出被重定向（管道或文件），它的控制台窗口就是空的 ——
/// 一个输出流只能去一个地方。所以「控制台窗口里能看到输出 **同时** 启动器也捕获到」
/// 只能靠 Tee：让子进程把输出同时写进管道/控制台和文件，启动器再从文件尾随读取。
/// 提权模式更是别无选择（提权进程根本无法用管道读）。
///
/// 三个必须处理的细节，少一个都会出错：
///   1. 用 FileShare.ReadWrite 打开，否则与正在写入的子进程互相锁死；
///   2. 保留**未完成的行**（没有换行符的尾巴），否则一行分两次写入会被记成两条或半截；
///   3. 文件变小（截断 / 轮转）时位置归零重读；
///   4. 停止前做一次收尾抽取，否则进程刚死时最后几行会丢。
/// </summary>
public sealed class FileTailer : IDisposable
{
    private readonly string _path;
    private readonly bool _isError;
    private readonly Action<string, bool> _onLine;
    private readonly StringBuilder _pending = new();
    private long _pos;
    private CancellationTokenSource? _cts;
    private Task? _task;

    /// <summary>
    /// 编码只探测一次并记住。原因：BOM 只存在于文件开头，而我们每次都从中间位置新建
    /// StreamReader —— 后一轮没有 BOM 可看就会退回 UTF-8，把 UTF-16 文本读成
    /// 「t i c k」（字符之间夹着 0x00）。Windows PowerShell 5.1 的 Tee-Object /
    /// Out-File 默认就是写 UTF-16LE，所以这条必须处理。
    /// </summary>
    private Encoding? _enc;

    /// <summary>已探测到的 BOM 长度，首轮读取时要跳过。</summary>
    private int _bomLength;

    public FileTailer(string path, bool isError, Action<string, bool> onLine)
    {
        _path = path;
        _isError = isError;
        _onLine = onLine;
    }

    public void Start(int intervalMs = 200)
    {
        Stop();
        _pending.Clear();
        _pos = 0;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _task = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                Pump();
                try { await Task.Delay(intervalMs, ct); } catch { break; }
            }
            Pump();   // 收尾：把最后几行也取出来
        }, ct);
    }

    /// <summary>停止并做最后一次抽取，把死掉进程的收尾输出也交给调用方。</summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _task?.Wait(1500); } catch { }
        _cts?.Dispose();
        _cts = null;
        _task = null;
    }

    private void Pump()
    {
        try
        {
            if (!File.Exists(_path)) return;
            using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < _pos) { _pos = 0; _enc = null; _bomLength = 0; }   // 被截断/轮转：重头读
            DetectEncodingOnce(fs);
            if (fs.Length <= _pos) return;

            fs.Seek(_pos, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, _enc ?? Encoding.UTF8);
            string chunk = sr.ReadToEnd();
            _pos = fs.Position;

            _pending.Append(chunk);
            string all = _pending.ToString();
            int lastNl = all.LastIndexOf('\n');
            if (lastNl < 0) return;                              // 还没有完整的一行

            string complete = all.Substring(0, lastNl);
            _pending.Clear();
            _pending.Append(all.Substring(lastNl + 1));

            foreach (string raw in complete.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length > 0) _onLine(line, _isError);
            }
        }
        catch
        {
            // 读失败（文件被独占、正在重命名…）不是致命错误：下一轮再试。
        }
    }

    /// <summary>从文件头嗅一次 BOM，决定编码与需要跳过的字节数。</summary>
    private void DetectEncodingOnce(FileStream fs)
    {
        if (_enc != null) return;
        long save = fs.Position;
        try
        {
            fs.Seek(0, SeekOrigin.Begin);
            var head = new byte[3];
            int n = fs.Read(head, 0, 3);
            // 还看不出编码就先不决定：文件是先被创建、随后才写入内容的，此刻拍板成
            // UTF-8 会把稍后写入的 UTF-16 内容读错。留到下一轮再看。
            if (n < 2) return;
            if (n >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            {
                _enc = new UTF8Encoding(false);
                _bomLength = 3;
            }
            else if (n >= 2 && head[0] == 0xFF && head[1] == 0xFE)
            {
                _enc = Encoding.Unicode;
                _bomLength = 2;
            }
            else if (n >= 2 && head[0] == 0xFE && head[1] == 0xFF)
            {
                _enc = Encoding.BigEndianUnicode;
                _bomLength = 2;
            }
            else
            {
                _enc = new UTF8Encoding(false);
                _bomLength = 0;
            }
            if (_pos == 0) _pos = _bomLength;
        }
        finally
        {
            fs.Seek(save, SeekOrigin.Begin);
        }
    }

    public void Dispose() => Stop();
}
