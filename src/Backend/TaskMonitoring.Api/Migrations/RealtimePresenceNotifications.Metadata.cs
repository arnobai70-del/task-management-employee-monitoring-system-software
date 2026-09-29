using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TaskMonitoring.Api.Data;

#nullable disable

namespace TaskMonitoring.Api.Migrations;

// Keep the previously generated snapshot as the full baseline and layer only this
// migration's generated model delta on top. Making the baseline abstract ensures
// EF selects the constructible snapshot below for design-time drift comparison.
abstract partial class AppDbContextModelSnapshot
{
}

[DbContext(typeof(AppDbContext))]
sealed class RealtimePresenceNotificationsModelSnapshot : AppDbContextModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        base.BuildModel(modelBuilder);
        RealtimePresenceNotificationsModelMetadata.Configure(modelBuilder);
    }

    internal void BuildInto(ModelBuilder modelBuilder) => BuildModel(modelBuilder);
}

[DbContext(typeof(AppDbContext))]
[Migration("20260929222350_RealtimePresenceNotifications")]
partial class RealtimePresenceNotifications
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
        => new RealtimePresenceNotificationsModelSnapshot().BuildInto(modelBuilder);
}

internal static class RealtimePresenceNotificationsModelMetadata
{
    internal static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("TaskMonitoring.Api.Domain.EmployeeNotification", b =>
        {
            b.Property<Guid>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("uuid");

            b.Property<DateTime>("CreatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<Guid>("EmployeeId")
                .HasColumnType("uuid");

            b.Property<Guid?>("EntityId")
                .HasColumnType("uuid");

            b.Property<string>("EntityType")
                .IsRequired()
                .HasMaxLength(50)
                .HasColumnType("character varying(50)");

            b.Property<string>("Kind")
                .IsRequired()
                .HasMaxLength(40)
                .HasColumnType("character varying(40)");

            b.Property<string>("Message")
                .IsRequired()
                .HasMaxLength(500)
                .HasColumnType("character varying(500)");

            b.Property<DateTime?>("ReadAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<string>("Title")
                .IsRequired()
                .HasMaxLength(160)
                .HasColumnType("character varying(160)");

            b.HasKey("Id");
            b.HasIndex("EntityId");
            b.HasIndex("EmployeeId", "CreatedAtUtc");
            b.HasIndex("EmployeeId", "ReadAtUtc");
            b.ToTable("employee_notifications", (string)null);
        });

        modelBuilder.Entity("TaskMonitoring.Api.Domain.EmployeePresence", b =>
        {
            b.Property<Guid>("EmployeeId")
                .HasColumnType("uuid");

            b.Property<string>("ClientKind")
                .IsRequired()
                .HasMaxLength(30)
                .HasColumnType("character varying(30)");

            b.Property<string>("ClientVersion")
                .HasMaxLength(50)
                .HasColumnType("character varying(50)");

            b.Property<DateTime>("CreatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<DateTime>("LastSeenAtUtc")
                .HasColumnType("timestamp with time zone");

            b.Property<DateTime>("UpdatedAtUtc")
                .HasColumnType("timestamp with time zone");

            b.HasKey("EmployeeId");
            b.HasIndex("LastSeenAtUtc");
            b.ToTable("employee_presences", (string)null);
        });

        modelBuilder.Entity("TaskMonitoring.Api.Domain.EmployeeNotification", b =>
        {
            b.HasOne("TaskMonitoring.Api.Domain.Employee", "Employee")
                .WithMany()
                .HasForeignKey("EmployeeId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            b.Navigation("Employee");
        });

        modelBuilder.Entity("TaskMonitoring.Api.Domain.EmployeePresence", b =>
        {
            b.HasOne("TaskMonitoring.Api.Domain.Employee", "Employee")
                .WithOne()
                .HasForeignKey("TaskMonitoring.Api.Domain.EmployeePresence", "EmployeeId")
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();

            b.Navigation("Employee");
        });
    }
}
