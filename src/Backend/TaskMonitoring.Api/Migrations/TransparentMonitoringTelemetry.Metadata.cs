using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TaskMonitoring.Api.Data;

#nullable disable

namespace TaskMonitoring.Api.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260929225909_TransparentMonitoringTelemetry")]
partial class TransparentMonitoringTelemetry
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        new RealtimePresenceNotificationsModelSnapshot().BuildRealtimeInto(modelBuilder);
        TransparentMonitoringTelemetryModelMetadata.Configure(modelBuilder);
    }
}

internal static class TransparentMonitoringTelemetryModelMetadata
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("TaskMonitoring.Api.Domain.ApprovedApplication", b =>
        {
            b.Property<Guid>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("uuid");

            b.Property<bool>("CaptureWindowTitle")
                .HasColumnType("boolean");

            b.Property<DateTime>("CreatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<string>("DisplayName")
                .IsRequired()
                .HasMaxLength(160)
                .HasColumnType("character varying(160)");

            b.Property<bool>("IsActive")
                .HasColumnType("boolean");

            b.Property<string>("NormalizedProcessName")
                .IsRequired()
                .HasMaxLength(120)
                .HasColumnType("character varying(120)");

            b.Property<string>("ProcessName")
                .IsRequired()
                .HasMaxLength(120)
                .HasColumnType("character varying(120)");

            b.Property<DateTime>("UpdatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.HasKey("Id");

            b.HasIndex("NormalizedProcessName")
                .IsUnique();

            b.HasIndex("IsActive", "DisplayName");

            b.ToTable("approved_monitoring_applications", (string)null);
        });

        modelBuilder.Entity("TaskMonitoring.Api.Domain.ApprovedBusinessDomain", b =>
        {
            b.Property<Guid>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("uuid");

            b.Property<DateTime>("CreatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<string>("DisplayName")
                .IsRequired()
                .HasMaxLength(160)
                .HasColumnType("character varying(160)");

            b.Property<string>("Domain")
                .IsRequired()
                .HasMaxLength(253)
                .HasColumnType("character varying(253)");

            b.Property<bool>("IncludeSubdomains")
                .HasColumnType("boolean");

            b.Property<bool>("IsActive")
                .HasColumnType("boolean");

            b.Property<string>("NormalizedDomain")
                .IsRequired()
                .HasMaxLength(253)
                .HasColumnType("character varying(253)");

            b.Property<DateTime>("UpdatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.HasKey("Id");

            b.HasIndex("NormalizedDomain")
                .IsUnique();

            b.HasIndex("IsActive", "Domain");

            b.ToTable("approved_monitoring_domains", (string)null);
        });

        modelBuilder.Entity("TaskMonitoring.Api.Domain.MonitoringActivitySegment", b =>
        {
            b.Property<Guid>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("uuid");

            b.Property<string>("ApplicationName")
                .HasMaxLength(160)
                .HasColumnType("character varying(160)");

            b.Property<DateTime>("CreatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<string>("Domain")
                .HasMaxLength(253)
                .HasColumnType("character varying(253)");

            b.Property<Guid>("EmployeeId")
                .HasColumnType("uuid");

            b.Property<string>("Kind")
                .IsRequired()
                .HasMaxLength(30)
                .HasColumnType("character varying(30)");

            b.Property<DateTime>("LastObservedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<string>("ProcessName")
                .HasMaxLength(120)
                .HasColumnType("character varying(120)");

            b.Property<int>("SampleCount")
                .HasColumnType("integer");

            b.Property<int>("SampleIntervalSeconds")
                .HasColumnType("integer");

            b.Property<DateTime>("StartedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<DateTime>("UpdatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<string>("WindowTitle")
                .HasMaxLength(300)
                .HasColumnType("character varying(300)");

            b.HasKey("Id");

            b.HasIndex("Domain");

            b.HasIndex("ProcessName");

            b.HasIndex("EmployeeId", "LastObservedAtUtc");

            b.HasIndex("Kind", "LastObservedAtUtc");

            b.ToTable("monitoring_activity_segments", (string)null);
        });

        modelBuilder.Entity("TaskMonitoring.Api.Domain.MonitoringPolicy", b =>
        {
            b.Property<Guid>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("uuid");

            b.Property<DateTime>("CreatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<string>("DisclosureText")
                .IsRequired()
                .HasMaxLength(2000)
                .HasColumnType("character varying(2000)");

            b.Property<bool>("IsEnabled")
                .HasColumnType("boolean");

            b.Property<int>("RetentionDays")
                .HasColumnType("integer");

            b.Property<int>("SampleIntervalSeconds")
                .HasColumnType("integer");

            b.Property<DateTime>("UpdatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.HasKey("Id");

            b.HasIndex("CreatedAtUtc");

            b.ToTable("monitoring_policies", (string)null);
        });

        modelBuilder.Entity("TaskMonitoring.Api.Domain.MonitoringActivitySegment", b =>
        {
            b.HasOne("TaskMonitoring.Api.Domain.Employee", "Employee")
                .WithMany()
                .HasForeignKey("EmployeeId")
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();

            b.Navigation("Employee");
        });
    }
}
