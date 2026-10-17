using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.GameServer.Services;
using Serilog;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Table-backed, persistent state for the configured treasure pass. Only the normal reward track is claimable until
    /// this server has a verified purchase path for the advanced track.
    /// </summary>
    public sealed class TreasureHandler(
        ITableService tableService,
        IAccountStateService accountState,
        IRewardService rewardService) : TreasureHandlerBase
    {
        private const uint ActiveTreasureId = 60015;
        private const string StateKey = "treasure.pass.60015";
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, object> AccountLocks = new();

        public override void HandleTreasureData(CS_TreasureData request, Connection connection)
        {
            TreasurePassConfig? pass = GetActivePass();
            if (pass is null)
            {
                Log.Warning("Treasure pass {TreasureId} is absent or has invalid table links.", ActiveTreasureId);
                connection.Send(new SC_TreasureData());
                return;
            }

            TreasurePassState state = connection.Account is null
                ? new TreasurePassState()
                : GetState(connection.Account.Uid, pass);
            connection.Send(new SC_TreasureData
            {
                LLLDFCFANII =
                {
                    [pass.Main.Id] = BuildProtoState(pass, state),
                },
            });
        }

        public override void HandleTreasureTakeReward(CS_TreasureTakeReward request, Connection connection)
        {
            if (connection.Account is null)
                return;

            TreasurePassConfig? pass = GetActivePass();
            if (pass is null || request.ONMNEMDCEIJ != pass.Main.Id)
            {
                connection.Send(new SC_TreasureTakeReward());
                return;
            }

            uint uid = connection.Account.Uid;
            lock (AccountLocks.GetOrAdd(uid, static _ => new object()))
            {
                List<IMessage> updates = [];
                SC_UpdateTreasureReward? changed = ClaimRewards(pass, uid, request.GMKOFCCPLOD, updates);
                if (changed is not null)
                    updates.Add(changed);

                updates.Add(new SC_TreasureTakeReward());
                connection.SendAutoEncrypted(updates.ToArray());
            }
        }

        private SC_UpdateTreasureReward? ClaimRewards(
            TreasurePassConfig pass,
            uint uid,
            IEnumerable<PBILPEAFFIC> requestedRewards,
            List<IMessage> updates)
        {
            TreasurePassState state = GetState(uid, pass);
            Dictionary<uint, ECDNJHCNDJB> changed = [];

            foreach (PBILPEAFFIC requested in requestedRewards
                .GroupBy(reward => (reward.Id, reward.NBOBFEGFAJF))
                .Select(group => group.First()))
            {
                // The client uses enum 0 for the normal track and 1 for the advanced track.
                // A paid-track purchase is not implemented, so never grant its items by trusting this request.
                if ((int)requested.NBOBFEGFAJF != 0
                    || !pass.Rewards.TryGetValue(requested.Id, out TreasureRewardData? reward)
                    || reward.LevelId == 0
                    || reward.LevelId > state.Level
                    || !state.Rewards.TryGetValue(requested.Id, out TreasureRewardStatus? status)
                    || status.Base != (int)TreasureRewardState.Unlock)
                    continue;

                bool configuredRewardIsGrantable = reward.BaseReward.Count > 0
                    && reward.BaseReward.All(pair => pair.Key != 0 && pair.Value > 0 && pair.Value <= int.MaxValue
                        && IsKnownRewardItem(pair.Key));
                if (!configuredRewardIsGrantable)
                {
                    Log.Warning("Treasure reward {RewardId} for account {Uid} has empty or unsupported configured items.", reward.Id, uid);
                    continue;
                }

                int before = updates.Count;
                foreach ((uint itemId, uint count) in reward.BaseReward)
                    rewardService.Grant(uid, itemId, (int)count, updates);

                // Do not consume the claim if a known table-backed item still failed to produce an update.
                if (updates.Count == before)
                {
                    Log.Warning("Treasure reward {RewardId} for account {Uid} contains no grantable items.", reward.Id, uid);
                    continue;
                }

                status.Base = (int)TreasureRewardState.Received;
                changed[reward.Id] = ToProtoStatus(status);
            }

            if (changed.Count == 0)
                return null;

            accountState.Set(uid, StateKey, state);
            return new SC_UpdateTreasureReward
            {
                ONMNEMDCEIJ = pass.Main.Id,
                Rewards = { changed },
            };
        }

        private bool IsKnownRewardItem(uint itemId) =>
            tableService.GetTable<ItemData>().Any(row => row.Id == itemId)
            || tableService.GetTable<GunData>().Any(row => row.Id == itemId)
            || tableService.GetTable<GunWeaponData>().Any(row => row.Id == itemId);

        private TreasurePassConfig? GetActivePass()
        {
            TreasureMainData? main = tableService.GetTable<TreasureMainData>()
                .FirstOrDefault(row => row.Id == ActiveTreasureId);
            if (main is null || main.NPPHMPAFIPM == 0)
                return null;

            TreasureConfigData? config = tableService.GetTable<TreasureConfigData>()
                .FirstOrDefault(row => row.Id == main.NPPHMPAFIPM);
            if (config is null)
                return null;

            Dictionary<uint, TreasureRewardData> rewards = tableService.GetTable<TreasureRewardData>()
                .Where(row => config.RewardId.Contains(row.Id))
                .ToDictionary(row => row.Id);
            return new TreasurePassConfig(main, config, rewards);
        }

        private TreasurePassState GetState(uint uid, TreasurePassConfig pass)
        {
            TreasurePassState state = accountState.Get<TreasurePassState>(uid, StateKey) ?? new TreasurePassState();
            uint maxConfiguredLevel = pass.Rewards.Values.Select(row => row.LevelId).DefaultIfEmpty(1u).Max();
            state.Level = Math.Clamp(state.Level, 1, Math.Max(1, maxConfiguredLevel));

            foreach (TreasureRewardData reward in pass.Rewards.Values)
            {
                if (reward.LevelId > state.Level)
                    continue;
                if (!state.Rewards.ContainsKey(reward.Id))
                    state.Rewards[reward.Id] = new TreasureRewardStatus { Base = (int)TreasureRewardState.Unlock };
            }

            return state;
        }

        private static IAHOOMEFPND BuildProtoState(TreasurePassConfig pass, TreasurePassState state)
        {
            IAHOOMEFPND result = new()
            {
                Level = state.Level,
                Exp = state.Exp,
                IOINKEDEKND = state.AdvancedPurchased,
            };
            foreach (TreasureRewardData reward in pass.Rewards.Values)
            {
                state.Rewards.TryGetValue(reward.Id, out TreasureRewardStatus? saved);
                result.Rewards[reward.Id] = saved is null
                    ? CreateProtoStatus(reward.LevelId <= state.Level ? (int)TreasureRewardState.Unlock : (int)TreasureRewardState.None, 0)
                    : ToProtoStatus(saved);
            }
            return result;
        }

        private static ECDNJHCNDJB ToProtoStatus(TreasureRewardStatus state)
            => CreateProtoStatus(state.Base, state.Advanced);

        private static ECDNJHCNDJB CreateProtoStatus(int baseState, int advancedState)
        {
            ECDNJHCNDJB result = new();
            SetEnumField(result, 1, baseState);
            SetEnumField(result, 2, advancedState);
            return result;
        }

        private static void SetEnumField(ECDNJHCNDJB message, int fieldNumber, int value)
        {
            Google.Protobuf.Reflection.FieldDescriptor field = ECDNJHCNDJB.Descriptor.FindFieldByNumber(fieldNumber);
            object current = field.Accessor.GetValue(message);
            field.Accessor.SetValue(message, Enum.ToObject(current.GetType(), value));
        }

        private sealed record TreasurePassConfig(
            TreasureMainData Main,
            TreasureConfigData Config,
            Dictionary<uint, TreasureRewardData> Rewards);
    }

    public sealed class TreasurePassState
    {
        public uint Level { get; set; } = 1;
        public uint Exp { get; set; }
        public bool AdvancedPurchased { get; set; }
        public Dictionary<uint, TreasureRewardStatus> Rewards { get; set; } = [];
    }

    public sealed class TreasureRewardStatus
    {
        public int Base { get; set; }
        public int Advanced { get; set; }
    }

    internal enum TreasureRewardState
    {
        None = 0,
        Unlock = 1,
        Received = 2,
    }
}
