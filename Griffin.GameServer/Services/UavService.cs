using Google.Protobuf;
using Griffin.Common.Proto;
using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The drone (UAV) of the commander: arms (<c>UavArmsData</c>, 1 = id, 8 = level-cost group), their levels (<c>UavArmLevelCostData</c>: 1 = level, 2 = group,
    /// 3 = item and amount), the grade (<c>UavAdvanceData</c>: 1 = grade, 2 = gold, 3 = item and amount) and the arms put in the positions. ASSUMPTIONS
    /// (A-25): a new account owns the first arm at level 1 and has it in position 1; unlocking an arm is free; the cost of raising an arm from level L is
    /// the row of that level in its group; the cost of reaching a grade is the row of that grade; a position holds any owned arm.
    /// </summary>
    public interface IUavService : IGameService
    {
        UavRevelant Get(uint uid);

        /// <summary>Raises the grade; null (with the error) when there is no next grade or it cannot be paid.</summary>
        uint? UpGrade(uint uid, List<IMessage> messages, out HNINDAOJCKP error);

        UavArm? UnlockArm(uint uid, uint armId, out HNINDAOJCKP error);

        UavArm? LevelUp(uint uid, uint armId, List<IMessage> messages, out HNINDAOJCKP error);

        IReadOnlyList<uint>? Install(uint uid, uint position, uint armId, out HNINDAOJCKP error);
    }

    public sealed class UavService(ITableService tableService, IAccountStateService accountState, IItemUseService itemUse) : IUavService
    {
        private const string StateKey = "uav";

        public sealed class UavState
        {
            public uint Grade { get; set; } = 1;
            public List<uint> Equips { get; set; } = [];
            public Dictionary<uint, uint> Arms { get; set; } = [];
        }

        private UavState Load(uint uid)
        {
            UavState? state = accountState.Get<UavState>(uid, StateKey);
            if (state is not null)
            {
                while (state.Equips.Count < 3)
                    state.Equips.Add(0);
                return state;
            }

            uint first = tableService.GetTable<UavArmsData>().Select(r => TableFields.U(r, 1)).Where(id => id != 0).DefaultIfEmpty(0u).Min();
            state = new UavState { Equips = [first, 0, 0] };
            if (first != 0)
                state.Arms[first] = 1;
            return state;
        }

        private void Save(uint uid, UavState state) => accountState.Set(uid, StateKey, state);

        public UavRevelant Get(uint uid)
        {
            UavState state = Load(uid);
            UavRevelant reply = new() { Uav = new Uav { UavGrade = state.Grade, Details = new Uav.Types.Details { HGGOEMCAEEH = 100, HAMFJBNLCOM = 100 } } };
            reply.Uav.UavEquips.AddRange(state.Equips);
            foreach ((uint arm, uint level) in state.Arms)
            {
                UavArm proto = new() { StcId = arm, Level = level };
                reply.UavArms[arm] = proto;
                reply.Uav.Details.BFHMGPOCKAO.Add(proto);
            }

            return reply;
        }

        private IMessage? ArmRow(uint armId) => tableService.GetTable<UavArmsData>().FirstOrDefault(r => TableFields.U(r, 1) == armId);

        public uint? UpGrade(uint uid, List<IMessage> messages, out HNINDAOJCKP error)
        {
            lock (AccountLock.For(uid))
            {
                error = HNINDAOJCKP.ItemConfigNotExist;
                UavState state = Load(uid);
                IMessage? row = tableService.GetTable<UavAdvanceData>().FirstOrDefault(r => TableFields.U(r, 1) == state.Grade + 1);
                if (row is null)
                    return null;

                Dictionary<uint, int> cost = [];
                if (TableFields.U(row, 2) > 0)
                    cost[2] = (int)TableFields.U(row, 2);
                IReadOnlyList<uint> item = TableFields.List(row, 3);
                if (item.Count >= 2 && item[1] > 0)
                    cost[item[0]] = cost.GetValueOrDefault(item[0]) + (int)item[1];
                if (cost.Count > 0 && !itemUse.Spend(uid, cost, messages))
                {
                    error = HNINDAOJCKP.ItemNotEnough;
                    return null;
                }

                state.Grade++;
                Save(uid, state);
                return state.Grade;
        
            }
        }

        public UavArm? UnlockArm(uint uid, uint armId, out HNINDAOJCKP error)
        {
            lock (AccountLock.For(uid))
            {
                error = HNINDAOJCKP.ItemConfigNotExist;
                if (ArmRow(armId) is null)
                    return null;

                UavState state = Load(uid);
                state.Arms.TryAdd(armId, 1);
                Save(uid, state);
                return new UavArm { StcId = armId, Level = state.Arms[armId] };
        
            }
        }

        public UavArm? LevelUp(uint uid, uint armId, List<IMessage> messages, out HNINDAOJCKP error)
        {
            lock (AccountLock.For(uid))
            {
                error = HNINDAOJCKP.ItemConfigNotExist;
                UavState state = Load(uid);
                IMessage? arm = ArmRow(armId);
                if (arm is null || !state.Arms.TryGetValue(armId, out uint level))
                    return null;

                uint group = TableFields.U(arm, 8);
                IMessage? row = tableService.GetTable<UavArmLevelCostData>().FirstOrDefault(r => TableFields.U(r, 2) == group && TableFields.U(r, 1) == level);
                if (row is null)
                    return null;

                IReadOnlyList<uint> item = TableFields.List(row, 3);
                if (item.Count >= 2 && item[1] > 0 && !itemUse.Spend(uid, new Dictionary<uint, int> { [item[0]] = (int)item[1] }, messages))
                {
                    error = HNINDAOJCKP.ItemNotEnough;
                    return null;
                }

                state.Arms[armId] = level + 1;
                Save(uid, state);
                return new UavArm { StcId = armId, Level = level + 1 };
        
            }
        }

        public IReadOnlyList<uint>? Install(uint uid, uint position, uint armId, out HNINDAOJCKP error)
        {
            lock (AccountLock.For(uid))
            {
                error = HNINDAOJCKP.ItemConfigNotExist;
                UavState state = Load(uid);
                if (position == 0 || position > state.Equips.Count || (armId != 0 && !state.Arms.ContainsKey(armId)))
                    return null;

                for (int i = 0; i < state.Equips.Count; i++)
                    if (armId != 0 && state.Equips[i] == armId)
                        state.Equips[i] = 0;
                state.Equips[(int)position - 1] = armId;
                Save(uid, state);
                return state.Equips;
        
            }
        }
    }
}
