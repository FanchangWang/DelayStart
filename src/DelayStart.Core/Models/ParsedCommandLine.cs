namespace DelayStart.Core.Models;

/// <summary>
/// 从注册表值或快捷方式参数解析出的「可执行文件路径 + 参数」二元组。
/// </summary>
/// <param name="Path">可执行文件路径（已去除包裹引号）。</param>
/// <param name="Arguments">启动参数；无参数时为空串。</param>
/// <remarks>
/// 值语义（<c>readonly record struct</c>）让单元测试可以直接做相等断言，
/// 不必逐字段比较。
/// </remarks>
public readonly record struct ParsedCommandLine(string Path, string Arguments);
