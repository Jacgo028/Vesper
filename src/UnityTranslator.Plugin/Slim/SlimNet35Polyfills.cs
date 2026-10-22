#if SLIM_NO_TMP
using System.Collections.Generic;

// ─────────────────────────────────────────────────────────────────────────────
// 老运行时（Unity 5.6.7 / CLR 2.0）的垫片。
//
// 背景（进展记录 B79）：NightofRevenge 跑在 `CLR runtime version: 2.0.50727.1433` 上，
// 它的 Managed 里**既没有 netstandard.dll，也没有 System.Collections.Concurrent**
// （那是 .NET 4.0 才引入的），连命名管道都被裁掉了（System.Core.dll 只有 260 KB）。
//
// 这里的做法是**用自己的实现补上缺失的类型** —— 只有在精简变体里才编译。
// 之所以只补 `ConcurrentQueue<T>`：它是**类**，可以完整实现；
// 而 `IReadOnlyList<T>` 是**接口**，数组和 List 不会自动实现我们的版本，垫片无效，
// 那些签名必须改成 `IList<T>`（见交接文档的实施清单）。
//
// 语义要求：调用方只用到 Enqueue / TryDequeue / TryPeek / Count / IsEmpty，
// 且是"单生产者单消费者"或"多生产者单消费者"的用法，加锁实现足够且更保守。
// ─────────────────────────────────────────────────────────────────────────────
namespace System.Collections.Concurrent
{
    /// <summary>net35 垫片：.NET 4.0 的 <c>ConcurrentQueue&lt;T&gt;</c> 的最小可用实现。</summary>
    public class ConcurrentQueue<T>
    {
        private readonly Queue<T> _items = new Queue<T>();
        private readonly object _gate = new object();

        public void Enqueue(T item)
        {
            lock (_gate) { _items.Enqueue(item); }
        }

        public bool TryDequeue(out T result)
        {
            lock (_gate)
            {
                if (_items.Count == 0) { result = default(T); return false; }
                result = _items.Dequeue();
                return true;
            }
        }

        public bool TryPeek(out T result)
        {
            lock (_gate)
            {
                if (_items.Count == 0) { result = default(T); return false; }
                result = _items.Peek();
                return true;
            }
        }

        public int Count
        {
            get { lock (_gate) { return _items.Count; } }
        }

        public bool IsEmpty
        {
            get { lock (_gate) { return _items.Count == 0; } }
        }

        /// <summary>按快照清空（调用方只有断线重连时才会用到）。</summary>
        public void Clear()
        {
            lock (_gate) { _items.Clear(); }
        }
    }
}
#endif
