using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CaseGraph.Infrastructure.Persistence;

public sealed class WorkspaceDbContextDesignTimeFactory : IDesignTimeDbContextFactory<WorkspaceDbContext>
{
    public WorkspaceDbContext CreateDbContext(string[] args)
    {
        var workspaceRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CaseGraphOffline"
        );
        Directory.CreateDirectory(workspaceRoot);

        var optionsBuilder = new DbContextOptionsBuilder<WorkspaceDbContext>();
        optionsBuilder.UseSqlite($"Data Source={Path.Combine(workspaceRoot, "workspace.db")}");
        return new WorkspaceDbContext(optionsBuilder.Options);
    }
}
