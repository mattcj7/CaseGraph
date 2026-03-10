namespace CaseGraph.App.Services;

public interface IBackgroundMaintenanceManager
{
    event EventHandler<MaintenanceTaskSnapshot>? SnapshotChanged;

    MaintenanceTaskSnapshot? GetSnapshot(string taskKey);

    MaintenanceRequestResult QueueOrJoin(
        MaintenanceTaskRequest request,
        Func<IProgress<MaintenanceProgressUpdate>, CancellationToken, Task> work
    );

    bool TryCancel(string taskKey);
}
