using System.Diagnostics;
using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;
using Xunit.Abstractions;

namespace DefaultCluster.Tests.TimerTests
{
    /// <summary>
    /// Tests for Orleans Timer functionality.
    /// Timers provide grain-local, non-durable periodic callbacks. Unlike reminders,
    /// timers are active only while a grain is activated and don't persist across
    /// deactivations. They're ideal for short-lived periodic tasks, polling,
    /// timeouts, and other scenarios where persistence isn't required.
    /// Timers are more efficient than reminders for high-frequency operations.
    /// </summary>
    public class TimerOrleansTest : HostedTestClusterEnsureDefaultStarted
    {
        private readonly ITestOutputHelper output;

        public TimerOrleansTest(ITestOutputHelper output, DefaultClusterFixture fixture)
            : base(fixture)
        {
            this.output = output;
        }

        /// <summary>
        /// Tests basic timer functionality including start and stop operations.
        /// Verifies that timers tick at expected intervals, can be stopped,
        /// and that stopping a timer prevents further ticks. This demonstrates
        /// the fundamental timer lifecycle within a grain activation.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task TimerOrleansTest_Basic()
        {
            for (int i = 0; i < 10; i++)
            {
                var grain = GrainFactory.GetGrain<ITimerGrain>(GetRandomGrainId());
                var period = await grain.GetTimerPeriod();
                var timeout = period.Multiply(50);
                var stopwatch = Stopwatch.StartNew();
                var last = 0;
                while (stopwatch.Elapsed < timeout && last < 10)
                {
                    await Task.Delay(period.Divide(2));
                    last = await grain.GetCounter();
                }

                output.WriteLine("value = " + last);
                Assert.True(last >= 10 & last <= 12, last.ToString());

                await grain.StopDefaultTimer();
                await Task.Delay(period.Multiply(2));
                var curr = await grain.GetCounter();
                Assert.True(curr == last || curr == last + 1, "curr == last || curr == last + 1");
            }
        }

        /// <summary>
        /// Tests multiple grains with timers running in parallel.
        /// Verifies that each grain maintains its own independent timer
        /// and that timers across different grain activations don't interfere
        /// with each other, demonstrating timer isolation per grain.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Timers")]
        public async Task TimerOrleansTest_Parallel()
        {
            TimeSpan period = TimeSpan.Zero;
            List<ITimerGrain> grains = new List<ITimerGrain>();
            for (int i = 0; i < 10; i++)
            {
                ITimerGrain grain = GrainFactory.GetGrain<ITimerGrain>(GetRandomGrainId());
                grains.Add(grain);
                period = await grain.GetTimerPeriod(); // activate grains
            }

            var tasks = new List<Task>(grains.Count);
            for (int i = 0; i < grains.Count; i++)
            {
                ITimerGrain grain = grains[i];
                tasks.Add(
                    Task.Run(
                        async () =>
                        {
                            int last = await grain.GetCounter();
                            var stopwatch = Stopwatch.StartNew();
                            var timeout = period.Multiply(50);
                            while (stopwatch.Elapsed < timeout && last < 10)
                            {
                                await Task.Delay(period.Divide(2));
                                last = await grain.GetCounter();
                            }

                            output.WriteLine("value = " + last);
                            Assert.True(last >= 10 && last <= 12, "last >= 10 && last <= 12");
                        }));
            }

            await Task.WhenAll(tasks);
            for (int i = 0; i < grains.Count; i++)
            {
                ITimerGrain grain = grains[i];
                await grain.StopDefaultTimer();
            }
        }

        /// <summary>
        /// Tests timer behavior across grain deactivation and reactivation.
        /// Verifies that timers don't persist across grain lifecycle - when a grain
        /// is deactivated and reactivated, timers start fresh. This demonstrates
        /// the non-durable nature of timers compared to reminders.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Timers")]
        public async Task TimerOrleansTest_Migration()
        {
            ITimerGrain grain = GrainFactory.GetGrain<ITimerGrain>(GetRandomGrainId());
            TimeSpan period = await grain.GetTimerPeriod();

            // Ensure that the grain works as it should.
            var last = await grain.GetCounter();
            var stopwatch = Stopwatch.StartNew();
            var timeout = period.Multiply(50);
            while (stopwatch.Elapsed < timeout && last < 10)
            {
                await Task.Delay(period.Divide(2));
                last = await grain.GetCounter();
            }

            last = await grain.GetCounter();
            output.WriteLine("value = " + last);

            // Restart the grain.
            await grain.Deactivate();
            stopwatch.Restart();
            last = await grain.GetCounter();
            Assert.True(last == 0, "Restarted grains should have zero ticks. Actual: " + last);
            period = await grain.GetTimerPeriod();

            // Poke the grain and ensure it still works as it should.
            while (stopwatch.Elapsed < timeout && last < 10)
            {
                await Task.Delay(period.Divide(2));
                last = await grain.GetCounter();
            }

            last = await grain.GetCounter();
            stopwatch.Stop();

            int maximalNumTicks = (int)Math.Round(stopwatch.Elapsed.Divide(period), MidpointRounding.ToPositiveInfinity);
            Assert.True(
                last <= maximalNumTicks,
                $"Assert: last <= maximalNumTicks. Actual: last = {last}, maximalNumTicks = {maximalNumTicks}");

            output.WriteLine(
                "Total Elapsed time = " + (stopwatch.Elapsed.TotalSeconds) + " sec. Expected Ticks = " + maximalNumTicks +
                ". Actual ticks = " + last);
        }

        /// <summary>
        /// Tests timers that make asynchronous grain calls in their callbacks.
        /// Verifies that timer callbacks can perform grain-to-grain communication
        /// and that exceptions in timer callbacks are captured and don't crash
        /// the grain. Important for timer-based orchestration patterns.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task AsyncTimerTest_GrainCall()
        {
            const string testName = "AsyncTimerTest_GrainCall";
            TimeSpan delay = TimeSpan.FromSeconds(5);
            TimeSpan wait = delay.Multiply(2);

            ITimerCallGrain grain = null;

            Exception error = null;
            try
            {
                grain = GrainFactory.GetGrain<ITimerCallGrain>(GetRandomGrainId());

                await grain.StartTimer(testName, delay);

                await Task.Delay(wait);

                int tickCount = await grain.GetTickCount();
                Assert.Equal(1, tickCount);

                Exception err = await grain.GetException();
                Assert.Null(err); // Should be no exceptions during timer callback
            }
            catch (Exception exc)
            {
                output.WriteLine(exc);
                error = exc;
            }

            try
            {
                if (grain != null) await grain.StopTimer(testName);
            }
            catch (Exception exc)
            {
                // Ignore
                output.WriteLine("Ignoring exception from StopTimer : {0}", exc);
            }

            if (error != null)
            {
                Assert.Fail($"Test {testName} failed with error {error}");
            }
        }

        /// <summary>
        /// Tests all timer creation overloads and their behavior.
        /// Verifies that different timer registration methods (with/without state,
        /// different period specifications) work correctly and that all timer
        /// variants execute their callbacks as expected.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Timers")]
        public async Task GrainTimer_TestAllOverloads()
        {
            var grain = GrainFactory.GetGrain<ITimerRequestGrain>(GetRandomGrainId());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var numTimers = await grain.TestAllTimerOverloads();
            while (true)
            {
                var completedTimers = await grain.PollCompletedTimers().WaitAsync(cts.Token);
                if (completedTimers == numTimers)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cts.Token);
            }

            await grain.TestCompletedTimerResults();
        }

        /// <summary>
        /// Tests that timers can safely dispose themselves from their own callbacks.
        /// Verifies that self-disposal doesn't cause deadlocks or exceptions,
        /// which is important for one-shot timer patterns or timers that
        /// need to cancel themselves based on conditions.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task GrainTimer_DisposeFromCallback()
        {
            // Schedule a timer which disposes itself from its own callback.
            var grain = GrainFactory.GetGrain<ITimerCallGrain>(GetRandomGrainId());
            await grain.RunSelfDisposingTimer();

            var pocoGrain = GrainFactory.GetGrain<IPocoTimerCallGrain>(GetRandomGrainId());
            await pocoGrain.RunSelfDisposingTimer();
        }

        /// <summary>
        /// Tests timer behavior in non-reentrant grains.
        /// Verifies that multiple timers in a non-reentrant grain respect
        /// the grain's concurrency constraints - timer callbacks don't overlap
        /// and are serialized with other grain methods, maintaining the
        /// single-threaded execution model.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task NonReentrantGrainTimer_Test()
        {
            const string testName = "NonReentrantGrainTimer_Test";
            var delay = TimeSpan.FromSeconds(5);
            var wait = delay.Multiply(2);

            var grain = GrainFactory.GetGrain<INonReentrantTimerCallGrain>(GetRandomGrainId());

            // Schedule multiple timers with the same delay
            await grain.StartTimer(testName, delay);
            await grain.StartTimer($"{testName}_1", delay);
            await grain.StartTimer($"{testName}_2", delay);

            // Invoke some non-interleaving methods.
            var externalTicks = 0;
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < wait)
            {
                await grain.ExternalTick("external");
                externalTicks++;
            }

            var tickCount = await grain.GetTickCount();

            Assert.Equal(3 + externalTicks, tickCount);

            var err = await grain.GetException();
            Assert.Null(err); // Should be no exceptions during timer callback

            await grain.StopTimer(testName);
            await grain.StopTimer($"{testName}_1");
            await grain.StopTimer($"{testName}_2");
        }

        /// <summary>
        /// Tests changing timer periods and due times after creation.
        /// Verifies that timer.Change() correctly updates timing parameters,
        /// handles edge cases (infinite, zero, negative values), and that
        /// timers can be safely modified from within their own callbacks.
        /// Essential for adaptive timing scenarios.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task GrainTimer_Change()
        {
            const string testName = nameof(GrainTimer_Change);
            TimeSpan delay = TimeSpan.FromSeconds(5);
            TimeSpan wait = delay.Multiply(2);

            var grain = GrainFactory.GetGrain<ITimerCallGrain>(GetRandomGrainId());

            await grain.StartTimer(testName, delay);

            await Task.Delay(wait);

            int tickCount = await grain.GetTickCount();
            Assert.Equal(1, tickCount);

            await grain.RestartTimer(testName, delay);

            await Task.Delay(wait);

            tickCount = await grain.GetTickCount();
            Assert.Equal(2, tickCount);

            // Infinite timeouts should be valid.
            await grain.RestartTimer(testName, Timeout.InfiniteTimeSpan);
            await grain.RestartTimer(testName, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            // Zero and sub-ms timeouts should be valid (rounded up to 1ms)
            await grain.RestartTimer(testName, TimeSpan.Zero);
            await grain.RestartTimer(testName, TimeSpan.FromMicroseconds(10));
            await grain.RestartTimer(testName, TimeSpan.Zero, TimeSpan.Zero);
            await grain.RestartTimer(testName, TimeSpan.FromMicroseconds(10), TimeSpan.FromMicroseconds(10));
            await grain.RestartTimer(testName, TimeSpan.FromMilliseconds(-0.4));
            await grain.RestartTimer(testName, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(-0.5));

            // Invalid values
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await grain.RestartTimer(testName, TimeSpan.FromSeconds(-5)));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await grain.RestartTimer(testName, TimeSpan.MaxValue));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await grain.RestartTimer(testName, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(-5)));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await grain.RestartTimer(testName, TimeSpan.FromSeconds(1), TimeSpan.MaxValue));

            Exception err = await grain.GetException();
            Assert.Null(err); // Should be no exceptions during timer callback

            // Valid operations called from within a timer: updating the period and disposing the timer.
            var grain2 = GrainFactory.GetGrain<ITimerCallGrain>(GetRandomGrainId());
            await grain2.StartTimer(testName, delay, "update_period");
            await Task.Delay(wait);
            Assert.Null(await grain2.GetException()); // Should be no exceptions during timer callback
            Assert.Equal(1, await grain2.GetTickCount());

            var grain3 = GrainFactory.GetGrain<ITimerCallGrain>(GetRandomGrainId());
            await grain3.StartTimer(testName, delay, "dispose_timer");
            await Task.Delay(wait);
            Assert.Null(await grain3.GetException()); // Should be no exceptions during timer callback
            Assert.Equal(1, await grain3.GetTickCount());

            await grain.StopTimer(testName);
        }

        /// <summary>
        /// Tests basic timer functionality with POCO (Plain Old CLR Object) grains.
        /// Verifies that timers work identically with POCO grains as with
        /// traditional Grain-derived classes, demonstrating that the timer
        /// infrastructure is independent of the grain implementation style.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task TimerOrleansTest_Basic_Poco()
        {
            for (int i = 0; i < 10; i++)
            {
                var grain = GrainFactory.GetGrain<IPocoTimerGrain>(GetRandomGrainId());
                var period = await grain.GetTimerPeriod();
                var timeout = period.Multiply(50);
                var stopwatch = Stopwatch.StartNew();
                var last = 0;
                while (stopwatch.Elapsed < timeout && last < 10)
                {
                    await Task.Delay(period.Divide(2));
                    last = await grain.GetCounter();
                }

                output.WriteLine("value = " + last);
                Assert.True(last >= 10 & last <= 12, last.ToString());

                await grain.StopDefaultTimer();
                await Task.Delay(period.Multiply(2));
                var curr = await grain.GetCounter();
                Assert.True(curr == last || curr == last + 1, "curr == last || curr == last + 1");
            }
        }

        /// <summary>
        /// Tests parallel timer execution across multiple POCO grain instances.
        /// Verifies that POCO grains maintain timer isolation just like
        /// traditional grains, with each instance having independent timers
        /// that don't interfere with each other.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Timers")]
        public async Task TimerOrleansTest_Parallel_Poco()
        {
            TimeSpan period = TimeSpan.Zero;
            List<IPocoTimerGrain> grains = new List<IPocoTimerGrain>();
            for (int i = 0; i < 10; i++)
            {
                IPocoTimerGrain grain = GrainFactory.GetGrain<IPocoTimerGrain>(GetRandomGrainId());
                grains.Add(grain);
                period = await grain.GetTimerPeriod(); // activate grains
            }

            var tasks = new List<Task>(grains.Count);
            for (int i = 0; i < grains.Count; i++)
            {
                IPocoTimerGrain grain = grains[i];
                tasks.Add(
                    Task.Run(
                        async () =>
                        {
                            int last = await grain.GetCounter();
                            var stopwatch = Stopwatch.StartNew();
                            var timeout = period.Multiply(50);
                            while (stopwatch.Elapsed < timeout && last < 10)
                            {
                                await Task.Delay(period.Divide(2));
                                last = await grain.GetCounter();
                            }

                            output.WriteLine("value = " + last);
                            Assert.True(last >= 10 && last <= 12, "last >= 10 && last <= 12");
                        }));
            }

            await Task.WhenAll(tasks);
            for (int i = 0; i < grains.Count; i++)
            {
                IPocoTimerGrain grain = grains[i];
                await grain.StopDefaultTimer();
            }
        }

        /// <summary>
        /// Tests timer behavior across POCO grain deactivation/reactivation.
        /// Verifies that POCO grains exhibit the same non-persistent timer
        /// behavior as traditional grains - timers are lost on deactivation
        /// and start fresh on reactivation.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Timers")]
        public async Task TimerOrleansTest_Migration_Poco()
        {
            IPocoTimerGrain grain = GrainFactory.GetGrain<IPocoTimerGrain>(GetRandomGrainId());
            TimeSpan period = await grain.GetTimerPeriod();

            // Ensure that the grain works as it should.
            var last = await grain.GetCounter();
            var stopwatch = Stopwatch.StartNew();
            var timeout = period.Multiply(50);
            while (stopwatch.Elapsed < timeout && last < 10)
            {
                await Task.Delay(period.Divide(2));
                last = await grain.GetCounter();
            }

            last = await grain.GetCounter();
            output.WriteLine("value = " + last);

            // Restart the grain.
            await grain.Deactivate();
            stopwatch.Restart();
            last = await grain.GetCounter();
            Assert.True(last == 0, "Restarted grains should have zero ticks. Actual: " + last);
            period = await grain.GetTimerPeriod();

            // Poke the grain and ensure it still works as it should.
            while (stopwatch.Elapsed < timeout && last < 10)
            {
                await Task.Delay(period.Divide(2));
                last = await grain.GetCounter();
            }

            last = await grain.GetCounter();
            stopwatch.Stop();

            int maximalNumTicks = (int)Math.Round(stopwatch.Elapsed.Divide(period), MidpointRounding.ToPositiveInfinity);
            Assert.True(
                last <= maximalNumTicks,
                $"Assert: last <= maximalNumTicks. Actual: last = {last}, maximalNumTicks = {maximalNumTicks}");

            output.WriteLine(
                "Total Elapsed time = " + (stopwatch.Elapsed.TotalSeconds) + " sec. Expected Ticks = " + maximalNumTicks +
                ". Actual ticks = " + last);
        }

        /// <summary>
        /// Tests asynchronous grain calls from POCO grain timer callbacks.
        /// Verifies that POCO grain timers can perform async operations
        /// including grain-to-grain calls, with proper exception handling
        /// in the timer callback context.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task AsyncTimerTest_GrainCall_Poco()
        {
            const string testName = "AsyncTimerTest_GrainCall";
            TimeSpan delay = TimeSpan.FromSeconds(5);
            TimeSpan wait = delay.Multiply(2);

            IPocoTimerCallGrain grain = null;

            Exception error = null;
            try
            {
                grain = GrainFactory.GetGrain<IPocoTimerCallGrain>(GetRandomGrainId());

                await grain.StartTimer(testName, delay);

                await Task.Delay(wait);

                int tickCount = await grain.GetTickCount();
                Assert.Equal(1, tickCount);

                Exception err = await grain.GetException();
                Assert.Null(err); // Should be no exceptions during timer callback
            }
            catch (Exception exc)
            {
                output.WriteLine(exc);
                error = exc;
            }

            try
            {
                if (grain != null) await grain.StopTimer(testName);
            }
            catch (Exception exc)
            {
                // Ignore
                output.WriteLine("Ignoring exception from StopTimer : {0}", exc);
            }

            if (error != null)
            {
                Assert.Fail($"Test {testName} failed with error {error}");
            }
        }

        /// <summary>
        /// Tests all timer registration overloads with POCO grains.
        /// Ensures that POCO grains support the full range of timer
        /// registration methods, maintaining API compatibility with
        /// traditional grain implementations.
        /// </summary>
        [Fact, TestCategory("BVT"), TestCategory("Timers")]
        public async Task GrainTimer_TestAllOverloads_Poco()
        {
            var grain = GrainFactory.GetGrain<IPocoTimerRequestGrain>(GetRandomGrainId());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var numTimers = await grain.TestAllTimerOverloads();
            while (true)
            {
                var completedTimers = await grain.PollCompletedTimers().WaitAsync(cts.Token);
                if (completedTimers == numTimers)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), cts.Token);
            }

            await grain.TestCompletedTimerResults();
        }

        /// <summary>
        /// Tests timer concurrency constraints in non-reentrant POCO grains.
        /// Verifies that POCO grains respect reentrancy settings, ensuring
        /// timer callbacks are properly serialized in non-reentrant grains
        /// regardless of the grain implementation style.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task NonReentrantGrainTimer_Test_Poco()
        {
            const string testName = "NonReentrantGrainTimer_Test";
            var delay = TimeSpan.FromSeconds(5);
            var wait = delay.Multiply(2);

            var grain = GrainFactory.GetGrain<IPocoNonReentrantTimerCallGrain>(GetRandomGrainId());

            // Schedule multiple timers with the same delay
            await grain.StartTimer(testName, delay);
            await grain.StartTimer($"{testName}_1", delay);
            await grain.StartTimer($"{testName}_2", delay);

            // Invoke some non-interleaving methods.
            var externalTicks = 0;
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < wait)
            {
                await grain.ExternalTick("external");
                externalTicks++;
            }

            var tickCount = await grain.GetTickCount();

            Assert.Equal(3 + externalTicks, tickCount);

            var err = await grain.GetException();
            Assert.Null(err); // Should be no exceptions during timer callback

            await grain.StopTimer(testName);
            await grain.StopTimer($"{testName}_1");
            await grain.StopTimer($"{testName}_2");
        }

        /// <summary>
        /// Tests dynamic timer period changes in POCO grains.
        /// Verifies that POCO grain timers support runtime modifications
        /// of timing parameters through the Change method, including
        /// edge cases and callback-initiated changes.
        /// </summary>
        [Fact, TestCategory("SlowBVT"), TestCategory("Timers")]
        public async Task GrainTimer_Change_Poco()
        {
            const string testName = nameof(GrainTimer_Change);
            TimeSpan delay = TimeSpan.FromSeconds(5);
            TimeSpan wait = delay.Multiply(2);

            var grain = GrainFactory.GetGrain<IPocoTimerCallGrain>(GetRandomGrainId());

            await grain.StartTimer(testName, delay);

            await Task.Delay(wait);

            int tickCount = await grain.GetTickCount();
            Assert.Equal(1, tickCount);

            await grain.RestartTimer(testName, delay);

            await Task.Delay(wait);

            tickCount = await grain.GetTickCount();
            Assert.Equal(2, tickCount);

            // Infinite timeouts should be valid.
            await grain.RestartTimer(testName, Timeout.InfiniteTimeSpan);
            await grain.RestartTimer(testName, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

            // Zero and sub-ms timeouts should be valid (rounded up to 1ms)
            await grain.RestartTimer(testName, TimeSpan.Zero);
            await grain.RestartTimer(testName, TimeSpan.FromMicroseconds(10));
            await grain.RestartTimer(testName, TimeSpan.Zero, TimeSpan.Zero);
            await grain.RestartTimer(testName, TimeSpan.FromMicroseconds(10), TimeSpan.FromMicroseconds(10));
            await grain.RestartTimer(testName, TimeSpan.FromMilliseconds(-0.4));
            await grain.RestartTimer(testName, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(-0.5));

            // Invalid values
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await grain.RestartTimer(testName, TimeSpan.FromSeconds(-5)));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await grain.RestartTimer(testName, TimeSpan.MaxValue));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await grain.RestartTimer(testName, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(-5)));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await grain.RestartTimer(testName, TimeSpan.FromSeconds(1), TimeSpan.MaxValue));

            Exception err = await grain.GetException();
            Assert.Null(err); // Should be no exceptions during timer callback

            // Valid operations called from within a timer: updating the period and disposing the timer.
            var grain2 = GrainFactory.GetGrain<IPocoTimerCallGrain>(GetRandomGrainId());
            await grain2.StartTimer(testName, delay, "update_period");
            await Task.Delay(wait);
            Assert.Null(await grain2.GetException()); // Should be no exceptions during timer callback
            Assert.Equal(1, await grain2.GetTickCount());

            var grain3 = GrainFactory.GetGrain<IPocoTimerCallGrain>(GetRandomGrainId());
            await grain3.StartTimer(testName, delay, "dispose_timer");
            await Task.Delay(wait);
            Assert.Null(await grain3.GetException()); // Should be no exceptions during timer callback
            Assert.Equal(1, await grain3.GetTickCount());

            await grain.StopTimer(testName);
        }
    }
}
