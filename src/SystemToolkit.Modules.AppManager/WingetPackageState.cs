namespace SystemToolkit.Modules.AppManager;

/// <summary>winget 包安装状态（复用自旧工程 EnvManager）。</summary>
public enum WingetPackageState
{
    Unknown,
    NotInstalled,
    Installed,
    Updatable,
}
