using System.ComponentModel;

namespace DualLink.Core;

public static class RouteManagementAttempt
{
    // A transient kernel route failure is a status result, not a reason to
    // abandon adapter sampling and rendering for the entire monitor round.
    public static async Task<string?> RunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            await operation();
            return null;
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            return error.Message;
        }
    }
}
