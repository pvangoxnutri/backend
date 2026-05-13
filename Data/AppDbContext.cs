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
    public DbSet<ActivityComment> ActivityComments => Set<ActivityComment>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<ExpensePayer> ExpensePayers => Set<ExpensePayer>();
    public DbSet<ExpenseParticipant> ExpenseParticipants => Set<ExpenseParticipant>();
    public DbSet<Settlement> Settlements => Set<Settlement>();
    public DbSet<TripEvent> TripEvents => Set<TripEvent>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatPresenceEntry> ChatPresence => Set<ChatPresenceEntry>();
    public DbSet<Feedback> Feedbacks => Set<Feedback>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TripEvent>()
            .HasOne(e => e.Trip)
            .WithMany()
            .HasForeignKey(e => e.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ChatMessage>()
            .HasOne(m => m.Trip)
            .WithMany()
            .HasForeignKey(m => m.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ChatPresenceEntry>()
            .HasKey(cp => new { cp.TripId, cp.UserId });

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
            .HasOne(a => a.Owner)
            .WithMany()
            .HasForeignKey(a => a.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);

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

        modelBuilder.Entity<ActivityComment>()
            .HasOne(c => c.Activity)
            .WithMany()
            .HasForeignKey(c => c.ActivityId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ActivityComment>()
            .HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Expense>()
            .HasOne(e => e.Trip)
            .WithMany()
            .HasForeignKey(e => e.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Expense>()
            .HasOne(e => e.CreatedBy)
            .WithMany()
            .HasForeignKey(e => e.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ExpensePayer>()
            .HasOne(ep => ep.Expense)
            .WithMany(e => e.Payers)
            .HasForeignKey(ep => ep.ExpenseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ExpensePayer>()
            .HasOne(ep => ep.User)
            .WithMany()
            .HasForeignKey(ep => ep.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ExpenseParticipant>()
            .HasOne(ep => ep.Expense)
            .WithMany(e => e.Participants)
            .HasForeignKey(ep => ep.ExpenseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ExpenseParticipant>()
            .HasOne(ep => ep.User)
            .WithMany()
            .HasForeignKey(ep => ep.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Settlement>()
            .HasOne(s => s.Trip)
            .WithMany()
            .HasForeignKey(s => s.TripId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Settlement>()
            .HasOne(s => s.FromUser)
            .WithMany()
            .HasForeignKey(s => s.FromUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Settlement>()
            .HasOne(s => s.ToUser)
            .WithMany()
            .HasForeignKey(s => s.ToUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
