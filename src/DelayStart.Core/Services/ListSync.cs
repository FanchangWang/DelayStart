using System.Collections.ObjectModel;

namespace DelayStart.Core.Services;

/// <summary>
/// 把一个集合**差量**同步成目标内容：只对真的变了的位置发通知（D141）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 元素类型必须是引用类型（约束写在 <see cref="Apply{T}"/> 上）——
/// 判等一律用<b>引用相等</b>，值类型走不了这条路。
/// </para>
/// <para>
/// 🔴 <b>为什么在 Core 而不是留在 <c>ItemsViewModel</c> 里</b>：调度端与管理端共用不到它，
/// 但它是**纯索引运算**、却已经因为写错而在真机上出过两次可见故障
/// （一次是行对象被整表重建，一次是来源过滤谓词取反）。纯逻辑放在没有测试工程的层
/// 就等于没有测试，而这类算法错起来**编译期毫无异状**。
/// </para>
/// <para>
/// 🔴 <b>为什么用引用相等而不是 <c>Equals</c></b>：行对象是不可变的 ——
/// 内容变了就换一个新实例。所以"前后两次出现同一个对象"就等于"一个字节都没变"。
/// 走 <c>Equals</c> 只会多算一遍却得到同样的结论。
/// </para>
/// <para>
/// 🔴 <b>为什么关心"发了几次通知"</b>：<c>ObservableCollection</c> 的每一个
/// <c>Add</c>/<c>Remove</c>/索引赋值都会让 WinUI 的 <c>ListView</c> <b>新建或销毁一个
/// 行容器</b>，而 <c>Move</c> 不会。所以"结果算对了"不等于"用户看得见的刷新是对的"——
/// 旧算法在删中间几项时会把尾部同样数量的行全部重建一遍，用户看到的就是"整列表闪一下"。
/// 那正是 F10.3 声称已修好、实际被架空的那件事。
/// </para>
/// </remarks>
public static class ListSync
{
    /// <summary>把 <paramref name="current"/> 差量同步成 <paramref name="desired"/>。</summary>
    /// <typeparam name="T">元素类型。</typeparam>
    /// <param name="current">就地更新的集合（通常是绑定给 <c>ListView.ItemsSource</c> 的那个）。</param>
    /// <param name="desired">目标内容；元素应唯一。</param>
    /// <remarks>
    /// 🔴 <b>不要求"输入唯一"</b>：<paramref name="current"/> 里若有多余的重复项，
    /// 删除阶段会删不干净，此时由兜底分支砍尾部收场。算法不把"调用方一定传了唯一项"
    /// 当作前提 —— 那类前提一旦被打破，症状是"列表尾部多出几行"，极难查。
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> 或 <paramref name="desired"/> 为 <see langword="null"/>。</exception>
    public static void Apply<T>(IList<T> current, IReadOnlyList<T> desired)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);

        // ① 共同前缀：原样不动 —— 这就是"差量"三个字的全部含义。
        var start = 0;
        while (start < current.Count && start < desired.Count && ReferenceEquals(current[start], desired[start]))
        {
            start++;
        }

        DropSurplus(current, desired, start);

        // ② 逐位补齐。
        for (var i = start; i < desired.Count; i++)
        {
            if (i < current.Count && ReferenceEquals(current[i], desired[i]))
            {
                continue;
            }

            if (i >= current.Count)
            {
                current.Add(desired[i]);
                continue;
            }

            // ③ 这一行在后面别处已经出现过 ⇒ 它只是**位置变了**，不该重建容器。
            //   ObservableCollection<T>.Move（.NET 9+）发的是一条真正的 Move 通知；
            //   自己用 RemoveAt + Insert 拼则会发两条 —— WinUI 对 Remove 是销毁容器、
            //   对 Add 是新建容器，于是排一次序整屏行都没了。
            var existing = IndexOfFrom(current, desired[i], i + 1);
            if (existing >= 0)
            {
                Move(current, existing, i);
                continue;
            }

            // ④ 这一行在本集合里根本不存在，且**确实还缺项**，而被顶掉的旧行后面还要 ——
            //   插进去而不是覆盖：插进去之后旧行会自己落到它该在的位置，
            //   后面的对位比较就全部命中了。
            //
            //   🔴 那个"确实还缺项"的条件不能省：省掉就 Insert 出比目标更长的集合，
            //   而没有任何一步会去裁它（对拍反例：a=[8,10,9,11] want=[5,8,1] got=[5,8,1,9]）。
            //
            //   对照：旧行后面**也不要**了（那就是"这一行内容变了"）时走 ⑤ 的赋值 ——
            //   插进去会让旧行继续挡路，后面每一格都要重新对位，一次改动能变成二十次。
            if (current.Count < desired.Count
                && current.Count > i
                && ContainsByReference(desired, current[i])
                && !ContainsByReference(current, desired[i]))
            {
                current.Insert(i, desired[i]);
                continue;
            }

            // ⑤ 内容确实变了（状态翻转 / 换图标）—— 这一格只能 Replace。
            current[i] = desired[i];
        }
    }

    /// <summary>删掉 <paramref name="current"/> 里多出来的元素。</summary>
    private static void DropSurplus<T>(IList<T> current, IReadOnlyList<T> desired, int start)
        where T : class
    {
        var surplus = current.Count - desired.Count;
        if (surplus <= 0)
        {
            return;
        }

        var removed = 0;

        // 🔴 **按"不该再出现"来删，而不是按位置硬砍**：旧算法是
        // `while (current.Count > desired.Count) RemoveAt(Count - 1)` ——
        // 删中间几项时它删掉的是**尾部同样多**的行，于是分叉点之后每一格都要
        // Replace，一屏容器全被重建（用户看到"刷新仍是全量"，2026-10-02 用户实测）。
        for (var i = current.Count - 1; i >= start && removed < surplus; i--)
        {
            if (!ContainsByReference(desired, current[i]))
            {
                current.RemoveAt(i);
                removed++;
            }
        }

        // 🔴 兜底：多出来的全是"也想要的"（current 自己带重复项）⇒ 只能砍尾部。
        while (removed < surplus)
        {
            current.RemoveAt(current.Count - 1);
            removed++;
        }
    }

    private static bool ContainsByReference<T>(IReadOnlyList<T> items, T target)
        where T : class
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], target))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <see cref="ContainsByReference{T}(IReadOnlyList{T}, T)"/> 的可写集合重载。
    /// </summary>
    /// <remarks>
    /// 🔴 写成两个重载而不是在调用处把 <c>IList&lt;T&gt;</c> 强转成
    /// <c>IReadOnlyList&lt;T&gt;</c>：后者在运行时对"只实现了 <c>IList&lt;T&gt;</c> 的集合"
    /// 会抛 <see cref="InvalidCastException"/>，而这里传进去的正是调用方的集合本身。
    /// </remarks>
    private static bool ContainsByReference<T>(IList<T> items, T target)
        where T : class
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], target))
            {
                return true;
            }
        }

        return false;
    }

    private static int IndexOfFrom<T>(IList<T> items, T target, int startIndex)
        where T : class
    {
        for (var i = startIndex; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], target))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>移动一个元素，优先用真正的 <c>Move</c>（发一条 Move 通知而不是两条）。</summary>
    private static void Move<T>(IList<T> items, int from, int to)
        where T : class
    {
        if (items is ObservableCollection<T> observable)
        {
            observable.Move(from, to);
            return;
        }

        var item = items[from];
        items.RemoveAt(from);
        items.Insert(to, item);
    }
}