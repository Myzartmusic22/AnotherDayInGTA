using System;
using System.Threading;

namespace AnotherDay.Plugin.Game
{
    /// <summary>
    /// Identifies the script thread. GTA natives may only be called from it;
    /// anything else corrupts the game's script VM and closes the process.
    /// Every native-owning helper checks this before touching an entity, so a
    /// stray background call is a logged no-op instead of a crash.
    /// </summary>
    internal static class GameThread
    {
        private static int _threadId;

        public static void MarkCurrent()
        {
            Interlocked.CompareExchange(ref _threadId, Thread.CurrentThread.ManagedThreadId, 0);
        }

        public static bool IsCurrent
        {
            get
            {
                int threadId = Volatile.Read(ref _threadId);
                return threadId != 0 && Thread.CurrentThread.ManagedThreadId == threadId;
            }
        }

        public static int Id { get { return Volatile.Read(ref _threadId); } }
    }
}
