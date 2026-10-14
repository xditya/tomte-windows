namespace Tomte.Core.Text;

/// <summary>
/// A costly thing, such as a graphics device, that lives while anything uses it. The first user starts it being
/// made (off the caller's thread, by <c>make</c>); <see cref="Ready"/> says when it is there. When the last user
/// goes it is released one turn later (through <c>later</c>), so a view that drops all its users and adds new ones
/// in the same turn, a rebuild, keeps it. Users added and removed while it is being made leave it released if none
/// is left when it lands. One thread only.
/// </summary>
public sealed class SharedDevice<T>(Func<Task<T>> make, Action<Action> later)
    where T : class, IDisposable
{
    private Task<T>? making;
    private int users;
    private bool releasePending;

    /// <summary>The device, or null until it is made and again once it has been released.</summary>
    public T? Value { get; private set; }

    public int Users => users;

    /// <summary>Raised on the caller's thread when the device has been made and someone still uses it.</summary>
    public event Action? Ready;

    /// <summary>A make that failed, for the log; the users then stay without a device.</summary>
    public event Action<Exception>? Failed;

    public void Add()
    {
        users++;
        if (Value is null)
        {
            Make();
        }
    }

    public void Remove()
    {
        if (users == 0)
        {
            throw new InvalidOperationException("A user was removed that was never added.");
        }

        users--;
        if (users == 0 && !releasePending)
        {
            releasePending = true;
            later(() =>
            {
                releasePending = false;
                if (users == 0 && Value is { } device)
                {
                    Value = null;
                    device.Dispose();
                }
            });
        }
    }

    private async void Make()
    {
        if (making is not null)
        {
            return;
        }

        T made;
        try
        {
            making = make();
            made = await making;
        }
        catch (Exception e)
        {
            Failed?.Invoke(e);
            return;
        }
        finally
        {
            making = null;
        }

        if (users == 0)
        {
            made.Dispose();
            return;
        }

        Value = made;
        Ready?.Invoke();
    }
}
