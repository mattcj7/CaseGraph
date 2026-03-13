using CaseGraph.Infrastructure.Locations;
using CaseGraph.Infrastructure.Timeline;

namespace CaseGraph.Infrastructure.Incidents;

public interface IIncidentService
{
    Task<IReadOnlyList<IncidentRecord>> GetIncidentsAsync(Guid caseId, CancellationToken ct);

    Task<IncidentRecord?> GetIncidentAsync(Guid caseId, Guid incidentId, CancellationToken ct);

    Task<IncidentRecord> SaveIncidentAsync(IncidentRecord incident, string correlationId, CancellationToken ct);

    Task<IncidentCrossReferenceResult> RunCrossReferenceAsync(Guid caseId, Guid incidentId, string correlationId, CancellationToken ct);

    Task<IncidentRecord> PinMessageAsync(Guid caseId, Guid incidentId, TimelineRowDto message, string correlationId, CancellationToken ct);

    Task<IncidentRecord> PinLocationAsync(Guid caseId, Guid incidentId, IncidentLocationHit locationHit, string correlationId, CancellationToken ct);
}
