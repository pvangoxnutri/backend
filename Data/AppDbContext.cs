using Microsoft.EntityFrameworkCore;
using sidequest.backend.Models;

namespace sidequest.backend.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Trip> Trips => Set<Trip>();
}
