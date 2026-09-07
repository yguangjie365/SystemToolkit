namespace SystemToolkit.Core.Software.Models;

/// <summary>
/// winget 源镜像定义：源名 + 镜像地址。
/// </summary>
/// <remarks>
/// 官方源（<c>winget</c> / <c>winget-font</c>）在国内网络环境下常出现连接超时或极慢，
/// 切换为镜像源可显著改善搜索与安装体验。
/// 镜像只是把 <c>source add</c> 的地址换掉，随时可用 <c>winget source reset</c> 还原为官方源，
/// 不会改动用户已安装的软件。
/// </remarks>
public sealed record WingetMirrorSource(string Name, string Url);
