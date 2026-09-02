using System.Security.AccessControl;
using System.Security.Principal;

namespace ClashUI.App.Hosting;

public sealed class MutexSingleInstanceGuard : ISingleInstanceGuard
{
    private const string MutexName = @"Local\ClashUI.SingleInstance";
    private const string LegacyMutexName = @"Local\Clashui.SingleInstance";
    private Mutex? _mutex;
    private bool _acquired;

    public bool Acquire()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            bool createdNew;
            try
            {
                _mutex = new Mutex(false, MutexName, out createdNew);
                if (!createdNew)
                {
                    try { _mutex.Dispose(); } catch { }
                    _mutex = null;
                    Thread.Sleep(250);
                    continue;
                }
                var sec = CreateWorldMutexSecurity();
                if (sec != null) try { _mutex.SetAccessControl(sec); } catch { }
                try { _mutex.WaitOne(0); } catch { }
            }
            catch
            {
                Thread.Sleep(250);
                continue;
            }
            var legacyExists = false;
            try
            {
                using var legacy = Mutex.OpenExisting(LegacyMutexName);
                legacyExists = true;
            }
            catch (WaitHandleCannotBeOpenedException) { }
            catch (UnauthorizedAccessException) { legacyExists = true; }
            catch { }
            if (!legacyExists)
            {
                _acquired = true;
                return true;
            }
            try { _mutex.ReleaseMutex(); } catch { }
            try { _mutex.Dispose(); } catch { }
            _mutex = null;
            return false;
        }
        return false;
    }

    private static MutexSecurity CreateWorldMutexSecurity()
    {
        try
        {
            var sec = new MutexSecurity();
            var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            sec.AddAccessRule(new MutexAccessRule(world, MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
            sec.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), MutexRights.FullControl, AccessControlType.Allow));
            return sec;
        }
        catch { return null!; }
    }

    public void Dispose()
    {
        if (_acquired) try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        _mutex = null;
    }
}

public sealed class FakeSingleInstanceGuard : ISingleInstanceGuard
{
    private readonly bool _result;
    public FakeSingleInstanceGuard(bool result = true) => _result = result;
    public bool Acquire() => _result;
    public void Dispose() { }
}
