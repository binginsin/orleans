using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Configuration;
using Orleans.Internal;
using Orleans.Metadata;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Streams;
using Orleans.TestingHost;
using Orleans.TestingHost.Utils;
using Tester.CodeGenTests;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.General
{
    /// <summary>
    /// Tests for hosted client functionality in Orleans.
    /// A hosted client is an Orleans client that runs within the same process as the silo,
    /// enabling in-process communication between application code and grains.
    /// This pattern is common in ASP.NET Core applications hosting Orleans.
    /// </summary>
    [TestCategory("BVT"), TestCategory("HostedClient")]
    public class HostedClientTests : IClassFixture<HostedClientTests.Fixture>
    {
        private readonly TimeSpan _timeout = Debugger.IsAttached ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(10);
        private readonly IHost _host;

        public class Fixture : IAsyncLifetime
        {
            private readonly TestClusterPortAllocator portAllocator;
            public IHost Host { get; private set; }

            public Fixture()
            {
                portAllocator = new TestClusterPortAllocator();
            }

            public async Task InitializeAsync()
            {
                var (siloPort, gatewayPort) = portAllocator.AllocateConsecutivePortPairs(1);
                Host = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder()
                    .UseOrleans(siloBuilder =>
                    {
                        siloBuilder
                            .UseLocalhostClustering(siloPort, gatewayPort)
                            .Configure<ClusterOptions>(options =>
                            {
                                options.ClusterId = Guid.NewGuid().ToString();
                                options.ServiceId = Guid.NewGuid().ToString();
                            })
                            .ConfigureLogging(logging => logging.AddDebug())
                            .AddMemoryGrainStorage("PubSubStore")
                            .AddMemoryStreams<DefaultMemoryMessageBodySerializer>("MemStream");
                    })
                    .Build();
                await Host.StartAsync();
            }

            public async Task DisposeAsync()
            {
                try
                {
                    await Host.StopAsync();
                }
                finally
                {
                    Host.Dispose();
                    portAllocator.Dispose();
                }
            }
        }

        public HostedClientTests(Fixture fixture)
        {
            _host = fixture.Host;
        }

        /// <summary>
        /// Tests basic grain invocation from a hosted client.
        /// Verifies that an in-process client can successfully communicate with grains
        /// running in the same process, demonstrating the core hosted client pattern.
        /// </summary>
        [Fact]
        public async Task HostedClient_GrainCallTest()
        {
            var client = _host.Services.GetRequiredService<IClusterClient>();

            var grain = client.GetGrain<ISimpleGrain>(65);
            await grain.SetA(23);
            var val = await grain.GetA();
            Assert.Equal(23, val);
        }

        /// <summary>
        /// Tests timeout behavior for grain calls from a hosted client.
        /// Verifies that response timeouts are properly enforced and that
        /// the runtime correctly tracks running requests during timeout scenarios.
        /// This ensures reliability when grains become unresponsive.
        /// </summary>
        [Fact]
        public async Task HostedClient_TimeoutTest()
        {
            var client = _host.Services.GetRequiredService<IClusterClient>();
            var runtimeClient = _host.Services.GetRequiredService<IRuntimeClient>();
            var typeResolver = _host.Services.GetRequiredService<GrainInterfaceTypeResolver>();

            var stuckGrainType = typeResolver.GetGrainInterfaceType(typeof(IStuckGrain));
            var initialTimeout = runtimeClient.GetResponseTimeout();

            var timeout = TimeSpan.FromSeconds(1);
            var maxTimeout = timeout.Multiply(3.5);

            try
            {
                runtimeClient.SetResponseTimeout(timeout);
                var stopwatch = Stopwatch.StartNew();

                var assertionTask = Assert.ThrowsAsync<TimeoutException>(
                        async () =>
                        {
                            var grain = client.GetGrain<IStuckGrain>(Guid.NewGuid());
                            await grain.RunForever();
                        })
                    .WaitAsync(maxTimeout);

                Assert.Equal(expected: 1, actual: runtimeClient.GetRunningRequestsCount(stuckGrainType));

                await assertionTask;
                stopwatch.Stop();

                Assert.Equal(expected: 0, actual: runtimeClient.GetRunningRequestsCount(stuckGrainType));

                Assert.True(stopwatch.Elapsed >= timeout, $"Waited less than {timeout}. Waited {stopwatch.Elapsed}");
                Assert.True(stopwatch.Elapsed <= maxTimeout, $"Waited longer than {maxTimeout}. Waited {stopwatch.Elapsed}");
                stopwatch.Stop();
            }
            finally
            {
                runtimeClient.SetResponseTimeout(initialTimeout);
            }
        }

        /// <summary>
        /// Tests reference equality semantics for objects passed through grain calls.
        /// Verifies that immutable objects (strings, grain references) maintain reference equality,
        /// while mutable objects (arrays) are copied. Also demonstrates the Immutable<T> wrapper
        /// for preserving reference equality of mutable objects when needed.
        /// </summary>
        [Fact]
        public async Task HostedClient_ReferenceEquality_GrainCallTest()
        {
            var client = _host.Services.GetRequiredService<IClusterClient>();
            var grain = client.GetGrain<IGrainWithGenericMethods>(Guid.NewGuid());

            // Strings are immutable.
            object expected = new string('*', 5);
            var actual = await grain.RoundTrip(expected);
            Assert.Same(expected, actual);

            // Grain references are immutable.
            actual = await grain.RoundTrip(grain);
            Assert.Same(grain, actual);

            // Arrays are not immutable, so a copy is expected.
            var collection = new int[] { 1, 3, 9 };
            actual = await grain.RoundTrip(collection);
            Assert.NotSame(expected, actual);

            // Immutable<T> should round-trip without any copying.
            var expectedImmutable = new Immutable<int[]>(collection);
            var actualImmutable = await grain.RoundTrip(expectedImmutable);
            Assert.Same(expectedImmutable.Value, actualImmutable.Value);
        }

        /// <summary>
        /// Tests the observer pattern from a hosted client.
        /// Demonstrates how clients can register observer objects to receive
        /// notifications from grains, enabling push-based communication patterns.
        /// Verifies that callbacks are properly invoked with the correct context.
        /// </summary>
        [Fact]
        public async Task HostedClient_ObserverTest()
        {
            var client = _host.Services.GetRequiredService<IClusterClient>();

            var handle = new AsyncResultHandle();

            var callbackCounter = new int[1];
            var callbacksReceived = new bool[2];

            var grain = client.GetGrain<ISimpleObserverableGrain>(0);
            var observer = new ObserverTests.SimpleGrainObserver(
                (a, b, result) =>
                {
                    Assert.Null(RuntimeContext.Current);
                    callbackCounter[0]++;

                    if (a == 3 && b == 0)
                        callbacksReceived[0] = true;
                    else if (a == 3 && b == 2)
                        callbacksReceived[1] = true;
                    else
                        throw new ArgumentOutOfRangeException("Unexpected callback with values: a=" + a + ",b=" + b);

                    if (callbackCounter[0] == 1)
                    {
                        // Allow for callbacks occurring in any order
                        Assert.True(callbacksReceived[0] || callbacksReceived[1]);
                    }
                    else if (callbackCounter[0] == 2)
                    {
                        Assert.True(callbacksReceived[0] && callbacksReceived[1]);
                        result.Done = true;
                    }
                    else
                    {
                        Assert.True(false);
                    }
                },
                handle,
                client.ServiceProvider.GetRequiredService<ILogger<ISimpleGrainObserver>>());
            var reference = client.CreateObjectReference<ISimpleGrainObserver>(observer);
            await grain.Subscribe(reference);
            await grain.SetA(3);
            await grain.SetB(2);

            Assert.True(await handle.WaitForFinished(_timeout));

            client.DeleteObjectReference<ISimpleGrainObserver>(reference);
            Assert.NotNull(observer);
        }

        /// <summary>
        /// Tests streaming functionality from a hosted client.
        /// Verifies that clients can subscribe to Orleans streams and receive
        /// messages published to those streams, demonstrating the streaming
        /// abstraction for event-driven communication patterns.
        /// </summary>
        [Fact]
        public async Task HostedClient_StreamTest()
        {
            var client = _host.Services.GetRequiredService<IClusterClient>();

            var handle = new AsyncResultHandle();
            var vals = new List<int>();
            var stream0 = client.GetStreamProvider("MemStream").GetStream<int>("hi", Guid.Empty);
            await stream0.SubscribeAsync(
                (val, token) =>
                {
                    vals.Add(val);
                    if (vals.Count >= 2) handle.Done = true;
                    return Task.CompletedTask;
                });
            var stream = client.GetStreamProvider("MemStream").GetStream<int>("hi", Guid.Empty);
            await stream.OnNextAsync(1);
            await stream.OnNextAsync(409);
            Assert.True(await handle.WaitForFinished(_timeout));
            Assert.Equal(new[] { 1, 409 }, vals);
        }
    }
}
