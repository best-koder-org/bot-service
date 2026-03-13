using BotService.Models;
using Microsoft.EntityFrameworkCore;

namespace BotService.Data;

public class BotDbContext : DbContext
{
    public BotDbContext(DbContextOptions<BotDbContext> options) : base(options) { }
    
    public DbSet<BotState> BotStates { get; set; } = null!;
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BotState>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.PersonaId).IsUnique();
            entity.HasIndex(e => e.KeycloakUserId);
            entity.Property(e => e.Status).HasConversion<string>();
        });
    }
}
