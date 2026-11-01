using System.IO;
using System.Text.Json;
using DshLauncher.Models;

namespace DshLauncher.Services;

public static class ConfigService
{
    private static readonly string AppDataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DshLauncher"
    );
    private static readonly string ConfigFilePath = Path.Combine(AppDataFolder, "launcher_settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static LauncherConfig LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var config = JsonSerializer.Deserialize<LauncherConfig>(json, JsonOptions);
                if (config != null)
                {
                    AutoDetectDefaultsIfEmpty(config);
                    return config;
                }
            }
        }
        catch
        {
            // Ignore error and fallback to defaults
        }

        var defaultConfig = new LauncherConfig();
        AutoDetectDefaultsIfEmpty(defaultConfig);
        return defaultConfig;
    }

    public static void SaveConfig(LauncherConfig config)
    {
        try
        {
            if (!Directory.Exists(AppDataFolder))
            {
                Directory.CreateDirectory(AppDataFolder);
            }
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save config: {ex.Message}");
        }
    }

    public static LauncherConfig DetectEnvironment()
    {
        var config = new LauncherConfig();
        AutoDetectDefaultsIfEmpty(config);
        return config;
    }

    public static void AutoDetectDefaultsIfEmpty(LauncherConfig config)
    {
        // 1. Detect DSH Path
        if (string.IsNullOrWhiteSpace(config.DshPath))
        {
            string candidate1 = @"D:\Program Files\nodejs\node_global\node_modules\@deepseek-ai\dsh";
            string candidate2 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"npm\node_modules\@deepseek-ai\dsh"
            );
            string candidate3 = @"C:\Program Files\nodejs\node_global\node_modules\@deepseek-ai\dsh";

            if (Directory.Exists(candidate1)) config.DshPath = candidate1;
            else if (Directory.Exists(candidate2)) config.DshPath = candidate2;
            else if (Directory.Exists(candidate3)) config.DshPath = candidate3;
            else
            {
                // Try finding 'dsh.cmd' or 'dsh' in PATH
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var dshCmd = Path.Combine(dir.Trim(), "dsh.cmd");
                    if (File.Exists(dshCmd))
                    {
                        config.DshPath = dshCmd;
                        break;
                    }
                }
            }
        }

        // 2. Detect Config / Profile Path
        if (string.IsNullOrWhiteSpace(config.ConfigPath))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var candidateConfig1 = Path.Combine(userProfile, @".dsh\profiles\web\cordis.yml");
            var candidateConfig2 = Path.Combine(userProfile, @".dsh\profiles\web");
            var candidateConfig3 = Path.Combine(userProfile, @".dsh\cordis.yml");

            if (File.Exists(candidateConfig1)) config.ConfigPath = candidateConfig1;
            else if (Directory.Exists(candidateConfig2)) config.ConfigPath = candidateConfig2;
            else if (File.Exists(candidateConfig3)) config.ConfigPath = candidateConfig3;
        }

        // 3. Detect Node.js
        if (string.IsNullOrWhiteSpace(config.NodePath))
        {
            var defaultNode = @"C:\Program Files\nodejs\node.exe";
            if (File.Exists(defaultNode)) config.NodePath = defaultNode;
            else
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var nodeExe = Path.Combine(dir.Trim(), "node.exe");
                    if (File.Exists(nodeExe))
                    {
                        config.NodePath = nodeExe;
                        break;
                    }
                }
            }
        }

        // 4. Default Working Directory
        if (string.IsNullOrWhiteSpace(config.WorkingDirectory))
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var pycharmProjects = Path.Combine(userProfile, "PycharmProjects");
            if (Directory.Exists(pycharmProjects)) config.WorkingDirectory = pycharmProjects;
            else config.WorkingDirectory = userProfile;
        }

        // 5. 主工作区实例（只种一个；多 agent 由 DSH 本体插件提供，不在此编排）
        config.Instances ??= new List<InstanceConfig>();
        if (config.Instances.Count == 0)
        {
            config.Instances.Add(new InstanceConfig
            {
                Name = "主工作区",
                Profile = "web",
                Port = config.WebPort > 0 ? config.WebPort : 3080,
                IsPrimary = true,
                Role = "当前实例 · 用户/决策"
            });
        }
    }
}
