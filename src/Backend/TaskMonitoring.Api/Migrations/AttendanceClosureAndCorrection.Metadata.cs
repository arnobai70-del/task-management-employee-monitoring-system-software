using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TaskMonitoring.Api.Data;

#nullable disable

namespace TaskMonitoring.Api.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261013233309_AttendanceClosureAndCorrection")]
partial class AttendanceClosureAndCorrection
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        new RealtimePresenceNotificationsModelSnapshot().BuildRealtimeInto(modelBuilder);
        TransparentMonitoringTelemetryModelMetadata.Configure(modelBuilder);
        AttendanceClosureAndCorrectionModelMetadata.Configure(modelBuilder);
    }
}

internal static class AttendanceClosureAndCorrectionModelMetadata
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("TaskMonitoring.Api.Domain.WorkSession", b =>
        {
            b.Property<string>("ClosureSource")
                .HasMaxLength(30)
                .HasColumnType("character varying(30)");

            b.Property<DateTime?>("CorrectedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<Guid?>("CorrectedByUserId")
                .HasColumnType("uuid");

            b.Property<string>("CorrectionReason")
                .HasMaxLength(500)
                .HasColumnType("character varying(500)");

            b.Property<DateTime?>("EndedAtUtc")
                .IsConcurrencyToken()
                .HasColumnType("timestamp with time zone");

            b.HasIndex("ScheduledEndUtc")
                .HasFilter("\"EndedAtUtc\" IS NULL");
        });

        // Annotation only (no schema change): optimistic concurrency token added for refresh rotation (G-21).
        modelBuilder.Entity("TaskMonitoring.Api.Domain.RefreshToken", b =>
        {
            b.Property<DateTime?>("RevokedAtUtc")
                .IsConcurrencyToken()
                .HasColumnType("timestamp with time zone");
        });
    }
}
