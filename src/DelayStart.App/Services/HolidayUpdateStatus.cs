using System.ComponentModel;

using Microsoft.UI.Dispatching;

namespace DelayStart.App.Services;

/// <summary>
/// 节假日数据更新的共享可见状态：<b>谁在跑、跑到哪、上次跑得怎么样</b>。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：更新有两个触发方 —— 用户在设置页按按钮，以及管理端启动后的自动检查
/// （<see cref="HolidayAutoCheckService"/>）。用户只能看见前者的进度，于是"开关是开的、
/// 它到底跑了没有"在界面上**无处可查**。2026-09-23 真机反馈的"启动管理端没自动下载、
/// 进设置页也没看到提示"，一半原因就在这（另一半是自动检查本身有 bug）。
/// </para>
/// <para>
/// 🔴 **单例**：进度是全进程一份的事实，两个触发方必须往同一个对象上报 ——
/// 否则设置页（瞬态 ViewModel）只能看到自己那一次，自动检查跑得再起劲也是隐形的。
/// </para>
/// <para>
/// 🔴 **通知一律切回 UI 线程**：自动检查的续体跑在线程池线程上（服务内部
/// <c>ConfigureAwait(false)</c>），而 WinUI 的绑定更新必须在 UI 线程执行 ——
/// 从后台线程直接发 <c>PropertyChanged</c> 不是"偶尔不刷新"，是当场抛
/// <c>RPC_E_WRONG_THREAD</c>。构造发生在 UI 线程（容器在 <c>OnLaunched</c> 里解析），
/// 所以在这里抓住当时的 <see cref="DispatcherQueue"/> 是最省事也最可靠的做法。
/// </para>
/// <para>
/// 忙的判定用**计数**而不是布尔：两个触发方可以重叠（用户手动点「立即更新」时
/// 启动检查可能还没跑完），布尔会被先结束的那个复位，让界面在还有任务在跑时
/// 提前显示"空闲"。
/// </para>
/// </remarks>
public sealed class HolidayUpdateStatus : INotifyPropertyChanged
{
    /// <summary>进行中的默认文案。</summary>
    public const string DefaultActivityText = "正在检查并下载节假日数据，可能需要十几秒…";

    private readonly DispatcherQueue? _dispatcher;

    private int _busyCount;
    private bool _isBusy;
    private string _activityText = DefaultActivityText;
    private string _summaryText = string.Empty;

    /// <summary>构造状态对象。必须在 UI 线程上构造（见类型备注）。</summary>
    public HolidayUpdateStatus() => _dispatcher = DispatcherQueue.GetForCurrentThread();

    /// <summary>属性变化通知（绑定源）。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>是否有更新正在进行。</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            Raise(nameof(IsBusy));
            Raise(nameof(IsIdle));
        }
    }

    /// <summary>是否空闲（「立即更新」的可用条件）。</summary>
    /// <remarks>
    /// 🔴 单独给出这个属性是因为 <c>x:Bind</c> 不支持 <c>!</c> 取反 ——
    /// 把"忙"直接绑到 <c>IsEnabled</c> 上会得到"忙的时候能点、闲的时候点不了"。
    /// </remarks>
    public bool IsIdle => !_isBusy;

    /// <summary>进行中显示的那一句。</summary>
    public string ActivityText
    {
        get => _activityText;
        private set => Set(ref _activityText, value, nameof(ActivityText));
    }

    /// <summary>空闲时显示的那一句（上次检查的时间与结果）。</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value, nameof(SummaryText));
    }

    /// <summary>
    /// 标记"有一轮更新开始了"。
    /// </summary>
    /// <param name="activityText">这一轮要显示的进行中文案；为 <see langword="null"/> 时沿用默认。</param>
    /// <returns>释放即标记结束（<c>using</c> 用）。</returns>
    public IDisposable Begin(string? activityText = null)
    {
        Post(() =>
        {
            _busyCount++;
            if (!string.IsNullOrWhiteSpace(activityText))
            {
                ActivityText = activityText;
            }

            IsBusy = true;
        });

        return new Scope(this);
    }

    /// <summary>报告一句结果 / 状态文案（下次进入设置页、或当前页面实时显示）。</summary>
    /// <param name="summaryText">文案；空表示暂无可说的。</param>
    public void Report(string summaryText) => Post(() => SummaryText = summaryText ?? string.Empty);

    private void End()
    {
        Post(() =>
        {
            if (--_busyCount <= 0)
            {
                _busyCount = 0;
                IsBusy = false;
            }
        });
    }

    /// <summary>在 UI 线程上执行（已经在 UI 线程时直接执行，保证调用后状态即刻可读）。</summary>
    private void Post(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        // TryEnqueue 在窗口已销毁之后会返回 false —— 那时没人会再看这个状态，丢掉即可。
        _dispatcher.TryEnqueue(() => action());
    }

    private void Set(ref string field, string value, string propertyName)
    {
        value ??= string.Empty;
        if (string.Equals(field, value, StringComparison.Ordinal))
        {
            return;
        }

        field = value;
        Raise(propertyName);
    }

    private void Raise(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>一次「进行中」的租约，释放时把计数减一。</summary>
    /// <param name="owner">所属状态对象。</param>
    private sealed class Scope(HolidayUpdateStatus owner) : IDisposable
    {
        private bool _disposed;

        /// <summary>结束本轮（重复释放无副作用）。</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.End();
        }
    }
}
