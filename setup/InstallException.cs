namespace CuktechTraySetup;

/// <summary>安装/卸载过程中可直接展示给用户的错误。</summary>
internal sealed class InstallException : Exception
{
    public InstallException(string message) : base(message) { }
    public InstallException(string message, Exception? inner) : base(message, inner) { }

    /// <summary>建议进程用哪个退出码结束。</summary>
    public int ExitCode { get; init; } = 1;
}
