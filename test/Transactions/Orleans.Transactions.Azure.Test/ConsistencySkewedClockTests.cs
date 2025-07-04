using Orleans.Transactions.TestKit.xUnit;
using Xunit.Abstractions;
using Xunit;

namespace Orleans.Transactions.AzureStorage.Tests
{
    /// <summary>
    /// Tests for transaction consistency with skewed clock scenarios using Azure Storage.
    /// </summary>
    [TestCategory("AzureStorage"), TestCategory("Transactions-dev")]
    public class ConsistencySkewedClockTests : ConsistencyTransactionTestRunnerxUnit, IClassFixture<SkewedClockTestFixture>
    {
        public ConsistencySkewedClockTests(SkewedClockTestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }

        protected override bool StorageAdaptorHasLimitedCommitSpace => true;
        protected override bool StorageErrorInjectionActive => false;
    }
}
