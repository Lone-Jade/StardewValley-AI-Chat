namespace StardewAI.AI;

public sealed record AiScene(string Save, long Player, string Npc, int Day,
    string PlayerLocation, string NpcLocation, float PlayerX, float PlayerY, float NpcX, float NpcY)
{
    public bool IsCompatible(AiScene current) => Save == current.Save && Player == current.Player
        && Npc == current.Npc && Day == current.Day
        && PlayerLocation == current.PlayerLocation && NpcLocation == current.NpcLocation
        && DistanceSquared(PlayerX, PlayerY, current.PlayerX, current.PlayerY) <= 4
        && DistanceSquared(NpcX, NpcY, current.NpcX, current.NpcY) <= 4;

    private static float DistanceSquared(float x, float y, float otherX, float otherY)
        => (x - otherX) * (x - otherX) + (y - otherY) * (y - otherY);
}

/// <summary>Only the game thread owns this queue. The client receives an immutable game snapshot.</summary>
public sealed class AiDialogueQueue
{
    private sealed record Pending(AiScene Scene, Guid RequestId, CancellationTokenSource Cancellation, Task<AiDialogueResult> Task);
    private readonly IAiDialogueClient client;
    private Pending? pending;

    public AiDialogueQueue(IAiDialogueClient client) => this.client = client;
    public bool IsBusy => pending is not null;
    public Guid? CurrentRequestId => pending?.RequestId;

    public bool TryStart(DeepSeekOptions options, AiDialogueRequest request, AiScene scene)
    {
        if (pending is not null)
            return false;
        if (request.RequestId == Guid.Empty)
            request = request with { RequestId = Guid.NewGuid() };
        var cancellation = new CancellationTokenSource();
        pending = new Pending(scene, request.RequestId, cancellation, RunAsync(options, request, cancellation.Token));
        return true;
    }

    public bool TryTake(AiScene scene, bool safe, out AiDialogueResult? result)
    {
        result = null;
        if (pending is null)
            return false;
        if (!safe || !pending.Scene.IsCompatible(scene))
        {
            Cancel();
            return false;
        }
        if (!pending.Task.IsCompleted)
            return false;
        result = pending.Task.GetAwaiter().GetResult();
        pending.Cancellation.Dispose();
        pending = null;
        return true;
    }

    public void Cancel()
    {
        Pending? abandoned = pending;
        pending = null;
        if (abandoned is null)
            return;
        abandoned.Cancellation.Cancel();
        _ = abandoned.Task.ContinueWith(_ => abandoned.Cancellation.Dispose(),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<AiDialogueResult> RunAsync(DeepSeekOptions options, AiDialogueRequest request,
        CancellationToken token)
    {
        try
        {
            return await client.GenerateAsync(options, request, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AiDialogueResult.Failed("cancelled");
        }
        catch (Exception)
        {
            // Provider exceptions can contain credentials or response bodies. Keep logs to reason codes.
            return AiDialogueResult.Failed("client_error");
        }
    }
}
