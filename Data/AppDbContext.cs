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
    public DbSet<ChatMessageReaction> ChatMessageReactions => Set<ChatMessageReaction>();
    public DbSet<ChatPresenceEntry> ChatPresence => Set<ChatPresenceEntry>();
    public DbSet<Feedback> Feedbacks => Set<Feedback>();
    public DbSet<PushToken> PushTokens => Set<PushToken>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<PushDeliveryAttempt> PushDeliveryAttempts => Set<PushDeliveryAttempt>();
    public DbSet<UserReport> UserReports => Set<UserReport>();
    public DbSet<UserBlock> UserBlocks => Set<UserBlock>();
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<SupportMessage> SupportMessages => Set<SupportMessage>();
    public DbSet<SupportAttachment> SupportAttachments => Set<SupportAttachment>();
    public DbSet<PackingListCategory> PackingListCategories => Set<PackingListCategory>();
    public DbSet<PackingListItem> PackingListItems => Set<PackingListItem>();
    public DbSet<TripDayLocation> TripDayLocations => Set<TripDayLocation>();

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

        // "Only one location per calendar day" enforced at the DB level,
        // not just in application code.
        modelBuilder.Entity<TripDayLocation>()
            .HasIndex(d => new { d.TripId, d.StartDate })
            .IsUnique();

        // Without this, EF's convention-based FK discovery doesn't match
        // CreatedByUserId to the CreatedBy navigation (it looks for
        // CreatedById) and silently adds a second, shadow FK column instead
        // of reusing the explicit one.
        modelBuilder.Entity<TripDayLocation>()
            .HasOne(d => d.CreatedBy)
            .WithMany()
            .HasForeignKey(d => d.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<ChatMessageReaction>()
            .HasOne(r => r.ChatMessage)
            .WithMany()
            .HasForeignKey(r => r.ChatMessageId)
            .OnDelete(DeleteBehavior.Cascade);

        // One reaction per user+emoji per message — toggling is delete/insert.
        modelBuilder.Entity<ChatMessageReaction>()
            .HasIndex(r => new { r.ChatMessageId, r.UserId, r.Emoji })
            .IsUnique();

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

        modelBuilder.Entity<PushToken>()
            .HasOne(pt => pt.User)
            .WithMany()
            .HasForeignKey(pt => pt.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // One row per (user, token) — re-registering the same device just
        // updates LastSeenAt/IsActive instead of creating a duplicate.
        modelBuilder.Entity<PushToken>()
            .HasIndex(pt => new { pt.UserId, pt.Token })
            .IsUnique();

        // The actual idempotency guard: a unique index on DedupeKey means
        // even a race between two scheduler ticks can't double-insert, since
        // the second insert fails at the database level, not just in code.
        modelBuilder.Entity<NotificationLog>()
            .HasIndex(n => n.DedupeKey)
            .IsUnique();

        modelBuilder.Entity<PushDeliveryAttempt>()
            .HasOne(a => a.NotificationLog)
            .WithMany(n => n.Attempts)
            .HasForeignKey(a => a.NotificationLogId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PushDeliveryAttempt>()
            .HasOne(a => a.PushToken)
            .WithMany()
            .HasForeignKey(a => a.PushTokenId)
            .OnDelete(DeleteBehavior.Cascade);

        // The scheduler's "find due retries" query filters on (Status,
        // NextAttemptAt) every tick — index it.
        modelBuilder.Entity<PushDeliveryAttempt>()
            .HasIndex(a => new { a.Status, a.NextAttemptAt });

        // The receipt-checker's "find accepted tickets to verify" query.
        modelBuilder.Entity<PushDeliveryAttempt>()
            .HasIndex(a => a.Status);

        modelBuilder.Entity<UserReport>()
            .HasOne(r => r.Reporter)
            .WithMany()
            .HasForeignKey(r => r.ReporterId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserBlock>()
            .HasOne(b => b.Blocker)
            .WithMany()
            .HasForeignKey(b => b.BlockerId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserBlock>()
            .HasOne(b => b.BlockedUser)
            .WithMany()
            .HasForeignKey(b => b.BlockedUserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserBlock>()
            .HasIndex(b => new { b.BlockerId, b.BlockedUserId })
            .IsUnique();

        modelBuilder.Entity<SupportTicket>()
            .HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SupportMessage>()
            .HasOne(m => m.Ticket)
            .WithMany(t => t.Messages)
            .HasForeignKey(m => m.TicketId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SupportAttachment>()
            .HasOne(a => a.Message)
            .WithMany(m => m.Attachments)
            .HasForeignKey(a => a.MessageId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SupportTicket>()
            .HasIndex(t => t.UserId);

        modelBuilder.Entity<SupportTicket>()
            .HasIndex(t => t.Status);
    }
}
