using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TaskMonitoring.Api.Data;

#nullable disable

namespace TaskMonitoring.Api.Migrations;

// Data-only migration: the model is unchanged from AttendanceClosureAndCorrection.
[DbContext(typeof(AppDbContext))]
[Migration("20261014170000_EmployeeScopeAll")]
partial class EmployeeScopeAll
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        new RealtimePresenceNotificationsModelSnapshot().BuildRealtimeInto(modelBuilder);
        TransparentMonitoringTelemetryModelMetadata.Configure(modelBuilder);
        AttendanceClosureAndCorrectionModelMetadata.Configure(modelBuilder);
    }
}
