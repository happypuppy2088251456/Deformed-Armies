using System;
using System.Collections.Concurrent;
using System.Threading;
/*
使用说明：
第1步：让子弹类型继承 PooledObject。Unity 中不能同时继承 MonoBehaviour，可以用包装方式。
第2步：实现 OnGet。每次从池里取东西，池会自动调用你实现的这个函数。你实现的函数要包含激活子弹、重置本次状态、启用碰撞等。
第3步：实现 OnRelease。每次往池里放东西，池会自动调用你实现的这个函数。需包含隐藏子弹、停止计时、取消事件、清理脏数据。
第4步：创建子弹池。提供一个创建子弹的方法。设置最大空闲缓存数量。一个子弹类型通常只建一个池。
第5步：发射时调用池的 Get 方法。它会返回一颗可用子弹。不要自己创建子弹。不要手动调用 OnGet。
第6步：Get 之后自己设置本次发射参数。比如位置、方向、速度、伤害。因为 Get 不接收这些参数。
第7步：使用子弹。正常更新位置、生命和碰撞。使用期间不要归还。同一颗子弹不要被多个线程同时使用，虽然不会直接炸刚但是会报错
第8步：命中、超时或飞出屏幕时，调用池的 Release 方法归还。池会自动调用 OnRelease。正常归还是放回池，不是销毁。

注：
Release 后立刻丢掉引用。不要再读写这颗子弹。也不要再次 Release。重复归还会导致严重问题。
池满时，归还的子弹会被销毁。Clear 会销毁当前空闲子弹。Dispose 会关闭池，之后不能再使用。
OnGet 或 OnRelease 抛异常时，池也会销毁该子弹。OnGet 异常还会把异常抛给调用方。
跨池归还会被拒绝。子弹属于哪个池，就只能还给哪个池。
Unity 里所有 Unity API 尽量在主线程调用。对象池线程安全，不代表子弹里的 Unity 操作线程安全。
最大缓存数不是同时发射上限。它只限制池里最多保留多少颗空闲子弹。
*/
public interface IPoolable
{
    void OnGet();
    void OnRelease();
}

public abstract class PooledObject : IPoolable
{
    internal const int StateInPool = 0;
    internal const int StateCheckedOut = 1;
    internal const int StateDisposed = 2;

    private int m_State = StateInPool;
    private int m_OwnerPoolId;

    internal int OwnerPoolId
    {
        get { return Volatile.Read(ref m_OwnerPoolId); }
        set{ Volatile.Write(ref m_OwnerPoolId, value); }
    }
    internal int State{get{return Volatile.Read(ref m_State);}}
    internal bool TryCheckout() {return Interlocked.CompareExchange(ref m_State, StateCheckedOut, StateInPool)== StateInPool;}
    internal bool TryReturn() {return Interlocked.CompareExchange(ref m_State, StateInPool, StateCheckedOut) == StateCheckedOut;}
    internal bool TryDispose() {return Interlocked.Exchange(ref m_State, StateDisposed) != StateDisposed;}
    public abstract void OnGet();
    public abstract void OnRelease();
}

internal static class PoolIdGenerator
{
    private static int s_Seed;//用于记录池中对象的递增ID
    public static int Next() { return Interlocked.Increment(ref s_Seed); }
}

public sealed class ThreadSafeObjectPool<T> : IDisposable where T : PooledObject
{
    private readonly ConcurrentQueue<T> m_Queue = new ConcurrentQueue<T>();
    private readonly Func<T> m_CreateFunc;
    private readonly int m_MaxSize;
    private readonly Action<string> m_Logger;
    private readonly int m_PoolId = PoolIdGenerator.Next();

    private int m_Count;
    private int m_Disposed;

    public ThreadSafeObjectPool(Func<T> createFunc, int maxSize = 1000, Action<string> logger = null)
    {
        if (maxSize < 0) throw new ArgumentOutOfRangeException(nameof(maxSize));
        if (createFunc == null)
        {
            throw new ArgumentNullException(nameof(createFunc));
        }

        m_CreateFunc = createFunc;
        m_MaxSize = maxSize;
        m_Logger = logger;
    }

    public T Get()
    {
        if (Volatile.Read(ref m_Disposed) != 0)
            throw new ObjectDisposedException(nameof(ThreadSafeObjectPool<T>));

        T element = null;
        while (m_Queue.TryDequeue(out var candidate))
        {
            Interlocked.Decrement(ref m_Count);
            if (candidate.TryCheckout())
            {
                element = candidate;
                break;
            }
            SafeDispose(candidate);
        }

        if (element == null)
        {
            element = m_CreateFunc();
            if (element == null)
            {
                throw new InvalidOperationException("createFunc 返回了 null");
            }
            element.OwnerPoolId = m_PoolId;

            if (!element.TryCheckout())
            {
                SafeDispose(element);
                throw new InvalidOperationException("createFunc 返回了状态非法的对象");
            }
        }

        try
        {
            element.OnGet();
        }
        catch (Exception ex)
        {
            m_Logger?.Invoke($"[Pool] OnGet 异常，对象被销毁：{ex}");
            SafeDispose(element);
            throw;
        }
        return element;
    }

    public void Release(T element)
    {
        if (element == null) { m_Logger?.Invoke("[Pool] Release(null) 被忽略"); return; }

        if (element.OwnerPoolId != m_PoolId)
        {
            m_Logger?.Invoke($"[Pool] 警告：对象不属于本池(OwnerPoolId={element.OwnerPoolId})，拒绝归还。");
            return;
        }

        if (!element.TryReturn())
        {
            m_Logger?.Invoke($"[Pool] 警告：重复归还或非法状态");
            return;
        }

        if (Volatile.Read(ref m_Disposed) != 0)
        {
            SafeDispose(element);
            return;
        }

        try
        {
            element.OnRelease();
        }
        catch (Exception ex)
        {
            m_Logger?.Invoke($"[Pool] OnRelease 异常，对象被销毁：{ex}");
            SafeDispose(element);
            return;
        }

        if (Interlocked.Increment(ref m_Count) <= m_MaxSize)
        {
            m_Queue.Enqueue(element);
        }
        else
        {
            Interlocked.Decrement(ref m_Count);
            m_Logger?.Invoke($"[Pool] 池已满({m_MaxSize})，对象被销毁");
            SafeDispose(element);
        }
    }

    public void Clear()
    {
        while (m_Queue.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref m_Count);
            SafeDispose(item);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref m_Disposed, 1) != 0) return;
        Clear();
    }

    private static void SafeDispose(T item)
    {
        if (item == null) return;

        item.TryDispose();

        try
        {
            (item as IDisposable)?.Dispose();
        }
        catch
        {
        }
    }
}
