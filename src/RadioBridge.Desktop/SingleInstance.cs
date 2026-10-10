using System.IO.Pipes;

namespace RadioBridge.Desktop;

/// <summary>
/// Launching RadioBridge while it already runs brings the running window to the front, even when it
/// is hidden in the tray, instead of starting a second copy that would fight over the panel.
/// </summary>
internal static class SingleInstance
{
    private const byte ShowWindow = 1;
    private static readonly string PipeName = $"RadioBridge.Activate.{Environment.UserName}";

    /// <summary>Asks the running instance to show its window. False if no instance answered.</summary>
    public static bool AskRunningToShow()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(TimeSpan.FromSeconds(2));
            pipe.WriteByte(ShowWindow);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Calls <paramref name="show"/> (on a background thread) whenever another launch asks.</summary>
    public static void Listen(Action show, CancellationToken ct) => _ = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                if (server.ReadByte() == ShowWindow)
                    show();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ContinueWith(_ => { });
            }
        }
    }, ct);
}
