namespace SystemToolkit.Core.Backup.Services;

/// <summary>规则库操作失败异常（保存/导入/导出规则时抛出，消息面向用户可直接展示）。</summary>
public sealed class RuleException : Exception
{
    /// <summary>以面向用户的消息创建异常。</summary>
    public RuleException(string message)
        : base(message)
    {
    }
}
