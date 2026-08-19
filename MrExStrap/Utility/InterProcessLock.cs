using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BeastStrap.Utility
{
    public class InterProcessLock : IDisposable
    {
        public Mutex Mutex { get; private set; }

        public bool IsAcquired { get; private set; }

        public InterProcessLock(string name) : this(name, TimeSpan.Zero) { }

        public InterProcessLock(string name, TimeSpan timeout)
        {
            Mutex = new Mutex(false, "BeastStrap-" + name);

            try
            {
                IsAcquired = Mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                IsAcquired = true;
            }
        }

        public void Dispose()
        {
            if (IsAcquired)
            {
                // A Windows mutex is owned by the THREAD that waited on it, and ReleaseMutex from
                // any other thread throws ApplicationException. That is easy to hit here: the
                // watcher takes its lock in a constructor on the UI thread and disposes from a
                // Task continuation on the thread pool. The throw escaped into that continuation
                // and skipped App.Terminate(), and because ShutdownMode is OnExplicitShutdown the
                // process then lived forever — one leaked BeastStrap per Roblox session.
                //
                // Swallowing it is safe. Releasing is a courtesy, not a correctness requirement:
                // Windows marks the mutex abandoned when the process exits, and the constructor
                // above already treats AbandonedMutexException as a successful acquire, so the
                // next holder is unaffected either way.
                try
                {
                    Mutex.ReleaseMutex();
                }
                catch (Exception ex)
                {
                    App.Logger.WriteException("InterProcessLock::Dispose", ex);
                }

                IsAcquired = false;
            }

            Mutex.Dispose();

            GC.SuppressFinalize(this);
        }
    }
}
