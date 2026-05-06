namespace Auxilia.UniversalDataAccess;

public static class ReaderWriterLockSlimExtensions
{
    private abstract class LockToken : IDisposable
    {
        protected ReaderWriterLockSlim? LockSlim { get; private set; }

        protected LockToken(ReaderWriterLockSlim lockSlim)
        {
            LockSlim = lockSlim;
        }

        public void Dispose()
        {
            if (LockSlim == null) return;
            Exit();
            LockSlim = null;
        }

        protected abstract void Exit();
    }

    private sealed class ReadToken : LockToken
    {
        public ReadToken(ReaderWriterLockSlim rwLock) : base(rwLock)
        {
            rwLock.EnterReadLock();
        }

        protected override void Exit()
        {
            LockSlim?.ExitReadLock();
        }
    }

    private sealed class WriteToken : LockToken
    {
        public WriteToken(ReaderWriterLockSlim rwLock) : base(rwLock)
        {
            rwLock.EnterWriteLock();
        }

        protected override void Exit()
        {
            LockSlim?.ExitWriteLock();
        }
    }

    public static IDisposable Read(this ReaderWriterLockSlim rwLock)
    {
        return new ReadToken(rwLock);
    }

    public static IDisposable Write(this ReaderWriterLockSlim rwLock)
    {
        return new WriteToken(rwLock);
    }
}