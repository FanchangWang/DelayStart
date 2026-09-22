using System.Text;

namespace DelayStart.Core.Services;

/// <summary>
/// 调度完成通知的文案组装（N2，2026-09-22 批复）。纯函数，供单测与调度端共用。
/// </summary>
/// <remarks>
/// <para>
/// 结构（与守卫 toast 同款风格）：
/// <list type="bullet">
/// <item><description>标题恒为 <see cref="Title"/>（首行加粗，系统会在旁边另行渲染应用名）。</description></item>
/// <item><description>正文 = 「启动完成：N 项成功 · M 项失败 · K 项跳过」（0 的分段省略）；
/// 有失败时换行列出前 <see cref="MaxFailedNames"/> 条失败名称，超出折成「等 N 项」。</description></item>
/// </list>
/// </para>
/// <para>
/// 🔴 超长**截断而不是报错**：toast 正文没有硬上限，但通知中心对超长文本体验极差；
/// 截断点统一收在这里，调度端与测试看同一个结果。
/// </para>
/// </remarks>
public static class ScheduleToastComposer
{
    /// <summary>通知标题（系统渲染的应用名之上的那一行）。</summary>
    public const string Title = "DelayStart";

    /// <summary>失败条目名最多列几个（超出折成「等 N 项」）。</summary>
    public const int MaxFailedNames = 3;

    /// <summary>正文总长上限（含失败列表；超出在此截断）。</summary>
    public const int MaxMessageLength = 220;

    /// <summary>组装通知正文。</summary>
    /// <param name="doneCount">成功条目数。</param>
    /// <param name="failedCount">失败条目数。</param>
    /// <param name="skippedCount">跳过条目数（防双启动命中等）。</param>
    /// <param name="failedNames">失败条目名（按本批次顺序）。</param>
    /// <returns>可直接放进通知 <c>&lt;text&gt;</c> 的正文。</returns>
    public static string ComposeMessage(
        int doneCount,
        int failedCount,
        int skippedCount,
        IReadOnlyList<string> failedNames)
    {
        ArgumentNullException.ThrowIfNull(failedNames);

        var builder = new StringBuilder("启动完成：");
        _ = builder.Append(doneCount).Append(" 项成功");

        if (failedCount > 0)
        {
            _ = builder.Append(" · ").Append(failedCount).Append(" 项失败");
        }

        if (skippedCount > 0)
        {
            _ = builder.Append(" · 跳过 ").Append(skippedCount);
        }

        if (failedCount > 0)
        {
            _ = builder.AppendLine();
            var listed = Math.Min(failedNames.Count, MaxFailedNames);
            for (var index = 0; index < listed; index++)
            {
                if (index > 0)
                {
                    _ = builder.Append('、');
                }

                _ = builder.Append(failedNames[index]);
            }

            if (failedNames.Count > MaxFailedNames)
            {
                _ = builder.Append(" 等 ").Append(failedNames.Count).Append(" 项");
            }
        }

        var message = builder.ToString();
        return message.Length <= MaxMessageLength
            ? message
            : message[..(MaxMessageLength - 1)] + "…";
    }
}
