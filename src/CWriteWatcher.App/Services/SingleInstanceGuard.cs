using System.Security.Principal;

namespace CWriteWatcher.App.Services;

/// <summary>
/// 单实例守卫（v0.2 §4.2）。
/// </summary>
/// <remarks>
/// <para>用 <c>Local\</c>（会话作用域）而非 <c>Global\</c>：
/// 普通用户创建 Global 对象可能被 ACL 拒绝；而且多用户同时登录时，
/// 每个会话各跑一个托盘程序才是正确行为（托盘本身就是 per-session 的）。</para>
/// <para><b>已知限制（v0.2 §4.3）：</b>精确模式（管理员）与普通进程在同一 session，
/// <c>Local\</c> Mutex 能检测到冲突，但 UIPI 会阻止普通进程给管理员进程发信号，
/// 因此"二次启动唤起设置窗口"在跨权限时必然失败，需降级为提示框。</para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private EventWaitHandle? _activateEvent;
    private Thread? _listener;
    private volatile bool _disposed;

    public bool IsFirstInstance { get; }

    /// <summary>另一个实例请求唤起设置窗口。</summary>
    public event Action? ActivateRequested;

    public SingleInstanceGuard()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "default";
        var mutexName = $"Local\\CWriteWatcher.SingleInstance.{sid}";
        var eventName = $"Local\\CWriteWatcher.Activate.{sid}";

        _mutex = new Mutex(initiallyOwned: true, mutexName, out var createdNew);
        IsFirstInstance = createdNew;

        if (!IsFirstInstance) return;

        try
        {
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            _listener = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "CWriteWatcher-SingleInstance",
            };
            _listener.Start();
        }
        catch
        {
            // 事件创建失败不影响单实例判定本身
        }
    }

    private void ListenLoop()
    {
        while (!_disposed)
        {
            try
            {
                if (_activateEvent is null) return;
                if (!_activateEvent.WaitOne(TimeSpan.FromSeconds(1))) continue;
                if (_disposed) return;
                ActivateRequested?.Invoke();
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch
            {
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>通知已运行的实例。跨权限时会失败（UIPI），返回 false。</summary>
    public bool SignalExistingInstance()
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? "default";
            using var handle = EventWaitHandle.OpenExisting($"Local\\CWriteWatcher.Activate.{sid}");
            handle.Set();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _activateEvent?.Set(); } catch { /* ignore */ }
        try { _listener?.Join(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
        _activateEvent?.Dispose();
        try { _mutex.ReleaseMutex(); } catch { /* ignore */ }
        _mutex.Dispose();
    }
}
