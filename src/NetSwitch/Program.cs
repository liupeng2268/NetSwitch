using System.Text;
using System.Windows.Forms;
using NetSwitch.Cli;
using NetSwitch.Config;
using NetSwitch.Infrastructure;
using NetSwitch.Models;
using NetSwitch.Network;
using NetSwitch.Ui;

namespace NetSwitch;

/// <summary>
/// 程序入口。对应旧版第 3867-3929 行的模式分发逻辑。
///
/// 职责只有三件事：
///   1. 解析命令行参数
///   2. 装配服务（<see cref="AppServices"/>）
///   3. 分发到 CLI 或图形界面
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppOptions options;
        try
        {
            options = ParseArguments(args);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"命令行参数有误：{ex.Message}", "NetSwitch",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        // 配置文件路径：显式指定优先，否则按「程序同目录 → %APPDATA%」查找
        var configPath = !string.IsNullOrWhiteSpace(options.ConfigPath)
            ? options.ConfigPath!
            : Paths.GetDefaultConfigPath();

        Log.IsCliMode = options.HasCliAction;

        if (options.HasCliAction)
        {
            // WinExe 默认没有控制台，先挂到父终端才能打印
            ConsoleBridge.AttachToParent();
        }

        // 日志必须先初始化：配置加载失败的 ERROR 要能落进日志文件
        Log.Initialize(configPath, UiContract.Version);

        try
        {
            var services = BuildServices(options, configPath);

            if (options.HasCliAction)
            {
                var exitCode = new CliRunner().Run(services);
                ConsoleBridge.Detach();
                return exitCode;
            }

            // 排查用：-DialogsShot <目录> 依次截取三个对话框
            if (!string.IsNullOrWhiteSpace(options.DialogsShotDir))
            {
                return RunDialogsShot(services, options.DialogsShotDir!);
            }

            return RunGui(services, options.ShotDir);
        }
        catch (Exception ex)
        {
            Log.Error($"程序启动失败：{ex.Message}");

            if (options.HasCliAction)
            {
                Console.WriteLine($"程序启动失败：{ex.Message}");
            }
            else
            {
                MessageBox.Show($"程序启动失败：{ex.Message}\r\n\r\n日志：{Log.LogFile}",
                    "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            ConsoleBridge.Detach();
            return 1;
        }
    }

    // ── 服务装配 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 装配全部服务。
    ///
    /// 依赖顺序（注意两处循环依赖的破法）：
    ///   1. RouteRegistry ⇄ CimNetworkApi 互为依赖 → 双方都用属性注入后再连起来
    ///   2. ProfileStore 需要 IConfigStore，SwitchEngine 需要 IProfileStore
    ///      → 先建 ProfileStore，再建 Engine，不存在循环
    /// </summary>
    private static AppServices BuildServices(AppOptions options, string configPath)
    {
        // 配置层
        var configBackup = new ConfigBackup();
        var configStore = new ConfigStore(configPath, configBackup);

        // 网络层：RouteRegistry 与 CimNetworkApi 互相依赖，先各自建好再用属性连起来
        var adapters = new AdapterService();
        var routes = new RouteRegistry(configPath);
        var networkApi = new CimNetworkApi { Routes = routes };
        routes.Network = networkApi;

        // 备份与快捷方式
        var backups = new NetworkBackupService(adapters, networkApi, configPath);
        var shortcuts = new ShortcutService();

        // 配置集操作（依赖 IConfigStore 与快捷方式）
        var profiles = new ProfileStore(configStore, shortcuts);

        // 切换引擎（依赖上面全部）
        var engine = new SwitchEngine(profiles, adapters, networkApi, routes, backups);

        var status = new StatusReader(adapters);
        var transfer = new ConfigTransfer();

        // 加载配置（必须在 Log.Initialize 之后，否则解析失败的 ERROR 落不进日志）
        configStore.Load();

        return new AppServices
        {
            Options = options,
            Config = configStore,
            Profiles = profiles,
            ConfigBackup = configBackup,
            Backups = backups,
            Transfer = transfer,
            Adapters = adapters,
            Network = networkApi,
            Routes = routes,
            Engine = engine,
            Status = status,
            Shortcuts = shortcuts,
        };
    }

    // ── 图形界面 ──────────────────────────────────────────────────────────

    private static int RunGui(AppServices services, string? shotDir = null)
    {
        ApplicationConfiguration.Initialize();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error($"界面异常：{e.Exception.Message}");
            MessageBox.Show($"出现未预期的错误：{e.Exception.Message}\r\n\r\n日志：{Log.LogFile}",
                "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };

        using var form = new MainForm(services);
        form.BindLogBox();

        Log.Ok($"界面已就绪：共 {services.Config.Data.Profiles.Count} 个配置集。新建/编辑后点「应用此配置集」即可切换网络。");

        // 排查用：启动后自动截图并退出，供开发期核对布局。
        // 目录名里带 buffer 时改跑双缓冲自检（不新增命令行参数，避免干扰用户）。
        if (!string.IsNullOrWhiteSpace(shotDir))
        {
            var checkBuffer = shotDir!.Contains("buffer", StringComparison.OrdinalIgnoreCase);
            if (checkBuffer)
            {
                form.Shown += (_, _) => SelfShot.CheckDoubleBufferAndExit(form, shotDir!);
            }
            else
            {
                form.Shown += (_, _) => SelfShot.CaptureAndExit(form, shotDir!);
            }
        }

        Application.Run(form);
        return 0;
    }

    /// <summary>
    /// 排查用：启动后依次打开各对话框并截图，然后退出。
    /// 用于核对「配置集编辑器 / 备份管理 / 网卡重绑」在 150% 缩放下的布局。
    /// </summary>
    private static int RunDialogsShot(AppServices services, string shotDir)
    {
        ApplicationConfiguration.Initialize();
        Directory.CreateDirectory(shotDir);

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        var profiles = services.Config.Data.Profiles;
        var sample = profiles.Count > 0 ? profiles[0] : null;
        var adapters = services.Adapters.GetAdapters();

        var pending = new Queue<(string Name, Func<Form> Factory)>();
        pending.Enqueue(("editor-new", () => new ProfileEditorForm(services, null)));
        if (sample is not null)
        {
            pending.Enqueue(("editor-edit", () => new ProfileEditorForm(services, sample)));
        }
        pending.Enqueue(("backup", () => new BackupManagerForm(services)));
        pending.Enqueue(("rebind", () => new AdapterRebindForm("已失效的网卡名", adapters)));

        void RunNext()
        {
            if (pending.Count == 0)
            {
                Application.Exit();
                return;
            }

            var (name, factory) = pending.Dequeue();
            var dialog = factory();
            dialog.Shown += async (_, _) =>
            {
                try
                {
                    await Task.Delay(600);

                    var rect = dialog.ClientRectangle;
                    if (rect.Width <= 0 || rect.Height <= 0)
                    {
                        rect = new Rectangle(0, 0, dialog.Width, dialog.Height);
                    }

                    using var bmp = new Bitmap(rect.Width, rect.Height);
                    dialog.DrawToBitmap(bmp, rect);
                    bmp.Save(Path.Combine(shotDir, name + ".png"),
                        System.Drawing.Imaging.ImageFormat.Png);

                    File.WriteAllText(
                        Path.Combine(shotDir, name + "-info.txt"),
                        $"client={rect.Width}x{rect.Height} dpi={dialog.DeviceDpi}");
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(shotDir, name + "-error.txt"), ex.ToString());
                }
                finally
                {
                    dialog.Close();
                }
            };
            dialog.FormClosed += (_, _) =>
            {
                dialog.Dispose();
                RunNext();
            };
            dialog.Show();
        }

        RunNext();
        Application.Run();
        return 0;
    }

    // ── 参数解析 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 解析命令行参数。模仿 PowerShell 的宽松风格：
    /// 参数名大小写不敏感，<c>-Name value</c> 与 <c>-Switch</c> 可混用。
    ///
    /// 支持：-Apply / -ListProfiles / -ListAdapters / -ShowStatus / -ImportFile /
    ///       -ExportFile / -Silent / -ConfigPath / -Elevated / -DpiMode / -DumpLayout
    /// </summary>
    internal static AppOptions ParseArguments(string[] args)
    {
        var options = new AppOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg)) continue;

            // 允许 -Name 与 --Name 两种写法
            var name = arg.TrimStart('-', '/').ToLowerInvariant();

            // 取下一个参数作为值（若存在且不像另一个开关）
            string? NextValue()
            {
                if (i + 1 >= args.Length) return null;
                var next = args[i + 1];
                if (next.StartsWith('-') && next.Length > 1 && !char.IsDigit(next[1])) return null;
                i++;
                return next;
            }

            switch (name)
            {
                case "apply":
                    options.Apply = NextValue();
                    break;

                case "listprofiles":
                    options.ListProfiles = true;
                    break;

                case "listadapters":
                    options.ListAdapters = true;
                    break;

                case "showstatus":
                    options.ShowStatus = true;
                    break;

                case "importfile":
                    options.ImportFile = NextValue();
                    break;

                case "exportfile":
                    options.ExportFile = NextValue();
                    break;

                case "silent":
                    options.Silent = true;
                    break;

                case "configpath":
                    options.ConfigPath = NextValue();
                    break;

                case "elevated":
                    options.Elevated = true;
                    break;

                case "dpimode":
                    options.DpiMode = NextValue();
                    break;

                case "dumplayout":
                    options.DumpLayout = NextValue();
                    break;

                case "shotdir":
                case "shot":
                    options.ShotDir = NextValue();
                    break;

                case "dialogsshot":
                case "dialogshots":
                    options.DialogsShotDir = NextValue();
                    break;

                default:
                    // 未知参数不阻断，与 PowerShell 的宽松风格一致
                    break;
            }
        }

        return options;
    }
}
