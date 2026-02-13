using CaseGraph.Core.Abstractions;
using CaseGraph.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CaseGraph.Infrastructure.Services;

public sealed class WorkspaceDatabaseInitializer : IWorkspaceDatabaseInitializer
{
    private readonly IDbContextFactory<WorkspaceDbContext> _dbContextFactory;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private bool _initialized;

    public WorkspaceDatabaseInitializer(IDbContextFactory<WorkspaceDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
    }

    public async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        await _semaphore.WaitAsync(ct);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var db = await _dbContextFactory.CreateDbContextAsync(ct);
            await db.Database.EnsureCreatedAsync(ct);
            _initialized = true;
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
