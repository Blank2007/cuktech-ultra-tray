param([int]$ProcId, [string]$Out)
Add-Type -Namespace W -Name U -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags); [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r); [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h); public struct RECT { public int Left, Top, Right, Bottom; }'
[void][W.U]::SetProcessDPIAware()
$p = Get-Process -Id $ProcId -ErrorAction Stop
$h = $p.MainWindowHandle
$r = New-Object W.U+RECT
[void][W.U]::GetWindowRect($h, [ref]$r)
$vis = [W.U]::IsWindowVisible($h)
Write-Output ("hwnd={0} visible={1} rect={2},{3},{4},{5}" -f $h, $vis, $r.Left, $r.Top, $r.Right, $r.Bottom)
[void][W.U]::SetWindowPos($h, [IntPtr](-1), 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040)
Start-Sleep -Milliseconds 900
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap ($bounds.Width), ($bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, (New-Object System.Drawing.Size($bounds.Width, $bounds.Height)))
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "saved $Out"
