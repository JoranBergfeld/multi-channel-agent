using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using MultiChannelAgent.Infrastructure.Persistence;

namespace MultiChannelAgent.IntegrationTests;

public sealed class CustomWebApplicationFactoryTests
{
    [Fact]
    public void Factory_uses_the_caller_supplied_SQL_Server_connection_string()
    {
        const string expectedConnectionString =
            "Server=example.invalid,31433;Database=master;User Id=sa;Password=TestOnly1!;TrustServerCertificate=True";

        using var factory = new CustomWebApplicationFactory(expectedConnectionString);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MultiChannelAgentDbContext>();

        var actual = new SqlConnectionStringBuilder(db.Database.GetConnectionString());

        Assert.Equal("example.invalid,31433", actual.DataSource);
        Assert.Equal("master", actual.InitialCatalog);
        Assert.Equal("sa", actual.UserID);
        Assert.True(actual.TrustServerCertificate);
    }
}
