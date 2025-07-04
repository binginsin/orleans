using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Orleans.Configuration;
using Orleans.Runtime.ReminderService;
using Orleans.TestingHost;
using TestExtensions;
using UnitTests.TestHelper;
using Xunit;
using Xunit.Sdk;

namespace UnitTests.General
{
    /// <summary>
    /// Tests for the consistent ring provider's behavior during silo failures and joins.
    /// </summary>
    public class ConsistentRingProviderTests_Silo : TestClusterPerTest
    {
        private const int numAdditionalSilos = 3;
        private readonly TimeSpan failureTimeout = TimeSpan.FromSeconds(30);
        private readonly TimeSpan endWait = TimeSpan.FromMinutes(5);

        private enum Fail { First, Random, Last }

        protected override void ConfigureTestCluster(TestClusterBuilder builder)
        {
            builder.AddSiloBuilderConfigurator<Configurator>();
            builder.AddClientBuilderConfigurator<Configurator>();
        }

        private class Configurator : ISiloConfigurator, IClientBuilderConfigurator
        {
            public void Configure(ISiloBuilder hostBuilder)
            {
                hostBuilder.AddMemoryGrainStorage("MemoryStore")
                    .AddMemoryGrainStorageAsDefault()
                    .UseInMemoryReminderService();
            }

            public void Configure(IConfiguration configuration, IClientBuilder clientBuilder)
            {
                clientBuilder.Configure<GatewayOptions>(
                    options => options.GatewayListRefreshPeriod = TimeSpan.FromMilliseconds(100));
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_Basic()
        {
            await this.HostedCluster.StartAdditionalSilosAsync(numAdditionalSilos);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            await VerificationScenario(0);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1F_Random()
        {
            await FailureTest(Fail.Random, 1);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1F_Beginning()
        {
            await FailureTest(Fail.First, 1);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1F_End()
        {
            await FailureTest(Fail.Last, 1);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_2F_Random()
        {
            await FailureTest(Fail.Random, 2);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_2F_Beginning()
        {
            await FailureTest(Fail.First, 2);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_2F_End()
        {
            await FailureTest(Fail.Last, 2);
        }

        private async Task FailureTest(Fail failCode, int numOfFailures)
        {
            await this.HostedCluster.StartAdditionalSilosAsync(numAdditionalSilos);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            List<SiloHandle> failures = await getSilosToFail(failCode, numOfFailures);
            foreach (SiloHandle fail in failures) // verify before failure
            {
                await VerificationScenario(PickKey(fail.SiloAddress)); // fail.SiloAddress.GetConsistentHashCode());
            }

            logger.LogInformation(
                "FailureTest {FailureCount}, Code {FailureCode}, Stopping silos: {SiloAddresses}",
                numOfFailures,
                failCode,
                Utils.EnumerableToString(failures, handle => handle.SiloAddress.ToString()));
            List<uint> keysToTest = new List<uint>();
            foreach (SiloHandle fail in failures) // verify before failure
            {
                keysToTest.Add(PickKey(fail.SiloAddress)); //fail.SiloAddress.GetConsistentHashCode());
                await this.HostedCluster.StopSiloAsync(fail);
            }
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            await AssertEventually(async () =>
            {
                foreach (var key in keysToTest) // verify after failure
                {
                    await VerificationScenario(key);
                }
            }, failureTimeout);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1J()
        {
            await JoinTest(1);
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_2J()
        {
            await JoinTest(2);
        }

        private async Task JoinTest(int numOfJoins)
        {
            logger.LogInformation("JoinTest {NumOfJoins}", numOfJoins);
            await this.HostedCluster.StartAdditionalSilosAsync(numAdditionalSilos - numOfJoins);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            List<SiloHandle> silos = await this.HostedCluster.StartAdditionalSilosAsync(numOfJoins);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            foreach (SiloHandle sh in silos)
            {
                await VerificationScenario(PickKey(sh.SiloAddress));
            }
            Thread.Sleep(TimeSpan.FromSeconds(15));
        }

        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1F1J()
        {
            await this.HostedCluster.StartAdditionalSilosAsync(numAdditionalSilos);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            List<SiloHandle> failures = await getSilosToFail(Fail.Random, 1);
            uint keyToCheck = PickKey(failures[0].SiloAddress);// failures[0].SiloAddress.GetConsistentHashCode();
            List<SiloHandle> joins = null;

            // kill a silo and join a new one in parallel
            logger.LogInformation("Killing silo {SiloAddress} and joining a silo", failures[0].SiloAddress);

            var tasks = new Task[2]
            {
                Task.Factory.StartNew(() => this.HostedCluster.StopSiloAsync(failures[0])),
                this.HostedCluster.StartAdditionalSilosAsync(1).ContinueWith(t => joins = t.GetAwaiter().GetResult())
            };
            await Task.WhenAll(tasks).WaitAsync(endWait);

            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            await AssertEventually(async () =>
            {
                await VerificationScenario(keyToCheck); // verify failed silo's key
                await VerificationScenario(PickKey(joins[0].SiloAddress)); // verify newly joined silo's key
            }, failureTimeout);
        }

        // failing the secondary in this scenario exposed the bug in DomainGrain ... so, we keep it as a separate test than Ring_1F1J
        [Fact, TestCategory("Functional"), TestCategory("Ring")]
        public async Task Ring_1Fsec1J()
        {
            await this.HostedCluster.StartAdditionalSilosAsync(numAdditionalSilos);
            await this.HostedCluster.WaitForLivenessToStabilizeAsync();
            //List<SiloHandle> failures = getSilosToFail(Fail.Random, 1);
            SiloHandle fail = this.HostedCluster.SecondarySilos.First();
            uint keyToCheck = PickKey(fail.SiloAddress); //fail.SiloAddress.GetConsistentHashCode();
            List<SiloHandle> joins = null;

            // kill a silo and join a new one in parallel
            logger.LogInformation("Killing secondary silo {SiloAddress} and joining a silo", fail.SiloAddress);
            var tasks = new Task[2]
            {
                Task.Factory.StartNew(() => this.HostedCluster.StopSiloAsync(fail)),
                this.HostedCluster.StartAdditionalSilosAsync(1).ContinueWith(t => joins = t.GetAwaiter().GetResult())
            };
            await Task.WhenAll(tasks).WaitAsync(endWait);

            await this.HostedCluster.WaitForLivenessToStabilizeAsync();

            await AssertEventually(async () =>
            {
                await VerificationScenario(keyToCheck); // verify failed silo's key
                await VerificationScenario(PickKey(joins[0].SiloAddress));
            }, failureTimeout);
        }

        private uint PickKey(SiloAddress responsibleSilo)
        {
            int iteration = 10000;
            var testHooks = this.Client.GetTestHooks(this.HostedCluster.Primary);
            for (int i = 0; i < iteration; i++)
            {
                double next = Random.Shared.NextDouble();
                uint randomKey = (uint)((double)RangeFactory.RING_SIZE * next);
                SiloAddress s = testHooks.GetConsistentRingPrimaryTargetSilo(randomKey).Result;
                if (responsibleSilo.Equals(s))
                    return randomKey;
            }
            throw new Exception(string.Format("Could not pick a key that silo {0} will be responsible for. Primary.Ring = \n{1}",
                responsibleSilo, testHooks.GetConsistentRingProviderDiagnosticInfo().Result));
        }

        private async Task VerificationScenario(uint testKey)
        {
            // setup
            List<SiloAddress> silos = new List<SiloAddress>();

            foreach (var siloHandle in this.HostedCluster.GetActiveSilos())
            {
                long hash = siloHandle.SiloAddress.GetConsistentHashCode();
                int index = silos.FindLastIndex(siloAddr => siloAddr.GetConsistentHashCode() < hash) + 1;
                silos.Insert(index, siloHandle.SiloAddress);
            }

            // verify parameter key
            await VerifyKey(testKey, silos);
            // verify some other keys as well, apart from the parameter key            
            // some random keys
            for (int i = 0; i < 3; i++)
            {
                await VerifyKey((uint)Random.Shared.Next(), silos);
            }
            // lowest key
            uint lowest = (uint)(silos.First().GetConsistentHashCode() - 1);
            await VerifyKey(lowest, silos);
            // highest key
            uint highest = (uint)(silos.Last().GetConsistentHashCode() + 1);
            await VerifyKey(lowest, silos);
        }

        private async Task VerifyKey(uint key, List<SiloAddress> silos)
        {
            var testHooks = this.Client.GetTestHooks(this.HostedCluster.Primary);
            SiloAddress truth = testHooks.GetConsistentRingPrimaryTargetSilo(key).Result; //expected;
            //if (truth == null) // if the truth isn't passed, we compute it here
            //{
            //    truth = silos.Find(siloAddr => (key <= siloAddr.GetConsistentHashCode()));
            //    if (truth == null)
            //    {
            //        truth = silos.First();
            //    }
            //}

            // lookup for 'key' should return 'truth' on all silos
            foreach (var siloHandle in this.HostedCluster.GetActiveSilos()) // do this for each silo
            {
                testHooks = this.Client.GetTestHooks(siloHandle);
                SiloAddress s = await testHooks.GetConsistentRingPrimaryTargetSilo((uint)key);
                Assert.Equal(truth, s);
            }
        }

        private async Task<List<SiloHandle>> getSilosToFail(Fail fail, int numOfFailures)
        {
            List<SiloHandle> failures = new List<SiloHandle>();
            int count = 0;

            // Figure out the primary directory partition and the silo hosting the ReminderTableGrain.
            var tableGrain = this.GrainFactory.GetGrain<IReminderTableGrain>(InMemoryReminderTable.ReminderTableGrainId);
            var tableGrainId = ((GrainReference)tableGrain).GrainId;

            // Ping the grain to make sure it is active.
            await tableGrain.ReadRows(tableGrainId);

            SiloAddress reminderTableGrainPrimaryDirectoryAddress = (await TestUtils.GetDetailedGrainReport(this.HostedCluster.InternalGrainFactory, tableGrainId, this.HostedCluster.Primary)).PrimaryForGrain;
            // ask a detailed report from the directory partition owner, and get the activation addresses
            var address = (await TestUtils.GetDetailedGrainReport(this.HostedCluster.InternalGrainFactory, tableGrainId, this.HostedCluster.GetSiloForAddress(reminderTableGrainPrimaryDirectoryAddress))).LocalDirectoryActivationAddress;
            GrainAddress reminderGrainActivation = address;

            SortedList<int, SiloHandle> ids = new SortedList<int, SiloHandle>();
            foreach (var siloHandle in this.HostedCluster.GetActiveSilos())
            {
                SiloAddress siloAddress = siloHandle.SiloAddress;
                if (siloAddress.Equals(this.HostedCluster.Primary.SiloAddress))
                {
                    continue;
                }
                // Don't fail primary directory partition and the silo hosting the ReminderTableGrain.
                if (siloAddress.Equals(reminderTableGrainPrimaryDirectoryAddress) || siloAddress.Equals(reminderGrainActivation.SiloAddress))
                {
                    continue;
                }
                ids.Add(siloHandle.SiloAddress.GetConsistentHashCode(), siloHandle);
            }

            int index;
            // we should not fail the primary!
            // we can't guarantee semantics of 'Fail' if it evalutes to the primary's address
            switch (fail)
            {
                case Fail.First:
                    index = 0;
                    while (count++ < numOfFailures)
                    {
                        while (failures.Contains(ids.Values[index]))
                        {
                            index++;
                        }
                        failures.Add(ids.Values[index]);
                    }
                    break;
                case Fail.Last:
                    index = ids.Count - 1;
                    while (count++ < numOfFailures)
                    {
                        while (failures.Contains(ids.Values[index]))
                        {
                            index--;
                        }
                        failures.Add(ids.Values[index]);
                    }
                    break;
                case Fail.Random:
                default:
                    while (count++ < numOfFailures)
                    {
                        SiloHandle r = ids.Values[Random.Shared.Next(ids.Count)];
                        while (failures.Contains(r))
                        {
                            r = ids.Values[Random.Shared.Next(ids.Count)];
                        }
                        failures.Add(r);
                    }
                    break;
            }
            return failures;
        }

        // for debugging only
        private void printSilos(string msg)
        {
            SortedList<int, SiloAddress> ids = new SortedList<int, SiloAddress>(numAdditionalSilos + 2);
            foreach (var siloHandle in this.HostedCluster.GetActiveSilos())
            {
                ids.Add(siloHandle.SiloAddress.GetConsistentHashCode(), siloHandle.SiloAddress);
            }
            logger.LogInformation("{Message} list of silos: ", msg);
            foreach (var id in ids.Keys.ToList())
            {
                logger.LogInformation("{From} -> {To}", ids[id], id);
            }
        }

        private static async Task AssertEventually(Func<Task> assertion, TimeSpan timeout)
        {
            await AssertEventually(assertion, timeout, TimeSpan.FromMilliseconds(500));
        }

        private static async Task AssertEventually(Func<Task> assertion, TimeSpan timeout, TimeSpan delayBetweenIterations)
        {
            var sw = Stopwatch.StartNew();

            while (true)
            {
                try
                {
                    await assertion();
                    return;
                }
                catch (XunitException)
                {
                    if (sw.ElapsedMilliseconds > timeout.TotalMilliseconds)
                    {
                        throw;
                    }
                }

                if (delayBetweenIterations > TimeSpan.Zero)
                {
                    Thread.Sleep(delayBetweenIterations);
                }
            }
        }
    }
}
