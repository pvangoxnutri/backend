using Microsoft.EntityFrameworkCore;
using sidequest.backend.Models;

namespace sidequest.backend.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<User> Users => Set<User>();
    public DbSet<TripMember> TripMembers => Set<TripMember>();
    public DbSet<TripActivity> TripActivities => Set<TripActivity>();
    public DbSet<TripInvite> TripInvites => Set<TripInvite>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();

        modelBuilder.Entity<TripMember>()
            .HasIndex(tm => new { tm.TripId, tm.UserId })
            .IsUnique();

        modelBuilder.Entity<TripInvite>()
            .HasIndex(ti => new { ti.TripId, ti.Email })
            .IsUnique();

        modelBuilder.Entity<Trip>()
            .HasOne(t => t.Owner)
            .WithMany()
            .HasForeignKey(t => t.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<TripMember>()
            .HasOne(tm => tm.Trip)
            .WithMany()
            .HasForeignKey(tm => tm.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TripMember>()
            .HasOne(tm => tm.User)
            .WithMany()
            .HasForeignKey(tm => tm.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TripActivity>()
            .HasOne(a => a.Trip)
            .WithMany()
            .HasForeignKey(a => a.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TripActivity>()
            .HasOne(a => a.AssignedTo)
            .WithMany()
            .HasForeignKey(a => a.AssignedToUserId)
            .OnDelete(DeleteBehavior.SetNull)
            .IsRequired(false);

        modelBuilder.Entity<TripInvite>()
            .HasOne(ti => ti.Trip)
            .WithMany()
            .HasForeignKey(ti => ti.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<TripInvite>()
            .HasOne(ti => ti.InvitedByUser)
            .WithMany()
            .HasForeignKey(ti => ti.InvitedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
