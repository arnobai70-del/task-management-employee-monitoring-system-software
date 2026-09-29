namespace TaskMonitoring.Api.Contracts;

public sealed record EmployeeAccessWorkspaceResponse(
    IReadOnlyCollection<RdpAssignmentResponse> RdpAssignments,
    IReadOnlyCollection<IpAssignmentResponse> IpAssignments,
    IReadOnlyCollection<WebsiteAssignmentResponse> WebsiteAssignments);
