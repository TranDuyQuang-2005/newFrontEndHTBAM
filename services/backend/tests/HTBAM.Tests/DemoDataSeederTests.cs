using HTBAM.Api.Support;
using HTBAM.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace HTBAM.Tests;

public sealed class DemoDataSeederTests
{
    [Fact]
    public async Task Disabled_development_seed_does_not_create_accounts_or_demo_catalogs()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DemoDataSeeder.SeedAsync(db));
        Assert.Empty(await db.UsersSet.ToArrayAsync());
        Assert.Empty(await db.FacultiesSet.ToArrayAsync());
    }

    [Fact]
    public async Task System_and_academic_seed_have_no_demo_users_or_dates_and_repair_missing_periods()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await SystemSeed.SeedAsync(db); await AcademicSeed.SeedAsync(db);
        var period = await db.BellPeriods.FirstAsync(); db.BellPeriods.Remove(period); await db.SaveChangesAsync();
        await SystemSeed.SeedAsync(db); await AcademicSeed.SeedAsync(db);
        Assert.Equal(3, await db.RolesSet.CountAsync());
        Assert.Empty(await db.UsersSet.ToArrayAsync());
        Assert.Equal(32, await db.BellPeriods.CountAsync());
        Assert.All(await db.AcademicTerms.ToArrayAsync(), t => { Assert.False(t.IsDevelopment); Assert.False(t.DatesConfirmed); Assert.Null(t.EndsOn); });
    }
}
