using GChannel.Core.GoogleApis;
using GChannel.Core.Messaging;
using GChannel.Core.Persistence.Documents;
using GChannel.Shared.Contracts;
using GChannel.Tests.Fixtures;
using GChannel.Worker.Handlers;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Net;
using Wolverine.Tracking;

namespace GChannel.Tests;

public sealed class SupportCaseNamesTests
{
    [Theory]
    [InlineData("organizations/1/cases/2", "organizations/1/cases/2")]
    [InlineData("organizations/1/cases/2/comments/3", "organizations/1/cases/2")]
    [InlineData("projects/p/cases/42/attachments/a1", "projects/p/cases/42")]
    [InlineData("/organizations/1/cases/2/", "organizations/1/cases/2")]
    [InlineData("organizations/1/supportEventSubscriptions/s", null)]
    [InlineData("organizations/1/cases", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CaseNameOf_strips_child_resources(string? resource, string? expected)
        => SupportCaseNames.CaseNameOf(resource).ShouldBe(expected);

    [Fact]
    public void CaseIdOf_returns_the_case_number()
        => SupportCaseNames.CaseIdOf("organizations/1/cases/98765").ShouldBe("98765");
}

/// <summary>The support event handler and the daily catch-up against a fake Cloud Support service.</summary>
[Collection(PostgresCollection.Name)]
public sealed class SupportHandlerTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Org = "organizations/111";
    private readonly ISupportCasesService _support = Substitute.For<ISupportCasesService>();
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _support.IsConfigured.Returns(true);
        _support.OrganizationName.Returns(Org);
        _host = await WorkerHost.StartAsync(postgres.ConnectionString, b => b.Services.AddSingleton(_support));
        _store = _host.Services.GetRequiredService<IDocumentStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private static string NewCaseId() => Random.Shared.NextInt64(10_000_000, 99_999_999).ToString();

    private static SupportCase Case(string caseId, DateTimeOffset updated, string state = "IN_PROGRESS_GOOGLE_SUPPORT") => new()
    {
        Name = $"{Org}/cases/{caseId}",
        CaseId = caseId,
        DisplayName = "Transfer fails",
        State = state,
        Priority = "P2",
        UpdateTime = updated,
    };

    private static SupportEvent Event(string resource, string type = "COMMENT_ADDED") => new()
    {
        MessageId = "msg-" + Guid.NewGuid().ToString("N"),
        NotificationType = type,
        ResourceName = resource,
        ReceivedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Comment_event_refreshes_the_case_and_records_the_event()
    {
        var caseId = NewCaseId();
        var caseName = $"{Org}/cases/{caseId}";
        _support.GetCaseAsync(caseName, Arg.Any<CancellationToken>()).Returns(new SupportCaseSnapshot(
            Case(caseId, DateTimeOffset.UtcNow),
            [new SupportComment { Name = $"{caseName}/comments/1", Body = "We're looking into it.", FromGoogleSupport = true }],
            []));
        var e = Event($"{caseName}/comments/1");

        await _host.InvokeMessageAndWaitAsync(new SupportEventArrived(e));

        await using var query = _store.QuerySession();
        var record = await query.LoadAsync<SupportCaseRecord>(caseId);
        record.ShouldNotBeNull();
        record.State.ShouldBe("IN_PROGRESS_GOOGLE_SUPPORT");
        record.Comments.ShouldHaveSingleItem().FromGoogleSupport.ShouldBeTrue();

        var stored = await query.LoadAsync<SupportEventRecord>(e.MessageId);
        stored.ShouldNotBeNull();
        stored.CaseId.ShouldBe(caseId);
        stored.Event.CaseName.ShouldBe(caseName);
        stored.Event.Error.ShouldBeNull();
    }

    [Fact]
    public async Task Forbidden_case_read_is_recorded_on_the_event_instead_of_retried()
    {
        var caseId = NewCaseId();
        var caseName = $"{Org}/cases/{caseId}";
        _support.GetCaseAsync(caseName, Arg.Any<CancellationToken>())
            .ThrowsAsync(new Google.GoogleApiException("cloudsupport", "denied") { HttpStatusCode = HttpStatusCode.Forbidden });
        var e = Event(caseName, "CASE_UPDATED");

        await _host.InvokeMessageAndWaitAsync(new SupportEventArrived(e));

        await using var query = _store.QuerySession();
        (await query.LoadAsync<SupportCaseRecord>(caseId)).ShouldBeNull();
        var stored = await query.LoadAsync<SupportEventRecord>(e.MessageId);
        stored.ShouldNotBeNull();
        stored.Event.Error.ShouldNotBeNull();
        stored.Event.Error.ShouldContain("Forbidden");
    }

    [Fact]
    public async Task Catch_up_refreshes_only_new_or_changed_cases()
    {
        var updated = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds());
        string unchangedId = NewCaseId(), changedId = NewCaseId(), newId = NewCaseId();
        await using (var session = _store.LightweightSession())
        {
            session.Store(
                new SupportCaseRecord { Id = unchangedId, Case = Case(unchangedId, updated), UpdateTime = updated },
                new SupportCaseRecord { Id = changedId, Case = Case(changedId, updated.AddDays(-1)), UpdateTime = updated.AddDays(-1) });
            await session.SaveChangesAsync();
        }

        var listed = new[] { Case(unchangedId, updated), Case(changedId, updated), Case(newId, updated) };
        _support.ListCasesAsync(Arg.Any<CancellationToken>()).Returns(listed);
        _support.GetCaseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new SupportCaseSnapshot(listed.Single(c => c.Name == ci.Arg<string>()), [], []));

        var error = await SupportCaseCatchUpHandler.CatchUpAsync(_support, _store, NullLogger.Instance, CancellationToken.None);

        error.ShouldBeNull();
        await _support.DidNotReceive().GetCaseAsync($"{Org}/cases/{unchangedId}", Arg.Any<CancellationToken>());
        await _support.Received(1).GetCaseAsync($"{Org}/cases/{changedId}", Arg.Any<CancellationToken>());
        await _support.Received(1).GetCaseAsync($"{Org}/cases/{newId}", Arg.Any<CancellationToken>());

        await using var query = _store.QuerySession();
        (await query.LoadAsync<SupportCaseRecord>(changedId))!.UpdateTime.ShouldBe(updated);
        (await query.LoadAsync<SupportCaseRecord>(newId)).ShouldNotBeNull();
        (await query.LoadAsync<SyncCursor>(SupportCaseStore.CursorScope)).ShouldNotBeNull();
    }
}
