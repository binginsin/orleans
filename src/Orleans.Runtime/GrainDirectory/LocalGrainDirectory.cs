using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.GrainDirectory;
using Orleans.Runtime.Scheduler;

#nullable enable
namespace Orleans.Runtime.GrainDirectory
{
    internal sealed partial class LocalGrainDirectory : ILocalGrainDirectory, ISiloStatusListener, ILifecycleParticipant<ISiloLifecycle>
    {
        private readonly ILogger log;
        private readonly SiloAddress? seed;
        private readonly ISiloStatusOracle siloStatusOracle;
        private readonly IInternalGrainFactory grainFactory;
        private readonly object writeLock = new object();
        private readonly IServiceProvider _serviceProvider;
        private DirectoryMembership directoryMembership = DirectoryMembership.Default;

        // Consider: move these constants into an appropriate place
        internal const int HOP_LIMIT = 6; // forward a remote request no more than 5 times
        public static readonly TimeSpan RETRY_DELAY = TimeSpan.FromMilliseconds(200); // Pause 200ms between forwards to let the membership directory settle down
        internal bool Running;
        private Catalog? _catalog;

        internal SiloAddress MyAddress { get; }

        internal IGrainDirectoryCache DirectoryCache { get; }
        internal LocalGrainDirectoryPartition DirectoryPartition { get; }

        public RemoteGrainDirectory RemoteGrainDirectory { get; }
        public RemoteGrainDirectory CacheValidator { get; }

        internal GrainDirectoryHandoffManager HandoffManager { get; }

        public LocalGrainDirectory(
            IServiceProvider serviceProvider,
            ILocalSiloDetails siloDetails,
            ISiloStatusOracle siloStatusOracle,
            IInternalGrainFactory grainFactory,
            Factory<LocalGrainDirectoryPartition> grainDirectoryPartitionFactory,
            IOptions<DevelopmentClusterMembershipOptions> developmentClusterMembershipOptions,
            IOptions<GrainDirectoryOptions> grainDirectoryOptions,
            ILoggerFactory loggerFactory,
            SystemTargetShared systemTargetShared)
        {
            this.log = loggerFactory.CreateLogger<LocalGrainDirectory>();

            MyAddress = siloDetails.SiloAddress;

            this.siloStatusOracle = siloStatusOracle;
            this.grainFactory = grainFactory;

            DirectoryCache = GrainDirectoryCacheFactory.CreateGrainDirectoryCache(serviceProvider, grainDirectoryOptions.Value);

            var primarySiloEndPoint = developmentClusterMembershipOptions.Value.PrimarySiloEndpoint;
            if (primarySiloEndPoint != null)
            {
                this.seed = this.MyAddress.Endpoint.Equals(primarySiloEndPoint) ? this.MyAddress : SiloAddress.New(primarySiloEndPoint, 0);
            }

            DirectoryPartition = grainDirectoryPartitionFactory();
            HandoffManager = new GrainDirectoryHandoffManager(this, siloStatusOracle, grainFactory, grainDirectoryPartitionFactory, loggerFactory);

            RemoteGrainDirectory = new RemoteGrainDirectory(this, Constants.DirectoryServiceType, systemTargetShared);
            CacheValidator = new RemoteGrainDirectory(this, Constants.DirectoryCacheValidatorType, systemTargetShared);

            // add myself to the list of members
            AddServer(MyAddress);

            DirectoryInstruments.RegisterDirectoryPartitionSizeObserve(() => DirectoryPartition.Count);
            DirectoryInstruments.RegisterMyPortionRingDistanceObserve(() => RingDistanceToSuccessor());
            DirectoryInstruments.RegisterMyPortionRingPercentageObserve(() => (((float)this.RingDistanceToSuccessor()) / ((float)(int.MaxValue * 2L))) * 100);
            DirectoryInstruments.RegisterMyPortionAverageRingPercentageObserve(() =>
            {
                var ring = this.directoryMembership.MembershipRingList;
                return ring.Count == 0 ? 0 : ((float)100 / (float)ring.Count);
            });
            DirectoryInstruments.RegisterRingSizeObserve(() => this.directoryMembership.MembershipRingList.Count);
            _serviceProvider = serviceProvider;
        }

        public void Start()
        {
            LogDebugStart();

            Running = true;

            siloStatusOracle.SubscribeToSiloStatusEvents(this);
        }

        // Note that this implementation stops processing directory change requests (Register, Unregister, etc.) when the Stop event is raised.
        // This means that there may be a short period during which no silo believes that it is the owner of directory information for a set of
        // grains (for update purposes), which could cause application requests that require a new activation to be created to time out.
        // The alternative would be to allow the silo to process requests after it has handed off its partition, in which case those changes
        // would receive successful responses but would not be reflected in the eventual state of the directory.
        // It's easy to change this, if we think the trade-off is better the other way.
        public Task StopAsync()
        {
            // This will cause remote write requests to be forwarded to the silo that will become the new owner.
            // Requests might bounce back and forth for a while as membership stabilizes, but they will either be served by the
            // new owner of the grain, or will wind up failing. In either case, we avoid requests succeeding at this silo after we've
            // begun stopping, which could cause them to not get handed off to the new owner.

            //mark Running as false will exclude myself from CalculateGrainDirectoryPartition(grainId)
            Running = false;

            DirectoryPartition.Clear();
            DirectoryCache.Clear();
            return Task.CompletedTask;
        }

        private void AddServer(SiloAddress silo)
        {
            lock (this.writeLock)
            {
                var existing = this.directoryMembership;
                if (existing.MembershipCache.Contains(silo))
                {
                    // we have already cached this silo
                    return;
                }

                // insert new silo in the sorted order
                long hash = silo.GetConsistentHashCode();

                // Find the last silo with hash smaller than the new silo, and insert the latter after (this is why we have +1 here) the former.
                // Notice that FindLastIndex might return -1 if this should be the first silo in the list, but then
                // 'index' will get 0, as needed.
                int index = existing.MembershipRingList.FindLastIndex(siloAddr => siloAddr.GetConsistentHashCode() < hash) + 1;

                this.directoryMembership = new DirectoryMembership(
                    existing.MembershipRingList.Insert(index, silo),
                    existing.MembershipCache.Add(silo));

                HandoffManager.ProcessSiloAddEvent(silo);

                AdjustLocalDirectory(silo, dead: false);
                AdjustLocalCache(silo, dead: false);

                LogDebugSiloAddedSilo(MyAddress, silo);
            }
        }

        private void RemoveServer(SiloAddress silo, SiloStatus status)
        {
            lock (this.writeLock)
            {
                try
                {
                    // Only notify the catalog once. Order is important: call BEFORE updating membershipRingList.
                    _catalog = _serviceProvider.GetRequiredService<Catalog>();
                    _catalog.OnSiloStatusChange(this, silo, status);
                }
                catch (Exception exc)
                {
                    LogErrorCatalogSiloStatusChangeNotificationException(exc, new(silo));
                }

                var existing = this.directoryMembership;
                if (!existing.MembershipCache.Contains(silo))
                {
                    // we have already removed this silo
                    return;
                }

                this.directoryMembership = new DirectoryMembership(
                    existing.MembershipRingList.Remove(silo),
                    existing.MembershipCache.Remove(silo));

                AdjustLocalDirectory(silo, dead: true);
                AdjustLocalCache(silo, dead: true);

                LogDebugSiloRemovedSilo(MyAddress, silo);
            }
        }

        /// <summary>
        /// Adjust local directory following the addition/removal of a silo
        /// </summary>
        private void AdjustLocalDirectory(SiloAddress silo, bool dead)
        {
            // Determine which activations to remove.
            var activationsToRemove = new List<(GrainId, ActivationId)>();
            foreach (var entry in this.DirectoryPartition.GetItems())
            {
                if (entry.Value.Activation is { } address)
                {
                    // Include any activations from dead silos and from predecessors.
                    if (dead && address.SiloAddress!.Equals(silo) || address.SiloAddress!.IsPredecessorOf(silo))
                    {
                        activationsToRemove.Add((entry.Key, address.ActivationId));
                    }
                }
            }

            // Remove all defunct activations.
            foreach (var activation in activationsToRemove)
            {
                DirectoryPartition.RemoveActivation(activation.Item1, activation.Item2);
            }
        }

        /// Adjust local cache following the removal of a silo by dropping:
        /// 1) entries that point to activations located on the removed silo
        /// 2) entries for grains that are now owned by this silo (me)
        /// 3) entries for grains that were owned by this removed silo - we currently do NOT do that.
        ///     If we did 3, we need to do that BEFORE we change the membershipRingList (based on old Membership).
        ///     We don't do that since first cache refresh handles that.
        ///     Second, since Membership events are not guaranteed to be ordered, we may remove a cache entry that does not really point to a failed silo.
        ///     To do that properly, we need to store for each cache entry who was the directory owner that registered this activation (the original partition owner).
        private void AdjustLocalCache(SiloAddress silo, bool dead)
        {
            // remove all records of activations located on the removed silo
            foreach (var tuple in DirectoryCache.KeyValues)
            {
                var activationAddress = tuple.ActivationAddress;

                // 2) remove entries now owned by me (they should be retrieved from my directory partition)
                if (MyAddress.Equals(CalculateGrainDirectoryPartition(activationAddress.GrainId)))
                {
                    DirectoryCache.Remove(activationAddress.GrainId);
                    continue;
                }

                // 1) remove entries that point to activations located on the removed silo
                // For dead silos, remove any activation registered to that silo or one of its predecessors.
                // For new silos, remove any activation registered to one of its predecessors.
                if (activationAddress.SiloAddress!.IsPredecessorOf(silo) || dead && activationAddress.SiloAddress.Equals(silo))
                {
                    DirectoryCache.Remove(activationAddress.GrainId);
                }
            }
        }

        internal SiloAddress? FindPredecessor(SiloAddress silo)
        {
            var existing = directoryMembership.MembershipRingList;
            int index = existing.IndexOf(silo);
            if (index == -1)
            {
                LogWarningFindPredecessorSiloNotInList(silo);
                return null;
            }

            return existing.Count > 1 ? existing[(index == 0 ? existing.Count : index) - 1] : null;
        }

        internal SiloAddress? FindSuccessor(SiloAddress silo)
        {
            var existing = directoryMembership.MembershipRingList;
            int index = existing.IndexOf(silo);
            if (index == -1)
            {
                LogWarningFindSuccessorSiloNotInList(silo);
                return null;
            }

            return existing.Count > 1 ? existing[(index + 1) % existing.Count] : null;
        }

        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            // This silo's status has changed
            if (!Equals(updatedSilo, MyAddress)) // Status change for some other silo
            {
                if (status.IsTerminating())
                {
                    // QueueAction up the "Remove" to run on a system turn
                    CacheValidator.WorkItemGroup.QueueAction(() => RemoveServer(updatedSilo, status));
                }
                else if (status == SiloStatus.Active)      // do not do anything with SiloStatus.Starting -- wait until it actually becomes active
                {
                    // QueueAction up the "Remove" to run on a system turn
                    CacheValidator.WorkItemGroup.QueueAction(() => AddServer(updatedSilo));
                }
            }
        }

        private bool IsValidSilo(SiloAddress? silo) => siloStatusOracle.IsFunctionalDirectory(silo);

        /// <summary>
        /// Finds the silo that owns the directory information for the given grain ID.
        /// This method will only be null when I'm the only silo in the cluster and I'm shutting down
        /// </summary>
        /// <param name="grainId"></param>
        /// <returns></returns>
        public SiloAddress? CalculateGrainDirectoryPartition(GrainId grainId)
        {
            // give a special treatment for special grains
            if (grainId.IsSystemTarget())
            {
                if (Constants.SystemMembershipTableType.Equals(grainId.Type))
                {
                    if (seed == null)
                    {
                        var errorMsg =
                            $"Development clustering cannot run without a primary silo. " +
                            $"Please configure {nameof(DevelopmentClusterMembershipOptions)}.{nameof(DevelopmentClusterMembershipOptions.PrimarySiloEndpoint)} " +
                            "or provide a primary silo address to the UseDevelopmentClustering extension. " +
                            "Alternatively, you may want to use reliable membership, such as Azure Table.";
                        throw new ArgumentException(errorMsg, "grainId = " + grainId);
                    }
                }

                LogTraceSystemTargetLookup(MyAddress, grainId, MyAddress);

                // every silo owns its system targets
                return MyAddress;
            }

            SiloAddress? siloAddress = null;
            int hash = unchecked((int)grainId.GetUniformHashCode());

            // excludeMySelf from being a TargetSilo if we're not running and the excludeThisSIloIfStopping flag is true. see the comment in the Stop method.
            // excludeThisSIloIfStopping flag was removed because we believe that flag complicates things unnecessarily. We can add it back if it turns out that flag
            // is doing something valuable.
            bool excludeMySelf = !Running;

            var existing = this.directoryMembership;
            if (existing.MembershipRingList.Count == 0)
            {
                // If the membership ring is empty, then we're the owner by default unless we're stopping.
                return !Running ? null : MyAddress;
            }

            // need to implement a binary search, but for now simply traverse the list of silos sorted by their hashes
            for (var index = existing.MembershipRingList.Count - 1; index >= 0; --index)
            {
                var item = existing.MembershipRingList[index];
                if (IsSiloNextInTheRing(item, hash, excludeMySelf))
                {
                    siloAddress = item;
                    break;
                }
            }

            if (siloAddress == null)
            {
                // If not found in the traversal, last silo will do (we are on a ring).
                // We checked above to make sure that the list isn't empty, so this should always be safe.
                siloAddress = existing.MembershipRingList[existing.MembershipRingList.Count - 1];
                // Make sure it's not us...
                if (siloAddress.Equals(MyAddress) && excludeMySelf)
                {
                    siloAddress = existing.MembershipRingList.Count > 1 ? existing.MembershipRingList[existing.MembershipRingList.Count - 2] : null;
                }
            }

            if (log.IsEnabled(LogLevel.Trace))
                LogTraceCalculatedDirectoryPartitionOwner(
                    MyAddress,
                    siloAddress,
                    grainId,
                    hash,
                    siloAddress?.GetConsistentHashCode());
            return siloAddress;
        }

        public SiloAddress? CheckIfShouldForward(GrainId grainId, int hopCount, string operationDescription)
        {
            var owner = CalculateGrainDirectoryPartition(grainId);

            if (owner is null || owner.Equals(MyAddress))
            {
                // Either we don't know about any other silos and we're stopping, or we are the owner.
                // Null indicates that the operation should be performed locally.
                // In the case that this host is terminating, any grain registered to this host must terminate.
                return null;
            }

            if (hopCount >= HOP_LIMIT)
            {
                // we are not forwarding because there were too many hops already
                throw new OrleansException($"Silo {MyAddress} is not owner of {grainId}, cannot forward {operationDescription} to owner {owner} because hop limit is reached");
            }

            // forward to the silo that we think is the owner
            return owner;
        }

        public Task<AddressAndTag> RegisterAsync(GrainAddress address, int hopCount) => RegisterAsync(address, previousAddress: null, hopCount: hopCount);

        public async Task<AddressAndTag> RegisterAsync(GrainAddress address, GrainAddress? previousAddress, int hopCount)
        {
            if (hopCount > 0)
            {
                DirectoryInstruments.RegistrationsSingleActRemoteReceived.Add(1);
            }
            else
            {
                DirectoryInstruments.RegistrationsSingleActIssued.Add(1);
            }

            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = this.CheckIfShouldForward(address.GrainId, hopCount, "RegisterAsync");

            // on all silos other than first, we insert a retry delay and recheck owner before forwarding
            if (hopCount > 0 && forwardAddress != null)
            {
                await Task.Delay(RETRY_DELAY);
                forwardAddress = this.CheckIfShouldForward(address.GrainId, hopCount, "RegisterAsync");
                if (forwardAddress is not null)
                {
                    int hash = unchecked((int)address.GrainId.GetUniformHashCode());
                    LogWarningRegisterAsyncNotOwner(
                        address,
                        hash,
                        forwardAddress,
                        hopCount);
                }
            }

            if (forwardAddress == null)
            {
                DirectoryInstruments.RegistrationsSingleActLocal.Add(1);

                var result = DirectoryPartition.AddSingleActivation(address, previousAddress);

                // update the cache so next local lookup will find this ActivationAddress in the cache and we will save full lookup.
                DirectoryCache.AddOrUpdate(result.Address, result.VersionTag);
                return result;
            }
            else
            {
                DirectoryInstruments.RegistrationsSingleActRemoteSent.Add(1);

                // otherwise, notify the owner
                AddressAndTag result = await GetDirectoryReference(forwardAddress).RegisterAsync(address, previousAddress, hopCount + 1);

                // Caching optimization:
                // cache the result of a successful RegisterSingleActivation call, only if it is not a duplicate activation.
                // this way next local lookup will find this ActivationAddress in the cache and we will save a full lookup!
                if (result.Address == null) return result;

                if (!address.Equals(result.Address) || !IsValidSilo(address.SiloAddress)) return result;

                // update the cache so next local lookup will find this ActivationAddress in the cache and we will save full lookup.
                DirectoryCache.AddOrUpdate(result.Address, result.VersionTag);

                return result;
            }
        }

        public Task UnregisterAfterNonexistingActivation(GrainAddress addr, SiloAddress origin)
        {
            LogTraceUnregisterAfterNonexistingActivation(addr, origin);

            if (origin == null || this.directoryMembership.MembershipCache.Contains(origin))
            {
                // the request originated in this cluster, call unregister here
                return UnregisterAsync(addr, UnregistrationCause.NonexistentActivation, 0);
            }
            else
            {
                // the request originated in another cluster, call unregister there
                var remoteDirectory = GetDirectoryReference(origin);
                return remoteDirectory.UnregisterAsync(addr, UnregistrationCause.NonexistentActivation);
            }
        }

        public async Task UnregisterAsync(GrainAddress address, UnregistrationCause cause, int hopCount)
        {
            if (hopCount > 0)
            {
                DirectoryInstruments.UnregistrationsRemoteReceived.Add(1);
            }
            else
            {
                DirectoryInstruments.UnregistrationsIssued.Add(1);
            }

            if (hopCount == 0)
                InvalidateCacheEntry(address);

            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = this.CheckIfShouldForward(address.GrainId, hopCount, "UnregisterAsync");

            // on all silos other than first, we insert a retry delay and recheck owner before forwarding
            if (hopCount > 0 && forwardAddress != null)
            {
                await Task.Delay(RETRY_DELAY);
                forwardAddress = this.CheckIfShouldForward(address.GrainId, hopCount, "UnregisterAsync");
                LogWarningUnregisterAsyncNotOwner(
                    address,
                    forwardAddress,
                    hopCount);
            }

            if (forwardAddress == null)
            {
                // we are the owner
                DirectoryInstruments.UnregistrationsLocal.Add(1);
                DirectoryPartition.RemoveActivation(address.GrainId, address.ActivationId, cause);
            }
            else
            {
                DirectoryInstruments.UnregistrationsRemoteSent.Add(1);
                // otherwise, notify the owner
                await GetDirectoryReference(forwardAddress).UnregisterAsync(address, cause, hopCount + 1);
            }
        }

        // helper method to avoid code duplication inside UnregisterManyAsync
        private void UnregisterOrPutInForwardList(List<GrainAddress> addresses, UnregistrationCause cause, int hopCount,
            ref Dictionary<SiloAddress, List<GrainAddress>>? forward, string context)
        {
            foreach (var address in addresses)
            {
                // see if the owner is somewhere else (returns null if we are owner)
                var forwardAddress = this.CheckIfShouldForward(address.GrainId, hopCount, context);

                if (forwardAddress != null)
                {
                    forward ??= new();
                    if (!forward.TryGetValue(forwardAddress, out var list))
                        forward[forwardAddress] = list = new();
                    list.Add(address);
                }
                else
                {
                    // we are the owner
                    DirectoryInstruments.UnregistrationsLocal.Add(1);

                    DirectoryPartition.RemoveActivation(address.GrainId, address.ActivationId, cause);
                }
            }
        }


        public async Task UnregisterManyAsync(List<GrainAddress> addresses, UnregistrationCause cause, int hopCount)
        {
            if (hopCount > 0)
            {
                DirectoryInstruments.UnregistrationsManyRemoteReceived.Add(1);
            }
            else
            {
                DirectoryInstruments.UnregistrationsManyIssued.Add(1);
            }

            Dictionary<SiloAddress, List<GrainAddress>>? forwardlist = null;

            UnregisterOrPutInForwardList(addresses, cause, hopCount, ref forwardlist, "UnregisterManyAsync");

            // before forwarding to other silos, we insert a retry delay and re-check destination
            if (hopCount > 0 && forwardlist != null)
            {
                await Task.Delay(RETRY_DELAY);
                Dictionary<SiloAddress, List<GrainAddress>>? forwardlist2 = null;
                UnregisterOrPutInForwardList(addresses, cause, hopCount, ref forwardlist2, "UnregisterManyAsync");
                forwardlist = forwardlist2;
                if (forwardlist != null)
                {
                    LogWarningUnregisterManyAsyncNotOwner(
                        forwardlist.Count,
                        hopCount);
                }
            }

            // forward the requests
            if (forwardlist != null)
            {
                var tasks = new List<Task>();
                foreach (var kvp in forwardlist)
                {
                    DirectoryInstruments.UnregistrationsManyRemoteSent.Add(1);
                    tasks.Add(GetDirectoryReference(kvp.Key).UnregisterManyAsync(kvp.Value, cause, hopCount + 1));
                }

                // wait for all the requests to finish
                await Task.WhenAll(tasks);
            }
        }


        public bool LocalLookup(GrainId grain, out AddressAndTag result)
        {
            DirectoryInstruments.LookupsLocalIssued.Add(1);

            var silo = CalculateGrainDirectoryPartition(grain);

            LogDebugLocalLookupAttempt(
                MyAddress,
                grain,
                silo,
                new(grain),
                new(silo));

            //this will only happen if I'm the only silo in the cluster and I'm shutting down
            if (silo == null)
            {
                LogTraceLocalLookupMineNull(grain);
                result = default;
                return false;
            }

            // handle cache
            DirectoryInstruments.LookupsCacheIssued.Add(1);
            var address = GetLocalCacheData(grain);
            if (address != default)
            {
                result = new(address, 0);

                LogTraceLocalLookupCache(grain, result.Address);
                DirectoryInstruments.LookupsCacheSuccesses.Add(1);
                DirectoryInstruments.LookupsLocalSuccesses.Add(1);
                return true;
            }

            // check if we own the grain
            if (silo.Equals(MyAddress))
            {
                DirectoryInstruments.LookupsLocalDirectoryIssued.Add(1);
                result = GetLocalDirectoryData(grain);
                if (result.Address == null)
                {
                    // it can happen that we cannot find the grain in our partition if there were
                    // some recent changes in the membership
                    LogTraceLocalLookupMineNull(grain);
                    return false;
                }
                LogTraceLocalLookupMine(grain, result.Address);
                DirectoryInstruments.LookupsLocalDirectorySuccesses.Add(1);
                DirectoryInstruments.LookupsLocalSuccesses.Add(1);
                return true;
            }

            LogTraceTryFullLookupElse(grain);
            result = default;
            return false;
        }

        public AddressAndTag GetLocalDirectoryData(GrainId grain) => DirectoryPartition.LookUpActivation(grain);

        public GrainAddress? GetLocalCacheData(GrainId grain) => DirectoryCache.LookUp(grain, out var cache) && IsValidSilo(cache.SiloAddress) ? cache : null;

        public async Task<AddressAndTag> LookupAsync(GrainId grainId, int hopCount = 0)
        {
            if (hopCount > 0)
            {
                DirectoryInstruments.LookupsRemoteReceived.Add(1);
            }
            else
            {
                DirectoryInstruments.LookupsFullIssued.Add(1);
            }

            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = this.CheckIfShouldForward(grainId, hopCount, "LookUpAsync");

            // on all silos other than first, we insert a retry delay and recheck owner before forwarding
            if (hopCount > 0 && forwardAddress != null)
            {
                await Task.Delay(RETRY_DELAY);
                forwardAddress = this.CheckIfShouldForward(grainId, hopCount, "LookUpAsync");
                if (forwardAddress is not null)
                {
                    int hash = unchecked((int)grainId.GetUniformHashCode());
                    LogWarningLookupAsyncNotOwner(
                        grainId,
                        hash,
                        forwardAddress,
                        hopCount);
                }
            }

            if (forwardAddress == null)
            {
                // we are the owner
                DirectoryInstruments.LookupsLocalDirectoryIssued.Add(1);
                var localResult = DirectoryPartition.LookUpActivation(grainId);
                if (localResult.Address == null)
                {
                    // it can happen that we cannot find the grain in our partition if there were
                    // some recent changes in the membership
                    LogTraceFullLookupMineNone(grainId);
                    return new(default, GrainInfo.NO_ETAG);
                }

                LogTraceFullLookupMine(grainId, localResult.Address);
                DirectoryInstruments.LookupsLocalDirectorySuccesses.Add(1);
                return localResult;
            }
            else
            {
                // Just a optimization. Why sending a message to someone we know is not valid.
                if (!IsValidSilo(forwardAddress))
                {
                    throw new OrleansException($"Current directory at {MyAddress} is not stable to perform the lookup for grainId {grainId} (it maps to {forwardAddress}, which is not a valid silo). Retry later.");
                }

                DirectoryInstruments.LookupsRemoteSent.Add(1);
                var result = await GetDirectoryReference(forwardAddress).LookupAsync(grainId, hopCount + 1);

                // update the cache
                if (result.Address is { } address && IsValidSilo(address.SiloAddress))
                {
                    DirectoryCache.AddOrUpdate(address, result.VersionTag);
                }

                LogTraceFullLookupRemote(grainId, result.Address);

                return result;
            }
        }

        public async Task DeleteGrainAsync(GrainId grainId, int hopCount)
        {
            // see if the owner is somewhere else (returns null if we are owner)
            var forwardAddress = this.CheckIfShouldForward(grainId, hopCount, "DeleteGrainAsync");

            // on all silos other than first, we insert a retry delay and recheck owner before forwarding
            if (hopCount > 0 && forwardAddress != null)
            {
                await Task.Delay(RETRY_DELAY);
                forwardAddress = this.CheckIfShouldForward(grainId, hopCount, "DeleteGrainAsync");
                LogWarningDeleteGrainAsyncNotOwner(
                    grainId,
                    forwardAddress,
                    hopCount);
            }

            if (forwardAddress == null)
            {
                // we are the owner
                DirectoryPartition.RemoveGrain(grainId);
            }
            else
            {
                // otherwise, notify the owner
                DirectoryCache.Remove(grainId);
                await GetDirectoryReference(forwardAddress).DeleteGrainAsync(grainId, hopCount + 1);
            }
        }

        public void InvalidateCacheEntry(GrainId grainId)
        {
            DirectoryCache.Remove(grainId);
        }

        public void InvalidateCacheEntry(GrainAddress activationAddress)
        {
            DirectoryCache.Remove(activationAddress);
        }

        /// <summary>
        /// For testing purposes only.
        /// Returns the silo that this silo thinks is the primary owner of directory information for
        /// the provided grain ID.
        /// </summary>
        /// <param name="grain"></param>
        /// <returns></returns>
        public SiloAddress? GetPrimaryForGrain(GrainId grain) => CalculateGrainDirectoryPartition(grain);

        private long RingDistanceToSuccessor() => FindSuccessor(MyAddress) is { } successor ? CalcRingDistance(MyAddress, successor) : 0;

        private static long CalcRingDistance(SiloAddress silo1, SiloAddress silo2)
        {
            const long ringSize = int.MaxValue * 2L;
            long hash1 = silo1.GetConsistentHashCode();
            long hash2 = silo2.GetConsistentHashCode();

            if (hash2 > hash1) return hash2 - hash1;
            if (hash2 < hash1) return ringSize - (hash1 - hash2);

            return 0;
        }

        internal IRemoteGrainDirectory GetDirectoryReference(SiloAddress silo)
        {
            return this.grainFactory.GetSystemTarget<IRemoteGrainDirectory>(Constants.DirectoryServiceType, silo);
        }

        private bool IsSiloNextInTheRing(SiloAddress siloAddr, int hash, bool excludeMySelf)
        {
            return siloAddr.GetConsistentHashCode() <= hash && (!excludeMySelf || !siloAddr.Equals(MyAddress));
        }

        public bool IsSiloInCluster(SiloAddress silo)
        {
            return this.directoryMembership.MembershipCache.Contains(silo);
        }

        public void AddOrUpdateCacheEntry(GrainId grainId, SiloAddress siloAddress) => this.DirectoryCache.AddOrUpdate(new GrainAddress { GrainId = grainId, SiloAddress = siloAddress }, 0);
        public bool TryCachedLookup(GrainId grainId, [NotNullWhen(true)] out GrainAddress? address) => (address = GetLocalCacheData(grainId)) is not null;
        void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
        {
            lifecycle.Subscribe<LocalGrainDirectory>(ServiceLifecycleStage.RuntimeServices, (ct) => Task.Run(() => Start()), (ct) => Task.Run(() => StopAsync()));
        }

        private readonly struct SiloAddressLogValue(SiloAddress silo)
        {
            public override string ToString() => silo.ToStringWithHashCode();
        }

        private readonly struct GrainHashLogValue(GrainId grain)
        {
            public override string ToString() => grain.GetUniformHashCode().ToString();
        }

        private readonly struct SiloHashLogValue(SiloAddress? silo)
        {
            public override string ToString() => silo?.GetConsistentHashCode().ToString() ?? "null";
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Start"
        )]
        private partial void LogDebugStart();

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Silo {SiloAddress} added silo {OtherSiloAddress}"
        )]
        private partial void LogDebugSiloAddedSilo(SiloAddress siloAddress, SiloAddress otherSiloAddress);

        [LoggerMessage(
            EventId = (int)ErrorCode.Directory_SiloStatusChangeNotification_Exception,
            Level = LogLevel.Error,
            Message = "CatalogSiloStatusListener.SiloStatusChangeNotification has thrown an exception when notified about removed silo {Silo}."
        )]
        private partial void LogErrorCatalogSiloStatusChangeNotificationException(Exception exception, SiloAddressLogValue silo);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Silo {LocalSilo} removed silo {OtherSilo}"
        )]
        private partial void LogDebugSiloRemovedSilo(SiloAddress localSilo, SiloAddress otherSilo);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100201,
            Level = LogLevel.Warning,
            Message = "Got request to find predecessors of silo {SiloAddress}, which is not in the list of members"
        )]
        private partial void LogWarningFindPredecessorSiloNotInList(SiloAddress siloAddress);

        [LoggerMessage(
            EventId = (int)ErrorCode.Runtime_Error_100203,
            Level = LogLevel.Warning,
            Message = "Got request to find successors of silo {SiloAddress}, which is not in the list of members"
        )]
        private partial void LogWarningFindSuccessorSiloNotInList(SiloAddress siloAddress);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Silo {SiloAddress} looked for a system target {GrainId}, returned {TargetSilo}"
        )]
        private partial void LogTraceSystemTargetLookup(SiloAddress siloAddress, GrainId grainId, SiloAddress targetSilo);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Silo {SiloAddress} calculated directory partition owner silo {OwnerAddress} for grain {GrainId}: {GrainIdHash} --> {OwnerAddressHash}"
        )]
        private partial void LogTraceCalculatedDirectoryPartitionOwner(SiloAddress siloAddress, SiloAddress? ownerAddress, GrainId grainId, int grainIdHash, long? ownerAddressHash);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "RegisterAsync - It seems we are not the owner of activation {Address} (hash: {Hash:X}), trying to forward it to {ForwardAddress} (hopCount={HopCount})"
        )]
        private partial void LogWarningRegisterAsyncNotOwner(GrainAddress address, int hash, SiloAddress forwardAddress, int hopCount);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "UnregisterAfterNonexistingActivation addr={Address} origin={Origin}"
        )]
        private partial void LogTraceUnregisterAfterNonexistingActivation(GrainAddress address, SiloAddress origin);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "UnregisterAsync - It seems we are not the owner of activation {Address}, trying to forward it to {ForwardAddress} (hopCount={HopCount})"
        )]
        private partial void LogWarningUnregisterAsyncNotOwner(GrainAddress address, SiloAddress? forwardAddress, int hopCount);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "RegisterAsync - It seems we are not the owner of some activations, trying to forward it to {Count} silos (hopCount={HopCount})"
        )]
        private partial void LogWarningUnregisterManyAsyncNotOwner(int count, int hopCount);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Silo {SiloAddress} tries to lookup for {Grain}-->{PartitionOwner} ({GrainHashCode}-->{PartitionOwnerHashCode})"
        )]
        private partial void LogDebugLocalLookupAttempt(SiloAddress siloAddress, GrainId grain, SiloAddress? partitionOwner, GrainHashLogValue grainHashCode, SiloHashLogValue partitionOwnerHashCode);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "LocalLookup mine {GrainId}=null"
        )]
        private partial void LogTraceLocalLookupMineNull(GrainId grainId);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "LocalLookup cache {GrainId}={TargetAddress}"
        )]
        private partial void LogTraceLocalLookupCache(GrainId grainId, GrainAddress? targetAddress);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "LocalLookup mine {GrainId}={Address}"
        )]
        private partial void LogTraceLocalLookupMine(GrainId grainId, GrainAddress? address);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "TryFullLookup else {GrainId}=null"
        )]
        private partial void LogTraceTryFullLookupElse(GrainId grainId);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "LookupAsync - It seems we are not the owner of grain {GrainId} (hash: {Hash:X}), trying to forward it to {ForwardAddress} (hopCount={HopCount})"
        )]
        private partial void LogWarningLookupAsyncNotOwner(GrainId grainId, int hash, SiloAddress forwardAddress, int hopCount);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "FullLookup mine {GrainId}=none"
        )]
        private partial void LogTraceFullLookupMineNone(GrainId grainId);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "FullLookup mine {GrainId}={Address}"
        )]
        private partial void LogTraceFullLookupMine(GrainId grainId, GrainAddress? address);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "FullLookup remote {GrainId}={Address}"
        )]
        private partial void LogTraceFullLookupRemote(GrainId grainId, GrainAddress? address);

        [LoggerMessage(
            Level = LogLevel.Warning,
            Message = "DeleteGrainAsync - It seems we are not the owner of grain {GrainId}, trying to forward it to {ForwardAddress} (hopCount={HopCount})"
        )]
        private partial void LogWarningDeleteGrainAsyncNotOwner(GrainId grainId, SiloAddress? forwardAddress, int hopCount);

        private class DirectoryMembership
        {
            public DirectoryMembership(ImmutableList<SiloAddress> membershipRingList, ImmutableHashSet<SiloAddress> membershipCache)
            {
                this.MembershipRingList = membershipRingList;
                this.MembershipCache = membershipCache;
            }

            public static DirectoryMembership Default { get; } = new DirectoryMembership(ImmutableList<SiloAddress>.Empty, ImmutableHashSet<SiloAddress>.Empty);


            public ImmutableList<SiloAddress> MembershipRingList { get; }
            public ImmutableHashSet<SiloAddress> MembershipCache { get; }
        }
    }
}
