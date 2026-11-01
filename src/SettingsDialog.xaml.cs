using System.IO;
using System.Windows;
using DshLauncher.Models;
using DshLauncher.Services;
using Microsoft.Win32;

namespace DshLauncher;

public partial class SettingsDialog : Window
{
    public LauncherConfig Config { get; private set; }

    public SettingsDialog(LauncherConfig currentConfig)
    {
        InitializeComponent();
        Config = currentConfig;

        TxtDshPath.Text = Config.DshPath;
        TxtConfigPath.Text = Config.ConfigPath;
        TxtNodePath.Text = Config.NodePath;
    }

    private void BtnBrowseDshDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 DSH 根目录 (如 node_modules/@deepseek-ai/dsh)"
        };
        if (dialog.ShowDialog() == true)
        {
            TxtDshPath.Text = dialog.FolderName;
        }
    }

    private void BtnBrowseDshFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 DSH 启动入口文件",
            Filter = "DSH 启动文件 (*.cmd;*.bat;*.js;*.exe;package.json)|*.cmd;*.bat;*.js;*.exe;package.json|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            TxtDshPath.Text = dialog.FileName;
        }
    }

    private void BtnBrowseConfigDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择配置 Profile 目录 (如 ~/.dsh/profiles/web)"
        };
        if (dialog.ShowDialog() == true)
        {
            TxtConfigPath.Text = dialog.FolderName;
        }
    }

    private void BtnBrowseConfigFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择配置文件 (cordis.yml / config.json / patch.yml)",
            Filter = "YAML 配置文件 (*.yml;*.yaml)|*.yml;*.yaml|JSON 配置文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            TxtConfigPath.Text = dialog.FileName;
        }
    }

    private void BtnBrowseNode_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 node.exe 路径",
            Filter = "Node 可执行文件 (node.exe)|node.exe|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            TxtNodePath.Text = dialog.FileName;
        }
    }

    private void BtnAutoDetect_Click(object sender, RoutedEventArgs e)
    {
        var detected = ConfigService.DetectEnvironment();
        if (!string.IsNullOrEmpty(detected.DshPath)) TxtDshPath.Text = detected.DshPath;
        if (!string.IsNullOrEmpty(detected.ConfigPath)) TxtConfigPath.Text = detected.ConfigPath;
        if (!string.IsNullOrEmpty(detected.NodePath)) TxtNodePath.Text = detected.NodePath;
        MessageBox.Show("已自动重新探测并填充本机 DSH 与 Node.js 路径！", "探测完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnOpenProfileFolder_Click(object sender, RoutedEventArgs e)
    {
        string path = TxtConfigPath.Text.Trim();
        if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? "";
        if (Directory.Exists(path))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
        }
        else
        {
            var defaultProfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
            if (Directory.Exists(defaultProfile))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"\"{defaultProfile}\"");
            }
            else
            {
                MessageBox.Show("配置目录不存在", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        Config.DshPath = TxtDshPath.Text.Trim();
        Config.ConfigPath = TxtConfigPath.Text.Trim();
        Config.NodePath = TxtNodePath.Text.Trim();

        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
