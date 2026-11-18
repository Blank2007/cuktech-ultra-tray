# DevCheck（类库自检 / 真机验证）

`CuktechLogin` 的独立控制台验证程序（不属于类库，`CuktechLogin.csproj` 已排除 `DevCheck/**`）。

~~~powershell
# 默认: 申请二维码 -> 存 out\qr.png -> 打印请求 URL / 返回字段 / 有效期
dotnet run --project src\CuktechLogin\DevCheck\DevCheck.csproj -c Release

# 加密原语与 Python 参考实现的对拍向量
dotnet run --project src\CuktechLogin\DevCheck\DevCheck.csproj -c Release -- --crypto

# 轮询失败路径(过期/超时/网络异常)自测, 不需要扫码
dotnet run --project src\CuktechLogin\DevCheck\DevCheck.csproj -c Release -- --poll-negative

# 真机验证(需要手机米家 App 扫码): 扫码 -> serviceToken -> 区域探测 -> 设备列表
dotnet run --project src\CuktechLogin\DevCheck\DevCheck.csproj -c Release -- --scan
~~~

产物（`out\` 下，临时文件，可随时删）：

| 文件 | 内容 |
| --- | --- |
| `out\qr.png` | 本次二维码（可直接打开给米家 App 扫） |
| `out\qr-info.json` | 请求 URL、返回字段名、图片尺寸（不含 ssecurity / serviceToken） |
| `out\session.json` | `--scan` 成功后保存的登录会话（**含 serviceToken，敏感**） |

默认的二维码模式只覆盖「申请 + 下载」这一段 HTTP/SSL/解析链路；扫码之后的部分见
`src\CuktechLogin\README.md` 的"验证状态"。
