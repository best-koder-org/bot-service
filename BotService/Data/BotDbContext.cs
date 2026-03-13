using BotService.Models;
using Microsoft.EntityFrameworkCore;

namespace BotService.Data;

public class BotDbContext : DbContext
{
    public BotDbContext(DbContextOptions<BotDbContext> options) : base(options) { }
    
    public DbSet<BotState> BotStates { get; set; } = null!;
    public DbSet<BotFinding> BotFindings { get; set; } = null!;
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BotState>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PersonaId).IsUnique();
            entity.HasIndex(e => e.KeycloakUserId);
            entity.Property(e => e.Status).HasConversion<string>();
        });
        
        modelBuilder.Entity<BotFinding>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.FoundAt);
            entity.HasIndex(e => e.Type);
            entity.HasIndex(e => e.Severity);
            entity.HasIndex(e => e.AffectedService);
            entity.HasIndex(e => e.IsResolved);
            entity.Property(e => e.Type).HasConversion<string>();
            entity.Property(e => e.Severity).HasConversion<string>();
        });
    }
}
