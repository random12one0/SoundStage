namespace Soundstage.App.Tests.Support;

/// <summary>Runs a delegate on a dedicated STA thread (WPF elements demand one).</summary>
public static class StaTestHelper
{
    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60));

        if (failure is not null)
        {
            throw new InvalidOperationException($"STA action failed: {failure.Message}", failure);
        }
    }
}
