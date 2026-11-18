namespace CuktechTray;

/// <summary>
/// 功率数字的取色与格式化。面板和任务栏小窗共用这一套，避免两处颜色漂移。
/// 这台机器单口上限 120W、四口合计也接近 120W，所以分档按 120W 满载来定。
/// </summary>
internal static class WattStyle
{
    internal static readonly Color Dim = Color.FromArgb(154, 160, 166);
    internal static readonly Color Ok = Color.FromArgb(52, 211, 153);
    internal static readonly Color Warn = Color.FromArgb(251, 191, 36);
    internal static readonly Color Bad = Color.FromArgb(248, 113, 113);
    internal static readonly Color Cool = Color.FromArgb(125, 211, 252);

    /// <summary>单个接口的功率档：0W 灰、小电流淡蓝、正常绿、偏高黄、接近单口上限红。</summary>
    internal static Color Port(double w) => w switch
    {
        >= 80 => Bad,
        >= 40 => Warn,
        >= 20 => Ok,
        > 0.05 => Cool,
        _ => Dim,
    };

    /// <summary>总功率的颜色分档：空载灰、起步淡蓝、过半绿、偏高黄、接近满功率红。</summary>
    internal static Color Total(double w) => w switch
    {
        >= 100 => Bad,
        >= 60 => Warn,
        >= 20 => Ok,
        > 0.05 => Cool,
        _ => Dim,
    };

    /// <summary>功率文本：100W 以上不带小数（三位数字再带小数就太长了），其余一位小数。</summary>
    internal static string Format(double w) => w >= 100 ? w.ToString("F0") : w.ToString("F1");
}
