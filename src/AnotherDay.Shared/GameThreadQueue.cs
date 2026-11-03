using System;
using System.Collections.Concurrent;

namespace AnotherDay.Shared
{
    /// <summary>
    /// The only bridge from the microphone, pipe reader and network threads to
    /// the game. GTA's script VM is single-threaded: calling a native from a
    /// background thread corrupts it and closes the game with no managed
    /// exception. Everything that touches an entity or a native is therefore
    /// posted here and executed inside the script tick.
    /// </summary>
    public sealed class GameThreadQueue
    {
        private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        /// <summary>Work waiting for the next tick. Diagnostics only.</summary>
        public int Count { get { return _queue.Count; } }

        /// <summary>Total actions executed, including ones that threw.</summary>
        public int Handled { get; private set; }

        /// <summary>Last exception a posted action threw. Never rethrown: one bad
        /// action must not stop the game thread from draining the rest.</summary>
        public Exception LastError { get; private set; }

        public void Post(Action action)
        {
            if (action != null) _queue.Enqueue(action);
        }

        /// <summary>
        /// Runs queued work on the calling (game) thread. Bounded so a flood of
        /// events can never stall a frame; the remainder runs next tick.
        /// </summary>
        public int Drain(int max = 64)
        {
            int count = 0;
            while (count < Math.Max(1, max))
            {
                Action action;
                if (!_queue.TryDequeue(out action)) break;
                count++;
                try
                {
                    action();
                    Handled++;
                }
                catch (Exception ex)
                {
                    LastError = ex;
                }
            }
            return count;
        }
    }
}
