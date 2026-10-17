using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Serilog;

namespace Griffin.GameServer.Services
{
    public sealed class GardenState
    {
        public uint PlantId { get; set; }
        public uint Exp { get; set; }
        /// <summary>Nutrition 0..4 (4 thriving, 2-3 free growth, 1 lacking nutrition, 0 withered).</summary>
        public uint Vitality { get; set; }
        public long LastWaterDay { get; set; } = -1;
        public long LastDecayDay { get; set; }
        public long LastWaterTime { get; set; }
        public long PlantedAt { get; set; }
        public long WitheredAt { get; set; }
        public uint FertilizerUses { get; set; }
        public int RoomId { get; set; }
        public List<uint> Decorations { get; set; } = [];
        public long PointTime { get; set; }
    }

    /// <summary>
    /// The lobby garden (花朵种植, run by Helena). Rules come from the client's own help text (<c>LangPackageTableCnData</c> 22673:
    /// choose a seed, water every day, fertiliser speeds growth, a withered flower can be revived within 7 days, a flower can be
    /// dug up) and from the tables, read by field number: <c>LobbyPlantData</c> (1 = plant, 4 = [normal flower item, rare flower
    /// item]), <c>LobbyPlantDevelopData</c> (growth stages by experience: 15 each, bloom from 75), <c>LobbyPlantAccelerateData</c>
    /// (fertiliser gain), <c>LobbyPlantSpeedData</c> (nutrition 4 / 2-3 / 1 / 0) and the single <c>LobbyPlantingConfigData</c> row
    /// (2 = days for a rare flower, 4 = experience per watering, 6 = maximum experience, 7 = revive days, 8 = revive cost,
    /// 10 = fertiliser item).
    /// <para>Assumptions (nothing of this is confirmed by a real client): the response field <c>NPJLLHJBFBP</c> is the nutrition
    /// value 0..4 (it changes with watering, fertilising and reviving); one watering a day raises it by one, every missed day
    /// lowers it by one and at 0 the flower withers; the n-th fertiliser use of a plant gives <c>4 × n</c> experience (the table
    /// rows 1..22 are 4 × id); a flower harvested within the config days is the rare variant; inviting a character has no effect.
    /// The day changes at 05:00 UTC+8 (21:00 UTC).</para>
    /// </summary>
    public interface ILobbyGardenService : IGameService
    {
        HNINDAOJCKP LastError { get; }

        FOMLKMNAKEC Info(uint uid);

        FOMLKMNAKEC? Plant(uint uid, uint plantId);

        /// <summary>Waters; returns (nutrition, time) or null.</summary>
        (uint Vitality, long Time)? Water(uint uid, uint plantId);

        (uint Exp, uint Vitality)? Fertilise(uint uid, uint plantId, List<IMessage> messages);

        bool Harvest(uint uid, uint plantId, List<IMessage> messages);

        bool Dig(uint uid, uint plantId);

        uint? Rescue(uint uid, uint plantId, List<IMessage> messages);

        bool Dress(uint uid, IEnumerable<uint> decorations);

        long? SetPoint(uint uid, int roomId);
    }

    public sealed class LobbyGardenService(
        IAccountStateService accountState,
        ITableService tableService,
        IItemUseService itemUse,
        IRewardService rewardService) : ILobbyGardenService
    {
        private const string StateKey = "lobby.garden";
        private const uint MaxVitality = 4;

        public HNINDAOJCKP LastError { get; private set; }

        private T? Fail<T>(HNINDAOJCKP error)
        {
            LastError = error;
            return default;
        }

        public FOMLKMNAKEC Info(uint uid) => ToProto(Load(uid));

        public FOMLKMNAKEC? Plant(uint uid, uint plantId)
        {
            GardenState state = Load(uid);
            if (state.PlantId != 0)
                return Fail<FOMLKMNAKEC>(HNINDAOJCKP.ClientInputInvalid);

            if (!tableService.GetTable<LobbyPlantData>().Any(p => TableFields.U(p, 1) == plantId))
                return Fail<FOMLKMNAKEC>(HNINDAOJCKP.ClientInputInvalid);

            long now = Now();
            state.PlantId = plantId;
            state.Exp = 0;
            state.Vitality = MaxVitality;
            state.LastWaterDay = -1;
            state.LastDecayDay = Day(now);
            state.LastWaterTime = 0;
            state.PlantedAt = now;
            state.WitheredAt = 0;
            state.FertilizerUses = 0;
            Save(uid, state);
            return ToProto(state);
        }

        public (uint Vitality, long Time)? Water(uint uid, uint plantId)
        {
            GardenState state = Load(uid);
            if (state.PlantId == 0 || state.PlantId != plantId || Withered(state))
                return Fail<(uint, long)?>(HNINDAOJCKP.ClientInputInvalid);

            long now = Now();
            if (state.LastWaterDay == Day(now))
                return Fail<(uint, long)?>(HNINDAOJCKP.LobbyOperationTooFast);

            state.LastWaterDay = Day(now);
            state.LastWaterTime = now;
            state.Vitality = Math.Min(MaxVitality, state.Vitality + 1);
            state.Exp = Math.Min(MaxExp(), state.Exp + Config(4, 10));
            Save(uid, state);
            return (state.Vitality, now);
        }

        public (uint Exp, uint Vitality)? Fertilise(uint uid, uint plantId, List<IMessage> messages)
        {
            GardenState state = Load(uid);
            if (state.PlantId == 0 || state.PlantId != plantId || Withered(state) || state.Exp >= MaxExp())
                return Fail<(uint, uint)?>(HNINDAOJCKP.ClientInputInvalid);

            uint fertiliser = Config(10, 1610002);
            if (!itemUse.Spend(uid, new Dictionary<uint, int> { [fertiliser] = 1 }, messages))
                return Fail<(uint, uint)?>(HNINDAOJCKP.ItemNotEnough);

            uint step = Math.Min(state.FertilizerUses + 1, 22);
            uint gain = tableService.GetTable<LobbyPlantAccelerateData>().Where(r => TableFields.U(r, 1) == step).Select(r => TableFields.U(r, 2)).FirstOrDefault();
            state.FertilizerUses++;
            state.Exp = Math.Min(MaxExp(), state.Exp + gain);
            state.Vitality = Math.Min(MaxVitality, state.Vitality + 1);
            Save(uid, state);
            return (state.Exp, state.Vitality);
        }

        public bool Harvest(uint uid, uint plantId, List<IMessage> messages)
        {
            GardenState state = Load(uid);
            if (state.PlantId == 0 || state.PlantId != plantId || Withered(state) || state.Exp < BloomExp())
                return Fail<bool>(HNINDAOJCKP.ClientInputInvalid);

            IMessage? row = tableService.GetTable<LobbyPlantData>().FirstOrDefault(p => TableFields.U(p, 1) == plantId);
            IReadOnlyList<uint> flowers = row is null ? [] : TableFields.List(row, 4);
            long days = (Now() - state.PlantedAt) / 86400;
            uint flower = flowers.Count > 1 && days <= Config(2, 4) ? flowers[1] : flowers.FirstOrDefault();
            if (flower != 0)
                rewardService.Grant(uid, flower, 1, messages);

            Log.Information("Garden: account {Uid} harvested plant {Plant} after {Days} day(s): flower {Flower}.", uid, plantId, days, flower);
            Save(uid, new GardenState { RoomId = state.RoomId, Decorations = state.Decorations, PointTime = state.PointTime });
            return true;
        }

        public bool Dig(uint uid, uint plantId)
        {
            GardenState state = Load(uid);
            if (state.PlantId == 0 || state.PlantId != plantId)
                return Fail<bool>(HNINDAOJCKP.ClientInputInvalid);

            Save(uid, new GardenState { RoomId = state.RoomId, Decorations = state.Decorations, PointTime = state.PointTime });
            return true;
        }

        public uint? Rescue(uint uid, uint plantId, List<IMessage> messages)
        {
            GardenState state = Load(uid);
            long now = Now();
            if (state.PlantId == 0 || state.PlantId != plantId || !Withered(state) || now - state.WitheredAt > Config(7, 7) * 86400L)
                return Fail<uint?>(HNINDAOJCKP.ClientInputInvalid);

            Dictionary<uint, int> cost = [];
            if (tableService.GetTable<LobbyPlantingConfigData>().FirstOrDefault() is { } config)
                foreach ((uint item, uint count) in TableFields.Map(config, 8))
                    cost[item] = (int)count;

            if (!itemUse.Spend(uid, cost, messages))
                return Fail<uint?>(HNINDAOJCKP.ItemNotEnough);

            state.Vitality = 1;
            state.WitheredAt = 0;
            state.LastDecayDay = Day(now);
            Save(uid, state);
            return state.Vitality;
        }

        public bool Dress(uint uid, IEnumerable<uint> decorations)
        {
            GardenState state = Load(uid);
            state.Decorations = decorations.Distinct().ToList();
            Save(uid, state);
            return true;
        }

        public long? SetPoint(uint uid, int roomId)
        {
            GardenState state = Load(uid);
            state.RoomId = roomId;
            state.PointTime = Now();
            Save(uid, state);
            return state.PointTime;
        }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>Day number; the day changes at 21:00 UTC (05:00 UTC+8).</summary>
        private static long Day(long time) => (time + 3 * 3600) / 86400;

        private static bool Withered(GardenState state) => state.PlantId != 0 && state.Vitality == 0;

        private uint Config(int column, uint fallback) =>
            tableService.GetTable<LobbyPlantingConfigData>().FirstOrDefault() is { } config && TableFields.U(config, column) is { } value and > 0 ? value : fallback;

        private uint MaxExp() => Config(6, 90);

        /// <summary>Experience where the last growth stage (bloom) starts.</summary>
        private uint BloomExp() => MaxExp() - 15;

        /// <summary>The state with the missed days applied: each day without watering lowers nutrition by one; at 0 the flower withers.</summary>
        private GardenState Load(uint uid)
        {
            GardenState state = accountState.Get<GardenState>(uid, StateKey) ?? new GardenState();
            if (state.PlantId == 0 || state.Vitality == 0)
                return state;

            long today = Day(Now());
            long idle = today - Math.Max(state.LastDecayDay, state.LastWaterDay);
            if (idle > 1)
            {
                uint lost = (uint)Math.Min(idle - 1, MaxVitality);
                state.Vitality = state.Vitality > lost ? state.Vitality - lost : 0;
                state.LastDecayDay = today - 1;
                if (state.Vitality == 0)
                    state.WitheredAt = Now();
                Save(uid, state);
            }

            return state;
        }

        private void Save(uint uid, GardenState state) => accountState.Set(uid, StateKey, state);

        private static FOMLKMNAKEC ToProto(GardenState state)
        {
            uint stage = state.PlantId == 0 ? 0 : Math.Min(6u, state.Exp / 15 + 1);
            FOMLKMNAKEC info = new()
            {
                JJGNOPPPPIB = state.PlantId,
                NPJLLHJBFBP = state.Vitality,
                Exp = state.Exp,
                NCINIEDEDFG = stage,
                GILBGLLFNEK = Withered(state),
                HMPGFCICDPF = state.LastWaterTime,
                APCCCCBNAJC = state.PlantId != 0,
                RoomId = state.RoomId,
            };
            info.CCOCGNFFIGD.AddRange(state.Decorations);
            return info;
        }
    }
}
