using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace DshLauncher.Services;

/// <summary>
/// 单文件（single-file）发布后，exe 旁边不再有 <c>tools\</c> 与 <c>rescue\profile\</c>，
/// 而 node / pwsh 必须拿到**磁盘上的真实路径**才能执行。所以运行时数据以
/// <see cref="System.Reflection.Assembly"/> 的 EmbeddedResource 形式嵌进程序集，
/// 第一次用到时解包到
/// <c>%LOCALAPPDATA%\DshLauncher\assets\&lt;8位内容哈希&gt;\</c>，之后直接复用同一条路径。
///
/// 解包目录刻意复刻仓库布局（<c>tools\</c> 与 <c>rescue\profile\</c> 同级并列），
/// 因此 <c>tools\rescue-dsh.ps1</c> 里由 <c>$PSCommandPath</c> 推导
/// <c>$ScriptDir</c>/<c>$Root</c>/<c>$SnapProfile</c> 的逻辑无需任何改动即可命中解包出来的 profile。
/// </summary>
public static class EmbeddedAssets
{
    /// <summary>嵌入资源逻辑名前缀：<c>DshLauncher.Assets.</c> + 相对路径（分隔符换成 '.'）。</summary>
    private const string ResourcePrefix = "DshLauncher.Assets.";

    private const string ProductFolder = "DshLauncher";
    private const string AssetsFolderName = "assets";
    private const string LogFileName = "embedded-assets.log";
    /// <summary>记录上次解包的内容哈希，决定是否需要重写嵌入文件（快照不受影响）。</summary>
    private const string ContentMarkerFileName = "embedded-assets.content";

    /// <summary>嵌入的救援快照 zip（dsh\ + profile\ + manifest.json）逻辑名。</summary>
    private const string RescueSnapshotResource = ResourcePrefix + "rescue.snapshot.zip";

    /// <summary>profile\ 里需要被复制成 home\profiles\rescue\ 真实目录的那 3 个文件。</summary>
    private static readonly string[] RescueProfileFiles = { "package.json", "cordis.patch.yml", "cordis.yml" };

    /// <summary>一条嵌入资源：逻辑名 → 解包后的相对路径。</summary>
    private sealed record Asset(string ResourceName, string RelativePath);

    /// <summary>
    /// 唯一的一份映射表（资源逻辑名 ↔ 解包相对路径）。改任何一边都只改这里。
    /// 逻辑名必须与 src\DshLauncher.csproj 里 &lt;EmbeddedResource&gt; 的 LogicalName 逐字一致。
    /// </summary>
    private static readonly Asset[] Assets =
    {
        // ── tools\（node 直接执行的 .mjs；session-manager.mjs 会 import './dsh-session-log.mjs'，必须同目录）
        new(ResourcePrefix + "tools.dsh-session-log.mjs",   @"tools\dsh-session-log.mjs"),
        new(ResourcePrefix + "tools.session-manager.mjs",   @"tools\session-manager.mjs"),
        new(ResourcePrefix + "tools.mine-sessions.mjs",     @"tools\mine-sessions.mjs"),
        new(ResourcePrefix + "tools.distill-sessions.mjs",  @"tools\distill-sessions.mjs"),
        new(ResourcePrefix + "tools.test-session-repair.mjs", @"tools\test-session-repair.mjs"),
        new(ResourcePrefix + "tools.rescue-dsh.ps1",        @"tools\rescue-dsh.ps1"),
        // ── rescue\profile\（rescue 快照用的 profile；rescue-dsh.ps1 verify 要求 package.json + cordis.patch.yml）
        new(ResourcePrefix + "rescue.profile.package.json",     @"rescue\profile\package.json"),
        new(ResourcePrefix + "rescue.profile.cordis.patch.yml", @"rescue\profile\cordis.patch.yml"),
        new(ResourcePrefix + "rescue.profile.cordis.yml",       @"rescue\profile\cordis.yml"),
    };

    /// <summary>
    /// 空目录无法作为文件资源嵌入，但它们是 Node 模块解析的占位（阻止向上查找父级 node_modules），
    /// 解包时补建，保持 profile 目录与仓库里逐项一致。
    /// </summary>
    private static readonly string[] EmptyDirectories =
    {
        @"rescue\profile\node_modules",
        @"rescue\profile\.dsh-module-fallback\node_modules",
    };

    private static readonly object Gate = new();
    private static string? _assetsRoot;
    private static string[]? _resourceNames;

    /// <summary>
    /// 解包全部嵌入资源，返回 <c>%LOCALAPPDATA%\DshLauncher\assets</c>。
    /// 幂等：进程内第二次调用直接返回缓存路径，不做任何 IO。
    ///
    /// 为什么【不是】按内容哈希分子目录
    /// ---------------------------------
    /// 起初解包到 <c>assets\&lt;8位哈希&gt;\</c>。问题是救援快照不在该目录里、
    /// 而是由 <c>rescue-dsh.ps1 create</c> 建在脚本父目录的旁边，也就是
    /// <c>assets\&lt;哈希&gt;\rescue\dsh\</c>（212.7 MB）。嵌入的脚本一改，哈希就变，
    /// 那 212.7 MB 立刻变成 LOCALAPPDATA 里的孤儿，而用户完全看不见。
    ///
    /// 所以改为稳定目录 + 内容标记文件：标记一致就直接复用（含快照），
    /// 不一致才重写嵌入的代码文件。快照本身不参与哈希，也就不会被反复遗弃。
    /// </summary>
    public static string EnsureAssets()
    {
        var cached = _assetsRoot;
        if (cached is not null) return cached;

        lock (Gate)
        {
            if (_assetsRoot is not null) return _assetsRoot;

            // 先把全部资源读进内存：既用于算内容哈希，也用于落盘。
            var payload = new Dictionary<string, byte[]>(Assets.Length, StringComparer.Ordinal);
            foreach (var asset in Assets) payload[asset.ResourceName] = ReadRequiredResource(asset);

            string hash = ComputeContentHash(payload);
            string root = Path.Combine(GetLocalAppDataRoot(), ProductFolder, AssetsFolderName);
            string marker = Path.Combine(root, ContentMarkerFileName);

            try
            {
                Directory.CreateDirectory(root);
                if (!MarkerMatches(marker, hash))
                {
                    foreach (var asset in Assets)
                        WriteIfNeeded(root, asset.RelativePath, payload[asset.ResourceName]);
                    foreach (var dir in EmptyDirectories)
                        Directory.CreateDirectory(Path.Combine(root, dir));
                    File.WriteAllText(marker, hash);
                }
            }
            catch (Exception ex)
            {
                // 解包失败时说明是哪个目录/资源，不要让调用方看到 bare IOException。
                var message = $"[EmbeddedAssets] failed to extract embedded assets to '{root}': {ex.GetType().Name}: {ex.Message}";
                Log(message);
                throw new InvalidOperationException(message, ex);
            }

            Log($"[EmbeddedAssets] assets ready: {root} ({Assets.Length} files, {payload.Sum(p => (long)p.Value.Length)} bytes, content {hash})");
            _assetsRoot = root;
            return root;
        }
    }

    // ─────────────────────────── 救援快照（懒解包内嵌 zip）───────────────────────────

    /// <summary>
    /// 保证救援快照在磁盘上，返回 <c>%LOCALAPPDATA%\DshLauncher\assets\rescue</c>。
    /// 快照以压缩 zip（<see cref="RescueSnapshotResource"/>，约 61.4 MB）整包嵌入程序集，
    /// 单文件 exe 不再依赖机器上现成的 DSH 安装；只从救援面板首次用到时调用（懒加载），
    /// 绝不在应用启动时执行。
    ///
    /// 幂等：<c>manifest.json</c> 与 <c>dsh\dsh\lib\bin.js</c> 都已存在即直接返回（零解包）。
    /// 否则读 zip 字节，用内建 System.IO.Compression 解包进 root，再补齐救援 home：
    ///   - home\profiles\rescue\ = profile\ 的 3 个文件（真实目录，非 junction，无需特权）；
    ///   - home\settings.yaml / home\.credentials.yaml（缺则从 %USERPROFILE%\.dsh 复制）。
    /// </summary>
    public static string EnsureRescueSnapshot()
    {
        string root = Path.Combine(EnsureAssets(), "rescue");
        if (SnapshotExtracted(root)) return root;

        lock (Gate)
        {
            if (SnapshotExtracted(root)) return root;

            var sw = Stopwatch.StartNew();
            byte[] zipBytes = ReadRequiredResourceBytes(RescueSnapshotResource, @"rescue\rescue-snapshot.zip");

            try
            {
                Directory.CreateDirectory(root);
                long extractedBytes = 0;
                int fileCount = 0;

                using (var ms = new MemoryStream(zipBytes, writable: false))
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        string? dest = SafeZipTarget(root, entry.FullName);
                        if (dest is null) continue;

                        if (string.IsNullOrEmpty(entry.Name))
                        {
                            Directory.CreateDirectory(dest); // 目录项（含空目录占位）
                            continue;
                        }

                        string? parent = Path.GetDirectoryName(dest);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        using (var es = entry.Open())
                        using (var fs = File.Create(dest))
                        {
                            es.CopyTo(fs);
                        }
                        extractedBytes += entry.Length;
                        fileCount++;
                    }
                }

                EnsureRescueHome(root);

                Log($"[EmbeddedAssets] rescue snapshot extracted to '{root}' ({fileCount} files, {extractedBytes} bytes, {sw.Elapsed.TotalSeconds:0.0}s)");
            }
            catch (Exception ex)
            {
                var message = $"[EmbeddedAssets] failed to extract rescue snapshot to '{root}': {ex.GetType().Name}: {ex.Message}";
                Log(message);
                throw new InvalidOperationException(message, ex);
            }

            return root;
        }
    }

    /// <summary>快照是否已解包到位（manifest.json + 入口 bin.js 都在才算）。</summary>
    private static bool SnapshotExtracted(string root)
        => File.Exists(Path.Combine(root, "manifest.json"))
        && File.Exists(Path.Combine(root, "dsh", "dsh", "lib", "bin.js"));

    /// <summary>
    /// 补齐救援 home：home\profiles\rescue 必须是含 profile\ 3 个文件的真实目录
    /// （junction/symlink → 只删链接本身、重建真实目录），再保证 home\settings.yaml 与
    /// home\.credentials.yaml 在位（缺则从 %USERPROFILE%\.dsh 复制）。全程幂等。
    /// </summary>
    private static void EnsureRescueHome(string rescueRoot)
    {
        string profileDir = Path.Combine(rescueRoot, "profile");
        string home = Path.Combine(rescueRoot, "home");
        string profilesRescue = Path.Combine(home, "profiles", "rescue");

        EnsureRealDirectory(profilesRescue);
        foreach (string name in RescueProfileFiles)
        {
            string src = Path.Combine(profileDir, name);
            string dst = Path.Combine(profilesRescue, name);
            if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst, overwrite: false);
        }

        // 优先 USERPROFILE/HOME 环境变量：GetFolderPath(UserProfile) 在服务/沙盒 token 下会被
        // 重定向到 systemprofile，导致 .dsh 源找不到；普通桌面进程两者一致，用环境变量更稳。
        string userProfile = Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Environment.GetEnvironmentVariable("HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile)) userProfile = GetLocalAppDataRoot();
        string realHome = Path.Combine(userProfile, ".dsh");

        foreach (string name in new[] { "settings.yaml", ".credentials.yaml" })
        {
            string src = Path.Combine(realHome, name);
            string dst = Path.Combine(home, name);
            if (File.Exists(src) && !File.Exists(dst))
            {
                Directory.CreateDirectory(home);
                File.Copy(src, dst, overwrite: false);
            }
        }
    }

    /// <summary>路径已存在但为 junction/symlink（重解析点）时只删链接本身（绝不动目标），再建真实目录。</summary>
    private static void EnsureRealDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(path, recursive: false); // 只删链接，不递归进目标
            }
        }
        catch
        {
            // 无法识别/删除遗留重解析点时，交给下面的 CreateDirectory 报出真实错误。
        }
        Directory.CreateDirectory(path);
    }

    /// <summary>
    /// 把 zip 条目路径安全地映射到 root 下的绝对路径；绝对路径 / 目录穿越返回 null。
    /// 自己的 zip，这只是防御性兜底——最终以 GetFullPath 后是否仍在 root 下为准。
    /// </summary>
    private static string? SafeZipTarget(string root, string entryName)
    {
        string normalized = entryName.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0) return root; // zip 根目录项
        if (normalized.StartsWith("/", StringComparison.Ordinal)) return null;

        string full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        string rootFull = Path.GetFullPath(root);
        if (!full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        return full;
    }

    /// <summary>标记文件与当前内容哈希一致 = 嵌入资源未变，无需重写（快照因此得以保留）。</summary>
    private static bool MarkerMatches(string markerPath, string hash)
    {
        try
        {
            return File.Exists(markerPath) &&
                   string.Equals(File.ReadAllText(markerPath).Trim(), hash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解包后的 session-manager.mjs（会话 CLI）绝对路径。</summary>
    public static string SessionManager => Path.Combine(EnsureAssets(), @"tools\session-manager.mjs");

    /// <summary>解包后的 rescue-dsh.ps1 绝对路径。</summary>
    public static string RescueScript => Path.Combine(EnsureAssets(), @"tools\rescue-dsh.ps1");

    /// <summary>解包后的 rescue profile 目录绝对路径（profile 内的 package.json / cordis.patch.yml 与之同级）。</summary>
    public static string RescueProfileDir => Path.Combine(EnsureAssets(), @"rescue\profile");

    // ─────────────────────────── 落盘 ───────────────────────────

    /// <summary>
    /// 目标已存在且长度一致 → 直接跳过（幂等且第二次几乎零成本）；
    /// 否则写 &lt;target&gt;.tmp 后同目录 File.Move 覆盖，保证任何时刻都不会有半截文件被别人读到。
    /// </summary>
    private static void WriteIfNeeded(string root, string relativePath, byte[] content)
    {
        string target = Path.Combine(root, relativePath);

        var existing = new FileInfo(target);
        if (existing.Exists && existing.Length == content.Length) return;

        string? parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        string temp = target + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, target, overwrite: true);
    }

    // ─────────────────────────── 资源读取 ───────────────────────────

    private static byte[] ReadRequiredResource(Asset asset)
        => ReadRequiredResourceBytes(asset.ResourceName, asset.RelativePath);

    /// <summary>按逻辑名读一条嵌入资源；缺失时列出程序集全部资源名并抛错（重命名不会静默失效）。</summary>
    private static byte[] ReadRequiredResourceBytes(string resourceName, string relativePath)
    {
        var assembly = typeof(EmbeddedAssets).Assembly;
        Stream? stream = null;
        try
        {
            stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                var message =
                    $"[EmbeddedAssets] embedded resource is MISSING: '{resourceName}' " +
                    $"(wanted at '{relativePath}') in assembly '{assembly.GetName().Name}'. " +
                    $"Available embedded resources ({GetResourceNames().Length}): [{string.Join(", ", GetResourceNames())}]";
                Log(message);
                throw new InvalidOperationException(message);
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        finally
        {
            stream?.Dispose();
        }
    }

    /// <summary>枚举一次 GetManifestResourceNames() 并缓存，用于成功日志与缺失诊断。</summary>
    private static string[] GetResourceNames()
    {
        var names = _resourceNames;
        if (names is not null) return names;

        try
        {
            names = typeof(EmbeddedAssets).Assembly.GetManifestResourceNames();
        }
        catch (Exception ex)
        {
            names = new[] { $"<GetManifestResourceNames failed: {ex.Message}>" };
        }

        Array.Sort(names, StringComparer.Ordinal);
        _resourceNames = names;
        return names;
    }

    // ─────────────────────────── 内容哈希 / 路径 / 日志 ───────────────────────────

    /// <summary>内容哈希（前 8 位十六进制）：资源名 + 长度 + 内容，资源改名或改内容都会换目录。</summary>
    private static string ComputeContentHash(Dictionary<string, byte[]> payload)
    {
        var names = payload.Keys.ToArray();
        Array.Sort(names, StringComparer.Ordinal);

        using var sha = SHA256.Create();
        using var buffer = new MemoryStream();
        foreach (var name in names)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name);
            byte[] lengthBytes = BitConverter.GetBytes((long)payload[name].Length);
            buffer.Write(nameBytes, 0, nameBytes.Length);
            buffer.WriteByte(0);
            buffer.Write(lengthBytes, 0, lengthBytes.Length);
            buffer.Write(payload[name], 0, payload[name].Length);
        }

        return Convert.ToHexString(sha.ComputeHash(buffer.ToArray()))[..8].ToLowerInvariant();
    }

    private static string GetLocalAppDataRoot()
    {
        string? local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();
        return local;
    }

    /// <summary>诊断日志：Trace/Debug + 追加到 assets\embedded-assets.log（日志失败绝不影响启动）。</summary>
    private static void Log(string message)
    {
        Trace.WriteLine(message, nameof(EmbeddedAssets));
        Debug.WriteLine(message, nameof(EmbeddedAssets));

        try
        {
            string dir = Path.Combine(GetLocalAppDataRoot(), ProductFolder, AssetsFolderName);
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, LogFileName),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // 只记日志，绝不因为写日志失败而中断。
        }
    }
}
