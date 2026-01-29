using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Phono.Models;

namespace Phono.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }
    
    public DbSet<Track> Tracks { get; set; }
    public DbSet<Album> Albums { get; set; }
    public DbSet<Artist> Artists { get; set; }
    public DbSet<TrackArtist> TrackArtists { get; set; }
    public DbSet<TorrentJob> TorrentJobs { get; set; }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // Configure Track
        modelBuilder.Entity<Track>(entity =>
        {
            entity.HasIndex(t => t.FileName).IsUnique();
            entity.HasOne(t => t.Album)
                  .WithMany(a => a.Tracks)
                  .HasForeignKey(t => t.AlbumId)
                  .OnDelete(DeleteBehavior.SetNull);
        });
        
        // Configure Album
        modelBuilder.Entity<Album>(entity =>
        {
            entity.HasIndex(a => a.Name);
        });
        
        // Configure Artist
        modelBuilder.Entity<Artist>(entity =>
        {
            entity.HasIndex(a => a.Name).IsUnique();
        });
        
        // Configure TrackArtist (many-to-many join table)
        modelBuilder.Entity<TrackArtist>(entity =>
        {
            entity.HasKey(ta => new { ta.TrackId, ta.ArtistId });
            
            entity.HasOne(ta => ta.Track)
                  .WithMany(t => t.TrackArtists)
                  .HasForeignKey(ta => ta.TrackId)
                  .OnDelete(DeleteBehavior.Cascade);
                  
            entity.HasOne(ta => ta.Artist)
                  .WithMany(a => a.TrackArtists)
                  .HasForeignKey(ta => ta.ArtistId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TorrentJob>(entity =>
        {
            entity.HasIndex(t => t.Status);
            entity.HasIndex(t => t.CreatedAt);
        });
    }
}
