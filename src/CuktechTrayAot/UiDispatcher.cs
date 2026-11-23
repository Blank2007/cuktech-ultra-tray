using System.Collections.Concurrent;

namespace CuktechTray;

/// <summary>
/// 把后台线程的续体排回 UI 线程：DeviceClient 只认 SynchronizationContext，
/// 而在裸 Win32 里我们没有 WinForms 的 WindowsFormsSynchronizationContext，
/// 所以自己写一个 —— Post 只是入队 + PostMessageW，真正的执行发生在消息循环里。
/// </summary>
internal sealed class UiDispatcher : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private IntPtr _hwnd;
    private uint _wakeMessage;

    public void Attach(IntPtr hwnd, uint wakeMessage)
    {
        _hwnd = hwnd;
        _wakeMessage = wakeMessage;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        _queue.Enqueue((d, state));
        if (_hwnd != IntPtr.Zero) Native.PostMessageW(_hwnd, _wakeMessage, IntPtr.Zero, IntPtr.Zero);
        else Drain();
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (_hwnd == IntPtr.Zero) { d(state); return; }
        Post(d, state);
    }

    public int Drain(int budget = 200)
    {
        int done = 0;
        while (done < budget && _queue.TryDequeue(out var item))
        {
            done++;
            try { item.Callback(item.State); }
            catch (Exception ex) { LogSink.Write("[ui] 回调异常: " + ex.GetType().Name + " " + ex.Message); }
        }
        return done;
    }
}
