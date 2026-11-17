using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using GChannel.Shared.Contracts;
using GChannel.Web.Components.Pages;
using GChannel.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;
using Radzen;

namespace GChannel.Tests;

public sealed class NotificationCancellationTests
{
    [Theory]
    [InlineData(ApiRoutes.Notifications)]
    [InlineData(ApiRoutes.PubSubSubscribers)]
    public async Task Leaving_during_initial_loading_completes_without_a_circuit_error(string pendingRoute)
    {
        using var handler = new PausedApiHandler(pendingRoute);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://gchannel.test/") };
        var auth = Substitute.For<AuthenticationStateProvider>();
        auth.GetAuthenticationStateAsync().Returns(new AuthenticationState(new ClaimsPrincipal()));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddHttpClient();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<GoogleTokenProvider>();
        services.AddSingleton(provider => new GChannelApiClient(http, auth, provider.GetRequiredService<GoogleTokenProvider>()));
        services.AddSingleton<NavigationManager, TestNavigationManager>();
        services.AddSingleton(Substitute.For<IJSRuntime>());
        services.AddSingleton<NotificationService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<UiNotifier>();

        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, NullLoggerFactory.Instance);
        TestNotifications? page = null;
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(TestNotifications.Capture)] = (Action<TestNotifications>)(component => page = component)
        });
        await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<TestNotifications>(parameters));
        page.ShouldNotBeNull();

        Task? initialization = null;
        await renderer.Dispatcher.InvokeAsync(() =>
        {
            initialization = page.InitializeAsync();
        });

        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            await renderer.Dispatcher.InvokeAsync(page.Dispose);
        }
        finally
        {
            handler.Release.TrySetResult();
        }

        var initializationTask = initialization.ShouldNotBeNull();
        await initializationTask.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        handler.Requests.ShouldBe(pendingRoute == ApiRoutes.Notifications
            ? [ApiRoutes.Notifications]
            : [ApiRoutes.Notifications, ApiRoutes.PubSubSubscribers]);
    }

    public sealed class TestNotifications : Notifications
    {
        [Parameter]
        public Action<TestNotifications> Capture { get; set; } = _ => { };

        protected override void OnInitialized() => Capture(this);

        // Exercise the page lifecycle without JS-dependent Radzen children.
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
        }

        public Task InitializeAsync() => base.OnAfterRenderAsync(true);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://gchannel.test/", "https://gchannel.test/notifications");
    }

    private sealed class PausedApiHandler(string pendingRoute) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var route = request.RequestUri!.AbsolutePath;
            Requests.Add(route);
            if (route == pendingRoute)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = route switch
                {
                    ApiRoutes.Notifications => JsonContent.Create(new ChannelNotificationsResult()),
                    ApiRoutes.PubSubSubscribers => JsonContent.Create(new SubscriberRegistration()),
                    _ => throw new InvalidOperationException($"Unexpected request: {route}")
                }
            };
        }
    }
}
