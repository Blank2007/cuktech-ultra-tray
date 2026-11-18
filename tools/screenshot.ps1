param([string]$Out = "D:\dsh projects\cuktech-ultra-tray\docs\shot.png")
# 先声明本进程 DPI 感知：否则 125% 缩放下只会截到物理屏幕左上角那一块，右下角的内容会「消失」
Add-Type -Namespace W -Name Native -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
[void][W.Native]::SetProcessDPIAware()
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap ($bounds.Width), ($bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, (New-Object System.Drawing.Size($bounds.Width, $bounds.Height)))
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "saved $Out $($bounds.Width)x$($bounds.Height)"
