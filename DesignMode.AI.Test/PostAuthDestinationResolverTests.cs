using DesignMode.AI.Shared.Navigation;

namespace DesignMode.AI.Test;

[TestClass]
public sealed class PostAuthDestinationResolverTests
{
    [DataTestMethod]
    [DataRow("/designerintro?funnelId=f&name=A%26B#step2")]
    [DataRow("/designerintro2?name=Acme")]
    [DataRow("/designerintrowebsite?funnelId=f")]
    public async Task ActiveDesignerIntro_ReplacesSocialLandingFallback(string route)
    {
        var store = new RoutingStore { User = new("id", null) };
        Assert.AreEqual(route, await PostAuthDestinationResolver.ResolveAsync("name", null, store, currentFunnelRoute: route));
    }

    [DataTestMethod]
    [DataRow("https://evil.example/designerintro")]
    [DataRow("//evil.example/designerintro")]
    [DataRow("/init/dashboard")]
    [DataRow("/designerintro/../login")]
    public async Task UnsafeOrUnrelatedFunnelRoute_DoesNotReplaceDefault(string route)
    {
        Assert.AreEqual("/social-landing.html", await PostAuthDestinationResolver.ResolveAsync(
            "name", null, new RoutingStore(), currentFunnelRoute: route));
    }

    [DataTestMethod]
    [DataRow("/init/ideas", null, false, "/init/ideas")]
    [DataRow(null, "/init/gen?s=1&cid=ABC", false, "/init/gen?s=1&cid=ABC")]
    [DataRow(null, null, true, "/init/dashboard")]
    public async Task ActiveDesignerIntro_PreservesHigherPriorityDestinations(string? returnUrl, string? current, bool paid, string expected)
    {
        var store = new RoutingStore { User = new("id", current), Paid = paid };
        Assert.AreEqual(expected, await PostAuthDestinationResolver.ResolveAsync(
            "name", returnUrl, store, currentFunnelRoute: "/designerintro"));
    }

    [DataTestMethod]
    [DataRow(null, false)]
    [DataRow("/init/ideas", false)]
    [DataRow(null, true)]
    [DataRow("/init/ideas", true)]
    public async Task ReturnUrl_WinsWithoutAnyLookups(string? current, bool paid)
    {
        var store = new RoutingStore { User = new("canonical-id", current), Paid = paid };
        var destination = "/init/gen?s=1&cid=ABC&label=A%26B#preview";
        Assert.AreEqual(destination, await PostAuthDestinationResolver.ResolveAsync("name", destination, store));
        Assert.AreEqual(0, store.UserCalls);
        Assert.AreEqual(0, store.SubscriptionCalls);
    }

    [DataTestMethod]
    [DataRow("/designerintro")]
    [DataRow("/init/ideas")]
    [DataRow("/init/gen?s=1&cid=ABC")]
    [DataRow("/designerintrowebsite?name=Acme&industry=Design&funnelId=f")]
    public async Task CurrentPage_WinsOverSubscription(string current)
    {
        var store = new RoutingStore { User = new("canonical-id", current), Paid = true };
        Assert.AreEqual(current, await PostAuthDestinationResolver.ResolveAsync("name", null, store));
        Assert.AreEqual(1, store.UserCalls);
        Assert.AreEqual(0, store.SubscriptionCalls);
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("https://external.example/init/ideas")]
    [DataRow("//evil.example")]
    [DataRow("javascript:alert(1)")]
    [DataRow("/login")]
    [DataRow("/%2flocal")]
    [DataRow("/broken%")]
    public async Task AbsentOrUnsafeCurrentPage_UsesCanonicalIdForSubscription(string? current)
    {
        var store = new RoutingStore { User = new("opaque-users-id", current), Paid = true };
        Assert.AreEqual("/init/dashboard", await PostAuthDestinationResolver.ResolveAsync("username@example.invalid", null, store));
        Assert.AreEqual("username@example.invalid", store.Username);
        Assert.AreEqual("opaque-users-id", store.SubscriptionUserId);
    }

    [DataTestMethod]
    [DataRow("https://evil.example")]
    [DataRow("http://evil.example")]
    [DataRow("//evil.example")]
    [DataRow("javascript:alert(1)")]
    [DataRow("data:text/html,hello")]
    [DataRow("/\\evil.example")]
    [DataRow("/%5cevil.example")]
    [DataRow("/%2fevil.example")]
    [DataRow("/%252fevil.example")]
    [DataRow("/a/../login")]
    [DataRow("/%6cogin")]
    [DataRow("/oauth/callback")]
    [DataRow("/init/ideas%0a")]
    [DataRow("/init/ideas?bad=%GG")]
    public async Task UnsafeReturnUrl_ContinuesWithCurrentPage(string url)
    {
        var store = new RoutingStore { User = new("id", "/init/ideas") };
        Assert.AreEqual("/init/ideas", await PostAuthDestinationResolver.ResolveAsync("name", url, store));
    }

    [DataTestMethod]
    [DataRow("signup")]
    [DataRow("login")]
    [DataRow("google")]
    public async Task NoProgressOrPaidAccess_DefaultsToSocialLanding(string method)
    {
        var store = new RoutingStore { User = new("id", null) };
        Assert.AreEqual("/social-landing.html", await PostAuthDestinationResolver.ResolveAsync(method, null, store));
        Assert.AreEqual(1, store.SubscriptionCalls);
    }

    [TestMethod]
    public async Task MissingUser_DoesNotQuerySubscriptions()
    {
        var store = new RoutingStore();
        Assert.AreEqual("/social-landing.html", await PostAuthDestinationResolver.ResolveAsync("name", null, store));
        Assert.AreEqual(0, store.SubscriptionCalls);
    }

    [DataTestMethod]
    [DataRow(true, "[AUTH ROUTING] user lookup failed")]
    [DataRow(false, "[AUTH ROUTING] subscription lookup failed")]
    public async Task LookupFailure_FailsClosedAndLogsNoPrivateValues(bool userFailure, string expected)
    {
        var store = new RoutingStore
        {
            User = new("private-user-id", null), ThrowUser = userFailure, ThrowSubscription = !userFailure
        };
        var logs = new List<string>();
        Assert.AreEqual("/social-landing.html", await PostAuthDestinationResolver.ResolveAsync("private-name", null, store, logs.Add));
        CollectionAssert.Contains(logs, expected);
        Assert.IsFalse(logs.Any(message => message.Contains("private")));
    }

    public sealed class RoutingStore : IPostAuthRoutingStore
    {
        public PostAuthUser? User { get; set; }
        public bool Paid { get; set; }
        public bool ThrowUser { get; set; }
        public bool ThrowSubscription { get; set; }
        public bool ThrowSave { get; set; }
        public string? SavedUsername { get; private set; }
        public string? SavedCurrentPage { get; private set; }
        public int UserCalls { get; private set; }
        public int SubscriptionCalls { get; private set; }
        public string? Username { get; private set; }
        public string? SubscriptionUserId { get; private set; }

        public Task<PostAuthUser?> FindUserAsync(string username)
        {
            UserCalls++;
            Username = username;
            if (ThrowUser) throw new InvalidOperationException("private-name");
            return Task.FromResult(User);
        }

        public Task<bool> HasPaidSubscriptionAsync(string userId)
        {
            SubscriptionCalls++;
            SubscriptionUserId = userId;
            if (ThrowSubscription) throw new InvalidOperationException("private-user-id");
            return Task.FromResult(Paid);
        }

        public Task SaveCurrentPageAsync(string username, string currentPage)
        {
            if (ThrowSave) throw new InvalidOperationException("private database failure");
            SavedUsername = username;
            SavedCurrentPage = currentPage;
            User = new(User?.Id ?? "canonical-users-id", currentPage);
            return Task.CompletedTask;
        }
    }
}
