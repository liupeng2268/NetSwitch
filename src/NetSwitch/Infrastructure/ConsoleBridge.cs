using System.Runtime.InteropServices;

namespace NetSwitch.Infrastructure;

/// <summary>
/// 控制台桥接 —— 解决「WinExe 不留黑窗」与「CLI 要能打印」的矛盾。
///
/// 背景：项目输出类型设为 <c>WinExe</c>，双击时不会弹出黑窗口（这是旧版 .bat 的痛点）。
/// 但 <c>WinExe</c> 进程默认没有控制台，<c>Console.WriteLine</c> 无处可去，
/// 连 <c>Console.OutputEncoding</c> 都会抛 <c>IOException</c>。
///
/// 解法：CLI 模式下调用 <c>AttachConsole(ATTACH_PARENT_PROCESS)</c> 挂到父进程
/// （cmd / PowerShell 窗口）的控制台上。
///   • 从已有终端启动 → 输出直接回到那个终端，与旧版体验一致
///   • 由快捷方式 / 双击启动且父进程无控制台 → 附加失败，此时不打印（也不报错）
///
/// 附加成功后必须重设输出编码与重定向流，否则中文会乱码或输出丢失。
/// </summary>
public static class ConsoleBridge
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    /// <summary>是否已成功连上控制台。</summary>
    public static bool IsAttached { get; private set; }

    /// <summary>
    /// 尝试连上父进程控制台。返回是否可用。
    /// 若调用方已在有控制台的环境（如 dotnet run 或从终端启动）中，则直接视为可用。
    /// </summary>
    public static bool AttachToParent()
    {
        if (IsAttached) return true;

        try
        {
            // 已有控制台（从终端启动的场景）就直接可用
            if (HasConsole())
            {
                IsAttached = true;
                ConfigureStreams();
                return true;
            }

            if (AttachConsole(AttachParentProcess))
            {
                IsAttached = true;
                ConfigureStreams();
                return true;
            }
        }
        catch
        {
            // 附加失败不致命：CLI 仍可执行，只是没有控制台输出
        }

        return false;
    }

    /// <summary>为便携场景自建一个控制台（一般不需要，留作后用）。</summary>
    public static bool CreateNew()
    {
        if (IsAttached) return true;
        try
        {
            if (AllocConsole())
            {
                IsAttached = true;
                ConfigureStreams();
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>断开控制台（CLI 结束前调用，避免父终端继续被占用）。</summary>
    public static void Detach()
    {
        if (!IsAttached) return;
        try { FreeConsole(); } catch { }
        IsAttached = false;
    }

    private static bool HasConsole()
    {
        try
        {
            // 有控制台时 GetConsoleWindow 返回非零句柄
            return GetConsoleWindow() != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    /// <summary>
    /// 附加控制台后，标准流句柄已变化，必须重新绑定并设好编码，
    /// 否则 Console.Write 不输出、中文乱码。
    /// </summary>
    private static void ConfigureStreams()
    {
        try
        {
            var stdout = Console.OpenStandardOutput();
            var writer = new StreamWriter(stdout, System.Text.Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(writer);

            var stderr = Console.OpenStandardError();
            var errWriter = new StreamWriter(stderr, System.Text.Encoding.UTF8) { AutoFlush = true };
            Console.SetError(errWriter);

            var stdin = Console.OpenStandardInput();
            Console.SetIn(new StreamReader(stdin, System.Text.Encoding.UTF8));
        }
        catch
        {
            // 重定向失败时保持默认流
        }

        try
        {
            // 代码页 65001 = UTF-8，保证中文与 √ ! X 等符号正常
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // WinExe 无控制台时该属性会抛 IOException —— 忽略即可
        }

        // ── 中文路径下的一个老问题 ──
        // WinExe 子进程若由旧版 cmd 以 GBK 代码页启动，控制台输入编码需一并放宽；
        // 失败不影响输出。
        try
        {
            Console.InputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
        }
    }
}
