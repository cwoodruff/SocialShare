using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SocialShare.Core.Data;

namespace SocialShare.Tests.Support;

/// <summary>
/// A real SQLite database in memory. Using the actual provider rather than the in memory one
/// means migrations, value converters and ExecuteUpdate all behave the way they do in production.
/// </summary>
public sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public TestDatabase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        Tenant = new TenantContext();
        Tenant.SetSystem();

        Options = new DbContextOptionsBuilder<SocialShareDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public TenantContext Tenant { get; }

    public DbContextOptions<SocialShareDbContext> Options { get; }

    /// <summary>A fresh context on the same connection, so each unit of work has its own tracker.</summary>
    public SocialShareDbContext NewContext() => new(Options, Tenant);

    public void Dispose() => _connection.Dispose();
}
