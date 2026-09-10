using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using SbConsole.Core.Data;

namespace SbConsole.Web.Tests;

/// <summary>
/// Login.razor's caption claims "sessions are audited". This proves it: both a failed and a
/// successful POST /auth/login write an "auth.login" audit row (Succeeded false/true
/// respectively), and POST /auth/logout writes an "auth.logout" row. Runs the real Program.cs
/// end-to-end via WebApplicationFactory -- the same registrations, the same endpoints -- rather
/// than re-implementing the handler logic in isolation, so it also would have caught the missing
/// audit writes this fix addresses.
/// </summary>
public sealed class AuthEndpointsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"sbc-auth-test-{Guid.NewGuid()}.db");
    private readonly string _originalDbPath;
    private readonly string? _originalDataKey;
    private readonly string? _originalAdminPassword;
    private readonly string? _originalApiKey;
    private const string AdminPassword = "correct-horse-battery-staple";

    public AuthEndpointsTests()
    {
        // BootstrapOptions reads these directly via Environment.GetEnvironmentVariable, so the
        // in-process WebApplicationFactory host needs them set on the test process itself.
        _originalDbPath = Environment.GetEnvironmentVariable("SBC_DB_PATH") ?? "";
        _originalDataKey = Environment.GetEnvironmentVariable("SBC_DATA_KEY");
        _originalAdminPassword = Environment.GetEnvironmentVariable("SBC_ADMIN_PASSWORD");
        _originalApiKey = Environment.GetEnvironmentVariable("SBC_API_KEY");

        Environment.SetEnvironmentVariable("SBC_DB_PATH", _dbPath);
        Environment.SetEnvironmentVariable("SBC_DATA_KEY", Convert.ToBase64String(new byte[32]));
        Environment.SetEnvironmentVariable("SBC_ADMIN_PASSWORD", AdminPassword);
        Environment.SetEnvironmentVariable("SBC_API_KEY", "test-api-key");
    }

    [Fact]
    public async Task Failed_login_writes_an_auth_login_audit_row_marked_not_succeeded()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.PostAsync("/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = "wrong-password" }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("failed=true");

        var entries = await ReadAuditEntriesAsync();
        entries.Should().ContainSingle(e => e.Action == "auth.login" && !e.Succeeded && e.Actor == "admin" && e.Target == "-");
    }

    [Fact]
    public async Task Successful_login_writes_an_auth_login_audit_row_marked_succeeded()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var response = await client.PostAsync("/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = AdminPassword }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/");

        var entries = await ReadAuditEntriesAsync();
        entries.Should().ContainSingle(e => e.Action == "auth.login" && e.Succeeded && e.Actor == "admin" && e.Target == "-");
    }

    [Fact]
    public async Task Logout_writes_an_auth_logout_audit_row()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var loginResponse = await client.PostAsync("/auth/login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["password"] = AdminPassword }));
        var authCookie = loginResponse.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("sbc.auth", StringComparison.Ordinal));

        using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        logoutRequest.Headers.Add("Cookie", authCookie.Split(';')[0]);
        using var logoutResponse = await client.SendAsync(logoutRequest);

        logoutResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        logoutResponse.Headers.Location!.ToString().Should().Be("/login");

        var entries = await ReadAuditEntriesAsync();
        entries.Should().ContainSingle(e => e.Action == "auth.logout" && e.Succeeded && e.Actor == "admin" && e.Target == "-");
    }

    private async Task<List<Core.Data.Entities.AuditEntry>> ReadAuditEntriesAsync()
    {
        var options = new DbContextOptionsBuilder<SbcDbContext>().UseSqlite($"Data Source={_dbPath}").Options;
        await using var db = new SbcDbContext(options);
        return await db.AuditEntries.AsNoTracking().ToListAsync();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        Environment.SetEnvironmentVariable("SBC_DB_PATH", string.IsNullOrEmpty(_originalDbPath) ? null : _originalDbPath);
        Environment.SetEnvironmentVariable("SBC_DATA_KEY", _originalDataKey);
        Environment.SetEnvironmentVariable("SBC_ADMIN_PASSWORD", _originalAdminPassword);
        Environment.SetEnvironmentVariable("SBC_API_KEY", _originalApiKey);
    }
}
