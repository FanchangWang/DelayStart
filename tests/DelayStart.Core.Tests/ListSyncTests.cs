using System.Collections.ObjectModel;

using DelayStart.Core.Services;

namespace DelayStart.Core.Tests;

/// <summary>
/// <see cref="ListSync"/> 的行为测试（D141）。
/// </summary>
/// <remarks>
/// 🔴 这一组钉两件不同的事，缺一不可：
/// <list type="number">
/// <item><description><b>结果对</b> —— 同步完必须与目标逐位相同。这靠随机对拍穷举。</description></item>
/// <item><description><b>通知少</b> —— 集合改动次数必须接近最小。这靠一组"理想值"断言。
/// 旧算法正是"结果对但通知多"，而多出来的通知会让 WinUI <c>ListView</c>
/// 销毁重建整屏行容器（用户看到的就是"有新增时增量、有移除时全量"）。</description></item>
/// </list>
/// </remarks>
public sealed class ListSyncTests
{
    /// <summary>一个可区分的"行对象"。用引用相等而不是 <c>Equals</c>，与真实场景一致。</summary>
    private sealed class Row
    {
        public Row(int id) => Id = id;

        public int Id { get; }

        public override string ToString() => $"R{Id}";
    }

    /// <summary>套上集合改动计数。</summary>
    /// <remarks>
    /// 🔴 分成两个计数器，因为它们对用户的意义完全不同：
    /// <c>Move</c> 只是换个位置，WinUI <c>ListView</c> <b>不销毁容器</b>；
    /// 而 <c>Add</c>/<c>Remove</c>/索引赋值每一条都要新建或销毁一个行容器 ——
    /// 行内按钮闪一下、滚动位置丢焦点，都是它们造成的。
    /// </remarks>
    private sealed class CountingCollection<T> : ObservableCollection<T>
    {
        public int Moves { get; private set; }

        public int Rebuilds { get; private set; }

        public int Edits
        {
            get => Moves + Rebuilds;
        }

        public void Reset()
        {
            Moves = 0;
            Rebuilds = 0;
        }

        protected override void InsertItem(int index, T item)
        {
            Rebuilds++;
            base.InsertItem(index, item);
        }

        protected override void RemoveItem(int index)
        {
            Rebuilds++;
            base.RemoveItem(index);
        }

        protected override void SetItem(int index, T item)
        {
            Rebuilds++;
            base.SetItem(index, item);
        }

        protected override void MoveItem(int oldIndex, int newIndex)
        {
            Moves++;
            base.MoveItem(oldIndex, newIndex);
        }
    }

    private static Row[] Rows(params int[] ids) => [.. ids.Select(static id => new Row(id))];

    private static int[] Ids(IEnumerable<Row> rows) => [.. rows.Select(static row => row.Id)];

    /// <summary>
    /// 从同一批实例里挑出目标 —— <b>必须复用对象</b>。
    /// </summary>
    /// <remarks>
    /// 🔴 真实场景里"内容没变的行"在两次渲染之间是**同一个对象**
    /// （<c>Apply</c> 按 Id 建旧行索引，内容一样就沿用旧对象）。这里若每次都
    /// <c>new Row</c>，引用永不相等，算法就会把每一格都 Replace ——
    /// 那测的就不是"差量"，而是"整表重建"，期望值也会跟着一起错。
    /// </remarks>
    private static Row[] Pick(Row[] source, IEnumerable<int> indexes)
        => [.. indexes.Select(index => source[index])];

    // ── 通知次数：这一组才是本次修复的目的 ───────────────────────────────

    [Theory]
    [InlineData("unchanged", 0)]
    [InlineData("one-content-changed", 1)]
    [InlineData("remove-middle-3", 3)]
    [InlineData("remove-head-3", 3)]
    [InlineData("remove-tail-3", 3)]
    [InlineData("insert-head-1", 1)]
    [InlineData("insert-middle-1", 1)]
    public void Apply_CommonShapes_EmitTheMinimumNumberOfEdits(string shape, int expected)
    {
        // 🔴 这些数字是**理想值**，不是"当前实现的输出"——
        // 把当前实现的输出抄成期望值，这条用例就永远不会红，也就毫无意义。
        var forty = Rows([.. Enumerable.Range(0, 40)]);

        Row[] desired = shape switch
        {
            "unchanged" => Pick(forty, Enumerable.Range(0, 40)),

            // 同一位置换了一个**新对象** = "这一行内容变了"（状态翻转 / 换图标）。
            // 🔴 这是**最高频**的改动，所以它必须是 1 次，不能为了照顾删除而变贵。
            "one-content-changed" =>
                [.. Pick(forty, Enumerable.Range(0, 19)), new Row(999), .. Pick(forty, Enumerable.Range(20, 20))],

            "remove-middle-3" => Pick(forty, Enumerable.Range(0, 10).Concat(Enumerable.Range(13, 27))),
            "remove-head-3" => Pick(forty, Enumerable.Range(3, 37)),
            "remove-tail-3" => Pick(forty, Enumerable.Range(0, 37)),

            // 新装的应用按名字排在最前面 / 中间 —— 现实里非常常见。
            "insert-head-1" => [new Row(50), .. Pick(forty, Enumerable.Range(0, 40))],
            "insert-middle-1" => [.. Pick(forty, Enumerable.Range(0, 20)), new Row(50), .. Pick(forty, Enumerable.Range(20, 20))],
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        var current = new CountingCollection<Row>();
        foreach (var row in forty)
        {
            current.Add(row);
        }

        current.Reset();
        ListSync.Apply(current, desired);

        Assert.Equal(Ids(desired), Ids(current));
        Assert.Equal(expected, current.Edits);

        // 🔴 这几种形态里**一次 Move 都不需要**（顺序本来就对，变的只是增删），
        // 所以所有改动都必须是"销毁/新建容器"那一类，且恰好是预期那么多。
        Assert.Equal(0, current.Moves);
    }

    [Fact]
    public void Apply_Reorder_UsesMoveSoNoContainerIsRebuilt()
    {
        // 🔴 排序变化是"同一批对象、位置全变"。若走 Add/Remove 或索引赋值，
        // 每一行都会被销毁重建 —— 排一次序整屏行都没了（这正是 F10.3 里
        // 记下的那个坑）。所以这里断言的是**重建次数为 0**，而不是某个 Move 数字：
        // 最优 Move 数是个纯算术细节，说不出理由的数字钉了也没意义。
        var rows = Rows([.. Enumerable.Range(0, 40)]);

        var current = new CountingCollection<Row>();
        foreach (var row in rows)
        {
            current.Add(row);
        }

        // 复用同一批实例（见 Pick 的说明）：重排的前提就是"每个对象都还在"。
        var reversed = Pick(rows, Enumerable.Range(0, 40).Reverse());

        current.Reset();
        ListSync.Apply(current, reversed);

        Assert.Equal(Ids(reversed), Ids(current));
        Assert.Equal(0, current.Rebuilds);
        Assert.True(current.Moves > 0, "一次 Move 都没发，那是怎么重排的？");
    }

    // ── 结果正确性：随机对拍 ─────────────────────────────────────────────

    [Fact]
    public void Apply_ResultAlwaysMatchesTarget_OnRandomInputs()
    {
        // 固定种子：出问题必须能复现。两侧都唯一（真实场景 id 唯一）。
        var random = new Random(20261002);
        var pool = Enumerable.Range(0, 12).ToArray();

        for (var iteration = 0; iteration < 20_000; iteration++)
        {
            var currentIds = Sample(random, pool);
            var desiredIds = Sample(random, pool);

            if (currentIds.Distinct().Count() != currentIds.Length)
            {
                continue;
            }

            if (desiredIds.Distinct().Count() != desiredIds.Length)
            {
                continue;
            }

            var current = Build(currentIds);
            var desired = Build(desiredIds);

            ListSync.Apply(current, desired);

            Assert.Equal(desiredIds, Ids(current));
        }
    }

    [Fact]
    public void Apply_DoesNotAssumeCurrentIsUnique()
    {
        // 🔴 算法不许把"调用方一定传了唯一项"当前提：current 带重复项时
        // "按不该再出现来删"会删不干净，剩下的多出来几行没有任何一步会去裁。
        // 对拍时抓到过这个反例：current=[8,10,9,11] want=[5,8,1] 得 [5,8,1,9]。
        var current = Build([8, 10, 9, 11]);
        var desired = Build([5, 8, 1]);

        ListSync.Apply(current, desired);

        Assert.Equal([5, 8, 1], Ids(current));
    }

    [Fact]
    public void Apply_NonObservableList_FallsBackToRemoveInsert()
    {
        // 非 ObservableCollection 的调用方也必须能用（Move 发不出通知时退化成两条）。
        var current = new List<Row>();
        current.AddRange(Build([0, 1, 2, 3]));

        var desired = Build([3, 2, 1, 0]);
        ListSync.Apply(current, desired);

        Assert.Equal([3, 2, 1, 0], Ids(current));
    }

    [Fact]
    public void Apply_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(
            () => ListSync.Apply(current: null!, Build([1])));

        Assert.Throws<ArgumentNullException>(
            () => ListSync.Apply(new List<Row>(), desired: null!));
    }

    private static int[] Sample(Random random, int[] pool)
    {
        var length = random.Next(0, 14);
        var result = new int[length];

        for (var i = 0; i < length; i++)
        {
            result[i] = pool[random.Next(pool.Length)];
        }

        return result;
    }

    /// <summary>按 id 建一批**各不相同的**行对象（与真实场景一致：行对象不可变）。</summary>
    private static CountingCollection<Row> Build(int[] ids)
    {
        var collection = new CountingCollection<Row>();
        foreach (var id in ids)
        {
            collection.Add(new Row(id));
        }

        collection.Reset();
        return collection;
    }
}