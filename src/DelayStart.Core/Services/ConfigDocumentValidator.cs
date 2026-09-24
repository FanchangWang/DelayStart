using System.Globalization;
using System.Text.Json;

using DelayStart.Core.Models;

namespace DelayStart.Core.Services;

/// <summary>配置 JSON 文档的格式类别。</summary>
internal enum ConfigDocumentFormat
{
    /// <summary>可迁移的 demo v1 格式。</summary>
    LegacyV1,

    /// <summary>当前 v2 格式。</summary>
    CurrentV2,

    /// <summary>高于当前程序支持范围的未来格式。</summary>
    Future,
}

/// <summary>配置文档的格式判定结果。</summary>
/// <param name="Format">格式类别。</param>
/// <param name="Version">文档声明的版本；无显式版本的 v1 使用 1。</param>
internal readonly record struct ConfigDocumentInfo(ConfigDocumentFormat Format, int Version);

/// <summary>
/// 配置 JSON 的版本识别与 v2 恢复元数据校验。
/// </summary>
/// <remarks>
/// 这一层刻意在 <see cref="ConfigService"/> 的 <c>Normalize</c> 之前工作：规范化会补
/// <c>Manual</c>、空字符串和默认 <c>OriginalState</c>，若先规范化就无法区分“字段缺失”
/// 与“用户明确使用了默认值”。所有检查只依赖 <see cref="JsonElement"/> 和 Core 模型，
/// 不引入反射或 Management 依赖，保持 AOT 边界。
/// </remarks>
internal static class ConfigDocumentValidator
{
    private static readonly HashSet<string> V2RootMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "settings",
        "cycles",
    };

    private static readonly HashSet<string> V2ItemMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "arguments",
        "sourceKey",
        "scope",
        "enabled",
        "originalState",
        "scheduleCycleId",
    };

    private static readonly HashSet<string> V1ItemMarkers = new(StringComparer.OrdinalIgnoreCase)
    {
        "args",
        "sourceKeyName",
    };

    /// <summary>
    /// 判定配置文档格式。明确的非法版本 / 模糊结构抛出 <see cref="FormatException"/>，
    /// 由 <see cref="ConfigService"/> 统一转换为 <c>Corrupt</c> 状态。
    /// </summary>
    /// <param name="root">JSON 根节点。</param>
    /// <returns>格式判定结果。</returns>
    public static ConfigDocumentInfo Detect(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw new FormatException("配置文件根节点不是 JSON 对象。");
        }

        if (TryGetProperty(root, "version", out var versionElement))
        {
            if (!TryReadVersion(versionElement, out var version))
            {
                throw new FormatException("配置 version 必须是整数。");
            }

            if (version > AppConfig.CurrentVersion)
            {
                return new ConfigDocumentInfo(ConfigDocumentFormat.Future, version);
            }

            if (version == 1)
            {
                ValidateLegacyShape(root);
                return new ConfigDocumentInfo(ConfigDocumentFormat.LegacyV1, version);
            }

            if (version == 2)
            {
                return new ConfigDocumentInfo(ConfigDocumentFormat.CurrentV2, version);
            }

            throw new FormatException($"配置版本 v{version} 不是受支持的版本。");
        }

        // 没有 version 时，只有明确的旧格式才允许迁移；v2 专属字段或无法判定的
        // 空对象都不能被“缺少 version”顺手解释成 v1。
        if (ContainsV2Marker(root))
        {
            throw new FormatException("配置缺少 version，但包含 v2 专属字段，拒绝按 v1 迁移。");
        }

        ValidateLegacyShape(root, requireLegacyEvidence: true);
        return new ConfigDocumentInfo(ConfigDocumentFormat.LegacyV1, 1);
    }

    /// <summary>
    /// 校验当前 v2 文档中会影响释放 / 恢复的字段。
    /// </summary>
    /// <param name="root">JSON 根节点。</param>
    public static void ValidateCurrentV2(JsonElement root)
    {
        if (root.ValueKind is not JsonValueKind.Object)
        {
            throw new FormatException("配置文件根节点不是 JSON 对象。");
        }

        if (ContainsV1Marker(root))
        {
            throw new FormatException("v2 配置混入 v1 专属字段，拒绝猜测字段含义。");
        }

        if (!TryGetProperty(root, "items", out var itemsElement)
            || itemsElement.ValueKind is not (JsonValueKind.Array or JsonValueKind.Null))
        {
            throw new FormatException("v2 配置缺少合法的 items 数组。");
        }

        if (itemsElement.ValueKind is JsonValueKind.Null)
        {
            return;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var itemElement in itemsElement.EnumerateArray())
        {
            var location = $"items[{index}]";
            if (itemElement.ValueKind is not JsonValueKind.Object)
            {
                throw new FormatException($"{location} 不是 JSON 对象。");
            }

            var id = ReadRequiredString(itemElement, "id", location);
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new FormatException($"{location}.id 不能为空。");
            }

            if (!ids.Add(id))
            {
                throw new FormatException($"配置条目 id 重复：{id}。");
            }

            var source = ReadSource(itemElement, location);
            var scope = ReadScope(itemElement, location);
            ValidateScope(source, scope, location);

            if (source is StartupSource.Manual)
            {
                ValidateManualFields(itemElement, location);
            }
            else
            {
                var sourceKey = ReadRequiredString(itemElement, "sourceKey", location);
                if (string.IsNullOrWhiteSpace(sourceKey))
                {
                    throw new FormatException($"{location}.sourceKey 不能为空。");
                }

                var originalState = ReadRequiredObject(itemElement, "originalState", location);
                var wasEnabled = ReadRequiredProperty(originalState, "wasEnabled", $"{location}.originalState");
                if (wasEnabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new FormatException($"{location}.originalState.wasEnabled 必须是布尔值。");
                }
            }

            index++;
        }
    }

    private static void ValidateLegacyShape(JsonElement root, bool requireLegacyEvidence = false)
    {
        if (ContainsV2Marker(root))
        {
            throw new FormatException("v1 配置混入 v2 专属字段，拒绝按 v1 迁移。");
        }

        if (!TryGetProperty(root, "items", out var itemsElement)
            || itemsElement.ValueKind is JsonValueKind.Null)
        {
            if (requireLegacyEvidence)
            {
                throw new FormatException("配置缺少 version，且无法确认是旧 v1 格式，拒绝猜测迁移。");
            }

            return;
        }

        if (itemsElement.ValueKind is not JsonValueKind.Array)
        {
            throw new FormatException("v1 配置的 items 必须是数组或 null。");
        }

        if (requireLegacyEvidence && !LooksLikeLegacy(root, itemsElement))
        {
            throw new FormatException("配置缺少 version，且无法确认是旧 v1 格式，拒绝猜测迁移。");
        }
    }

    private static bool LooksLikeLegacy(JsonElement root, JsonElement itemsElement)
    {
        // 真实 demo v1 由 PascalCase 序列化写出；根字段 Items 是最可靠的正向证据。
        if (HasExactProperty(root, "Items"))
        {
            return true;
        }

        // 即使用户把根字段改成了小写，只要仍保留 v1 专属字段，也应继续兼容。
        foreach (var item in itemsElement.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in item.EnumerateObject())
            {
                if (V1ItemMarkers.Contains(property.Name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryReadVersion(JsonElement element, out int version)
    {
        version = 0;
        return element.ValueKind is JsonValueKind.Number
            && int.TryParse(
                element.GetRawText(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out version);
    }

    private static bool ContainsV2Marker(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (V2RootMarkers.Contains(property.Name))
            {
                return true;
            }
        }

        if (!TryGetProperty(root, "items", out var itemsElement)
            || itemsElement.ValueKind is not JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in itemsElement.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in item.EnumerateObject())
            {
                if (V2ItemMarkers.Contains(property.Name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsV1Marker(JsonElement root)
    {
        if (!TryGetProperty(root, "items", out var itemsElement)
            || itemsElement.ValueKind is not JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in itemsElement.EnumerateArray())
        {
            if (item.ValueKind is not JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in item.EnumerateObject())
            {
                if (V1ItemMarkers.Contains(property.Name))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void ValidateScope(StartupSource source, StartupScope scope, string location)
    {
        var valid = source switch
        {
            StartupSource.Registry => scope is StartupScope.Hkcu or StartupScope.Hklm or StartupScope.HklmWow,
            StartupSource.StartupFolder => scope is StartupScope.UserFolder or StartupScope.SystemFolder,
            StartupSource.ScheduledTask or StartupSource.Uwp or StartupSource.Manual => scope is StartupScope.None,
            _ => false,
        };

        if (!valid)
        {
            throw new FormatException($"{location} 的 source / scope 组合无效：{source}/{scope}。");
        }
    }

    private static void ValidateManualFields(JsonElement item, string location)
    {
        var sourceKey = ReadOptionalString(item, "sourceKey", location);
        if (!string.IsNullOrWhiteSpace(sourceKey))
        {
            throw new FormatException($"{location}.sourceKey 对手动条目必须为空。");
        }

        var sourceDetail = ReadOptionalString(item, "sourceDetail", location);
        if (!string.IsNullOrWhiteSpace(sourceDetail))
        {
            throw new FormatException($"{location}.sourceDetail 对手动条目必须为空。");
        }
    }

    private static StartupSource ReadSource(JsonElement item, string location)
    {
        var element = ReadRequiredProperty(item, "source", location);
        if (element.ValueKind is JsonValueKind.String)
        {
            return element.GetString()?.ToLowerInvariant() switch
            {
                "registry" => StartupSource.Registry,
                "startupfolder" => StartupSource.StartupFolder,
                "scheduledtask" => StartupSource.ScheduledTask,
                "uwp" => StartupSource.Uwp,
                "manual" => StartupSource.Manual,
                _ => throw new FormatException($"{location}.source 不是已知来源。"),
            };
        }

        if (element.ValueKind is JsonValueKind.Number
            && element.TryGetInt32(out var numericSource))
        {
            return numericSource switch
            {
                0 => StartupSource.Registry,
                1 => StartupSource.StartupFolder,
                2 => StartupSource.ScheduledTask,
                3 => StartupSource.Uwp,
                4 => StartupSource.Manual,
                _ => throw new FormatException($"{location}.source 不是已知来源。"),
            };
        }

        throw new FormatException($"{location}.source 必须是已知枚举。");
    }

    private static StartupScope ReadScope(JsonElement item, string location)
    {
        var element = ReadRequiredProperty(item, "scope", location);
        if (element.ValueKind is JsonValueKind.String)
        {
            return element.GetString()?.ToLowerInvariant() switch
            {
                "none" => StartupScope.None,
                "hkcu" => StartupScope.Hkcu,
                "hklm" => StartupScope.Hklm,
                "hklmwow" => StartupScope.HklmWow,
                "userfolder" => StartupScope.UserFolder,
                "systemfolder" => StartupScope.SystemFolder,
                _ => throw new FormatException($"{location}.scope 不是已知作用域。"),
            };
        }

        if (element.ValueKind is JsonValueKind.Number
            && element.TryGetInt32(out var numericScope))
        {
            return numericScope switch
            {
                0 => StartupScope.None,
                1 => StartupScope.Hkcu,
                2 => StartupScope.Hklm,
                3 => StartupScope.HklmWow,
                4 => StartupScope.UserFolder,
                5 => StartupScope.SystemFolder,
                _ => throw new FormatException($"{location}.scope 不是已知作用域。"),
            };
        }

        throw new FormatException($"{location}.scope 必须是已知枚举。");
    }

    private static string ReadRequiredString(JsonElement owner, string name, string location)
    {
        var element = ReadRequiredProperty(owner, name, location);
        if (element.ValueKind is not JsonValueKind.String || element.GetString() is not { } value)
        {
            throw new FormatException($"{location}.{name} 必须是字符串。");
        }

        return value;
    }

    private static string? ReadOptionalString(JsonElement owner, string name, string location)
    {
        if (!TryGetProperty(owner, name, out var element))
        {
            return null;
        }

        if (element.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind is not JsonValueKind.String)
        {
            throw new FormatException($"{location}.{name} 必须是字符串。");
        }

        return element.GetString();
    }

    private static JsonElement ReadRequiredObject(JsonElement owner, string name, string location)
    {
        var element = ReadRequiredProperty(owner, name, location);
        if (element.ValueKind is not JsonValueKind.Object)
        {
            throw new FormatException($"{location}.{name} 必须是 JSON 对象。");
        }

        return element;
    }

    private static JsonElement ReadRequiredProperty(JsonElement owner, string name, string location)
    {
        if (!TryGetProperty(owner, name, out var element))
        {
            throw new FormatException($"{location} 缺少 {name}。");
        }

        return element;
    }

    private static bool HasExactProperty(JsonElement owner, string name)
    {
        foreach (var property in owner.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetProperty(JsonElement owner, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        foreach (var property in owner.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found)
            {
                throw new FormatException($"配置属性 {name} 重复。");
            }

            found = true;
            value = property.Value;
        }

        return found;
    }
}
